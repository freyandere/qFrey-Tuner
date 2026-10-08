using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Tuning;
using Xunit;

namespace QFrey.Tests;

public sealed class PlanBuilderTests
{
    private static readonly Guid SessionId = Guid.Parse("0ec23a9c-426d-4c4c-9d25-8b3cbdf1821f");
    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly TargetInterface[] Interfaces = [new("wg0", "wg0"), new("eth0", "Ethernet")];

    [Theory]
    [InlineData("1.2.19", 1)]
    [InlineData("2.0.11", 2)]
    public void BuildsCanonicalDiffWithStableFingerprintAndUnapprovedRevision(string version, int major)
    {
        var current = Current(version, major);
        current["up_limit"] = new IntegerPreference(1234);
        var plan = Build(Inputs(), current, major, current, []);
        var again = Build(Inputs(), current, major, current, []);

        Assert.Equal(new IntegerPreference(1234), plan.Original["up_limit"]);
        Assert.Equal(new IntegerPreference(9999360), plan.Proposed["up_limit"]);
        Assert.Equal(plan.InputsFingerprint, again.InputsFingerprint);
        Assert.Equal(plan.BaselineFingerprint, again.BaselineFingerprint);
        Assert.Equal("recommendation-up_limit", plan.Recommendations.Single(r => r.ApiKey == "up_limit").Id);
        Assert.False(plan.Approved);
        Assert.True(plan.Applicable);
    }

    [Fact]
    public void MissingOptionalIsVisibleButMissingRequiredBlocksApply()
    {
        var optional = Current("2.0.11", 2);
        optional["up_limit"] = new IntegerPreference(1234);
        var optionalPlan = Build(Inputs(), optional, 2, optional, []);
        Assert.Contains(optionalPlan.Omissions, omission => omission.ApiKey == "disk_cache" && !omission.Required);
        Assert.Equal(SupportStatus.MissingOptional, optionalPlan.Recommendations.Single(r => r.ApiKey == "disk_cache").SupportStatus);
        Assert.True(optionalPlan.Applicable);

        optional.Remove("dht");
        var requiredPlan = Build(Inputs(), optional, 2, optional, []);
        Assert.Contains(requiredPlan.Omissions, omission => omission.ApiKey == "dht" && omission.Required);
        Assert.Equal(SupportStatus.MissingRequired, requiredPlan.Recommendations.Single(r => r.ApiKey == "dht").SupportStatus);
        Assert.False(requiredPlan.Applicable);

        var wrongType = Current("2.0.11", 2);
        wrongType["dht"] = new IntegerPreference(1);
        var incompatiblePlan = Build(Inputs(), wrongType, 2, wrongType, []);
        Assert.Contains(incompatiblePlan.Omissions, omission => omission.ApiKey == "dht" && omission.Required
            && omission.ReasonCode == "SETTING_TYPE_INCOMPATIBLE");
        Assert.Equal(SupportStatus.Incompatible, incompatiblePlan.Recommendations.Single(r => r.ApiKey == "dht").SupportStatus);
        Assert.False(incompatiblePlan.Applicable);
    }

    [Fact]
    public void PortSelectionIsAtomicAndInvalidOverrideIsRejected()
    {
        var current = Current("1.2.19", 1);
        var inputs = Inputs() with { ProposedPort = 55001 };
        var excluded = Build(inputs, current, 1, current, [new PlanSelection("port", false, new Dictionary<string, PreferenceValue>())]);
        Assert.DoesNotContain("listen_port", excluded.Proposed.Keys);
        Assert.DoesNotContain("random_port", excluded.Proposed.Keys);

        var bad = new PlanSelection("port", true, new Dictionary<string, PreferenceValue>
        { ["listen_port"] = new IntegerPreference(80) });
        Assert.Equal(ErrorCodes.InvalidOverride, Assert.Throws<QbittorrentException>(() => Build(inputs, current, 1, current, [bad])).Code);

        current.Remove("random_port");
        var partial = Build(inputs, current, 1, current, []);
        Assert.Contains("PORT_GROUP_UNAVAILABLE", partial.BlockReasonCodes);
        Assert.False(partial.Applicable);
        var excludedPartial = Build(inputs, current, 1, current,
            [new PlanSelection("port", false, new Dictionary<string, PreferenceValue>())]);
        Assert.DoesNotContain("PORT_GROUP_UNAVAILABLE", excludedPartial.BlockReasonCodes);
        Assert.DoesNotContain(excludedPartial.Omissions, omission => (omission.ApiKey is "listen_port" or "random_port") && omission.Required);
    }

    [Fact]
    public void VPNMustResolveUniquelyAndTypedOverridesStayWithinValidatedBounds()
    {
        var current = Current("1.2.19", 1);
        var ambiguous = Build(Inputs(), current, 1, current,
            [new PlanSelection("current_network_interface", true, new Dictionary<string, PreferenceValue>())],
            [new("wg0", "wg0"), new("other", "wg0")]);
        Assert.Contains("VPN_INTERFACE_AMBIGUOUS", ambiguous.BlockReasonCodes);
        Assert.False(ambiguous.Applicable);

        var changedBindingInputs = Inputs() with { Network = Inputs().Network with { VpnInterface = "wg1" } };
        var deselected = Build(changedBindingInputs, current, 1, current,
            [new PlanSelection("current_network_interface", false, new Dictionary<string, PreferenceValue>())],
            [new("wg1", "wg1")]);
        Assert.Contains("VPN_BINDING_DESELECTED", deselected.BlockReasonCodes);
        Assert.False(deselected.Applicable);

        var badType = new PlanSelection("up_limit", true, new Dictionary<string, PreferenceValue>
        { ["up_limit"] = new BooleanPreference(false) });
        Assert.Equal(ErrorCodes.InvalidOverride, Assert.Throws<QbittorrentException>(() =>
            Build(Inputs(), current, 1, current, [badType])).Code);

        var invalidBufferPair = new PlanSelection("send_buffer_low_watermark", true,
            new Dictionary<string, PreferenceValue> { ["send_buffer_low_watermark"] = new IntegerPreference(600) });
        Assert.Equal(ErrorCodes.InvalidOverride, Assert.Throws<QbittorrentException>(() =>
            Build(Inputs(), current, 1, current, [invalidBufferPair])).Code);

        var duplicateSelections = new[]
        {
            new PlanSelection("up_limit", true, new Dictionary<string, PreferenceValue>()),
            new PlanSelection("up_limit", false, new Dictionary<string, PreferenceValue>())
        };
        Assert.Equal(ErrorCodes.InvalidOverride, Assert.Throws<QbittorrentException>(() => Build(Inputs(), current, 1, current, duplicateSelections)).Code);
        Assert.Equal(ErrorCodes.InvalidOverride, Assert.Throws<QbittorrentException>(() => Build(Inputs(), current, 1, current,
            [new PlanSelection("unknown", true, new Dictionary<string, PreferenceValue>())])).Code);
    }

    [Fact]
    public void EmptyDiffCannotBeApprovedAndApprovalDoesNotMutatePlan()
    {
        var current = Current("1.2.19", 1);
        var recommendations = PreferenceMapping.Map(Calculator.Calculate(Inputs()), current, 1).Values;
        var unchanged = Build(Inputs(), recommendations, 1, recommendations, []);
        Assert.Contains("NO_EFFECTIVE_CHANGES", unchanged.BlockReasonCodes);
        Assert.False(unchanged.Applicable);
        Assert.Throws<QbittorrentException>(() => PlanBuilder.Approve(unchanged, unchanged.Id, unchanged.Revision));

        current["up_limit"] = new IntegerPreference(1234);
        var plan = Build(Inputs(), current, 1, current, []);
        var approved = PlanBuilder.Approve(plan, plan.Id, plan.Revision);
        Assert.True(approved.Approved);
        Assert.False(plan.Approved);
        var editedRevision = Build(Inputs(), current, 1, current, [new PlanSelection("up_limit", false, new Dictionary<string, PreferenceValue>())]);
        Assert.False(editedRevision.Approved);
        Assert.NotEqual(approved.Id, editedRevision.Id);
    }

    private static Plan Build(DraftInputs inputs, IReadOnlyDictionary<string, PreferenceValue> current, int major,
        IReadOnlyDictionary<string, PreferenceValue>? baseline, PlanSelection[] selections, IReadOnlyList<TargetInterface>? interfaces = null) =>
        PlanBuilder.Build(SessionId, 3, Created, inputs, current, major, interfaces ?? Interfaces, baseline, selections);

    private static DraftInputs Inputs() => new(
        new(500, 100, ConnectionType.Fiber, true, "wg0", true, InputSource.Manual, InputSource.Manual),
        new(StorageType.Nvme, 16, 8, false, 0, InputSource.Manual),
        new(TrackerType.Public, UserRole.Leecher, EnvironmentProfile.System), 55000);

    private static Dictionary<string, PreferenceValue> Current(string version, int major)
    {
        var fixtureDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        while (fixtureDirectory is not null && !Directory.Exists(Path.Combine(fixtureDirectory.FullName, "tests-contract"))) fixtureDirectory = fixtureDirectory.Parent;
        Assert.NotNull(fixtureDirectory);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureDirectory!.FullName, "tests-contract/fixtures/legacy/settings-payloads.json")));
        var payload = doc.RootElement.GetProperty(version).GetProperty("payload");
        return Compatibility.ReadPreferences(payload, major).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }
}
