using System.Text.Json;
using QFrey.Core.Contracts;
using Xunit;

namespace QFrey.Tests;

public class ProductProtocolTests
{
    private static readonly string FixtureDirectory = FindFixtureDirectory();
    private static readonly Guid SessionId = Guid.Parse("00000000-0000-0000-0000-000000000201");
    private static readonly Guid ObjectId = Guid.Parse("00000000-0000-0000-0000-000000000401");

    public static IEnumerable<object[]> CatalogCommands()
    {
        var inputs = new DraftInputs(
            new NetworkInputs(100, 20, ConnectionType.Fiber, false, "", false, InputSource.Manual, InputSource.Manual),
            new HardwareInputs(StorageType.SsdSata, 16, 8, false, 8, InputSource.Manual),
            new UsageInputs(TrackerType.Private, UserRole.Seeder, EnvironmentProfile.System), 55000);
        var workload = new WorkloadReference(ObjectId, WorkloadKind.Existing, [new string('a', 40)]);
        var cycleId = Guid.Parse("00000000-0000-0000-0000-000000000601");
        var rows = new (string Name, object Payload, bool HasSession)[]
        {
            ("Initialize", new InitializePayload(Protocol.Version), false),
            ("SetUiPreferences", new SetUiPreferencesPayload("en-US", ThemePreference.Dark), false),
            ("Connect", new ConnectPayload("https://qb.example.test", new BypassAuthentication()), false),
            ("Disconnect", new EmptyPayload(), true),
            ("DetectLocalHardware", new DetectHardwarePayload(null), true),
            ("RunNetworkTest", new NetworkTestPayload("confirm"), true),
            ("BuildPlan", new BuildPlanPayload(inputs, []), true),
            ("AcceptPlan", new AcceptPlanPayload(ObjectId, 4), true),
            ("StartMeasurement", new StartMeasurementPayload(MeasurementKind.Baseline, workload), true),
            ("CancelOperation", new CancelOperationPayload(ObjectId), true),
            ("PrepareWorkload", new PrepareWorkloadPayload("catalog-1", "D:/downloads/qfrey-test", "approve"), true),
            ("StopOwnedWorkload", new StopWorkloadPayload(ObjectId, "confirm"), true),
            ("DeleteOwnedWorkload", new DeleteWorkloadPayload(ObjectId, true, "confirm"), true),
            ("ApplyPlan", new ApplyPlanPayload(ObjectId, 4, "confirm"), true),
            ("Rollback", new RollbackPayload(cycleId, 4, "confirm"), true),
            ("RestoreLegacyBackup", new RestoreBackupPayload("selection", "confirm"), true),
            ("KeepChanges", new KeepChangesPayload(cycleId, ApplyStatus.Verified), true),
            ("ListHistory", new ListHistoryPayload(null, 20), false),
            ("ReadCycle", new ReadCyclePayload(cycleId), false),
            ("ExportReport", new ExportReportPayload(cycleId, ReportFormat.Json, "destination", Locale.EnUs), false),
            ("StartTarget", new LifecyclePayload("confirm"), true),
            ("StopTarget", new LifecyclePayload("confirm"), true),
            ("RestartTarget", new LifecyclePayload("confirm"), true),
            ("RequestConfirmation", new RequestConfirmationPayload("ApplyPlan", ObjectId, null, null), true),
            ("SelectNativeFile", new SelectFilePayload("exportJson"), false)
        };
        foreach (var (name, payload, hasSession) in rows)
            yield return [name, payload, hasSession];
    }

    [Theory]
    [MemberData(nameof(CatalogCommands))]
    public void EveryCatalogCommandAcceptsItsTypedPayload(string name, object payload, bool hasSession)
    {
        var envelope = new
        {
            protocolVersion = Protocol.Version,
            requestId = ObjectId,
            command = name,
            targetSessionId = hasSession ? SessionId : (Guid?)null,
            expectedRevision = hasSession ? 4L : (long?)null,
            payload = JsonSerializer.SerializeToElement(payload, payload.GetType(), Protocol.Json)
        };
        var parsed = Protocol.Parse(JsonSerializer.Serialize(envelope, Protocol.Json));
        Assert.Equal(name, parsed.Command);
        Assert.IsType(payload.GetType(), CommandPayloads.Read(parsed));
    }

    [Theory]
    [InlineData("product-command-build-plan.json", typeof(BuildPlanPayload))]
    [InlineData("product-command-cancel.json", typeof(CancelOperationPayload))]
    [InlineData("product-command-connect.json", typeof(ConnectPayload))]
    public void TypedCommandFixturesParse(string file, Type expectedPayload)
    {
        var command = Protocol.Parse(Read(file));
        Assert.IsType(expectedPayload, CommandPayloads.Read(command));
    }

    [Theory]
    [InlineData("product-telemetry-normal.json", typeof(FreshReading))]
    [InlineData("product-telemetry-stale.json", typeof(StaleReading))]
    public void TelemetryFixturesPreserveFreshness(string file, Type expectedReading)
    {
        var metric = JsonSerializer.Deserialize<LiveMetric>(Read(file), Protocol.Json)!;
        Assert.IsType(expectedReading, metric.Reading);
    }

    [Fact]
    public void PlanFixtureRoundTrips()
    {
        var plan = JsonSerializer.Deserialize<Plan>(Read("product-plan.json"), Protocol.Json)!;
        Assert.True(plan.PreviewOnly);
        Assert.False(plan.Applicable);
        Assert.IsType<IntegerPreference>(plan.Proposed["listen_port"]);
        Assert.Equal(JsonSerializer.Serialize(plan, Protocol.Json), JsonSerializer.Serialize(JsonSerializer.Deserialize<Plan>(JsonSerializer.Serialize(plan, Protocol.Json), Protocol.Json), Protocol.Json));
    }

    [Fact]
    public void AcceptedOperationFixtureIsTyped() =>
        Assert.NotEqual(Guid.Empty, JsonSerializer.Deserialize<AcceptedOperation>(Read("product-accepted-operation.json"), Protocol.Json)!.OperationId);

    [Theory]
    [InlineData("product-applied-verified.json", ExperimentPhase.AppliedVerified, ApplyStatus.Verified)]
    [InlineData("product-unverified.json", ExperimentPhase.RecoveryRequired, ApplyStatus.Unverified)]
    public void SnapshotFixturesKeepApplyState(string file, ExperimentPhase phase, ApplyStatus status)
    {
        var snapshot = Protocol.ParseSnapshot(Read(file));
        Assert.Equal(phase, snapshot.Phase);
        Assert.Equal(status, snapshot.ApplyStatus);
    }

    [Fact]
    public void CancelledAndHistoricalFixturesRemainHistorical()
    {
        var cancelled = JsonSerializer.Deserialize<MeasurementSummary>(Read("product-cancelled-measurement.json"), Protocol.Json)!;
        Assert.Equal(MeasurementStatus.Cancelled, cancelled.Status);

        var historical = JsonSerializer.Deserialize<ResultCard>(Read("product-historical-result.json"), Protocol.Json)!;
        Assert.Equal(ResultContext.Historical, historical.Context);
        var content = Assert.IsType<ComparisonContent>(historical.Content);
        Assert.IsType<ValidHistoricalValue>(content.Comparison.Before);
        Assert.IsType<NotMeasuredValue>(content.Comparison.After);
    }

    [Theory]
    [InlineData("product-invalid-enum.json")]
    [InlineData("product-invalid-missing-required.json")]
    [InlineData("product-invalid-null-required.json")]
    [InlineData("product-invalid-bool-as-int.json")]
    [InlineData("product-invalid-unknown-mutation-field.json")]
    [InlineData("product-invalid-missing-session.json")]
    [InlineData("product-invalid-nan.json")]
    [InlineData("product-invalid-protocol-version.json")]
    [InlineData("product-invalid-null-selection.json")]
    [InlineData("product-invalid-null-preference-map-value.json")]
    public void InvalidCommandFixturesAreRejected(string file) => Assert.ThrowsAny<JsonException>(() => Protocol.Parse(Read(file)));

    [Fact]
    public void OversizeCommandIsRejected() =>
        Assert.Throws<JsonException>(() => Protocol.Parse(Read("product-command-cancel.json") + new string(' ', Protocol.MaxCommandBytes)));

    [Fact]
    public void AuthenticationAndConnectFormattingRedactSecrets()
    {
        var connect = JsonSerializer.Deserialize<ConnectPayload>(Payload(Read("product-command-connect.json")), Protocol.Json)!;
        Assert.DoesNotContain("never-store-this-password", connect.Auth.ToString());
        Assert.DoesNotContain("never-store-this-password", connect.ToString());
        Assert.DoesNotContain("never-store-this-api-key", new ApiKeyAuthentication("never-store-this-api-key").ToString());
        var snapshot = Protocol.ParseSnapshot(Read("product-applied-verified.json"));
        var snapshotJson = JsonSerializer.Serialize(snapshot, Protocol.Json);
        Assert.DoesNotContain("never-store-this-password", snapshotJson);
        Assert.DoesNotContain("never-store-this-api-key", snapshotJson);
    }

    [Theory]
    [InlineData("product-snapshot-invalid-metric-enum.json")]
    [InlineData("product-snapshot-invalid-bool-number.json")]
    [InlineData("product-snapshot-invalid-overflow-number.json")]
    [InlineData("product-snapshot-invalid-non-utc.json")]
    [InlineData("product-snapshot-invalid-historical-non-utc.json")]
    [InlineData("product-snapshot-invalid-protocol-version.json")]
    public void InvalidSnapshotsAreRejected(string file) => Assert.ThrowsAny<JsonException>(() => Protocol.ParseSnapshot(Read(file)));

    [Fact]
    public void SnapshotAllowsUnknownOptionalFields() =>
        Assert.Equal(Protocol.Version, Protocol.ParseSnapshot(Read("product-snapshot-unknown-addition.json")).ProtocolVersion);

    [Fact]
    public void RestoreReviewPreservesReadonlyValuesAndRejectsUnknownKeys()
    {
        var snapshot = Protocol.ParseSnapshot(Read("product-applied-verified.json"));
        var review = new QFrey.Core.Persistence.RestoreReview(true, true, true, false, new string('a', 64), [],
            [new("listen_port", new IntegerPreference(55000), new IntegerPreference(55001), new IntegerPreference(55001),
                QFrey.Core.Persistence.RestoreKeyDisposition.RestoreRequired)]);
        var restore = new RestorePreview("native-selection", "backup.json", ObjectId, review);
        var parsed = Protocol.ParseSnapshot(JsonSerializer.Serialize(snapshot with { Restore = restore }, Protocol.Json));
        Assert.Equal(55000, Assert.IsType<IntegerPreference>(Assert.Single(parsed.Restore!.Review.Differences).Original).Value);
        var unsafeReview = review with { Differences = [new("web_ui_password", new StringPreference("redacted"), null, null,
            QFrey.Core.Persistence.RestoreKeyDisposition.RestoreRequired)] };
        Assert.Throws<JsonException>(() => Protocol.ParseSnapshot(JsonSerializer.Serialize(snapshot with {
            Restore = restore with { Review = unsafeReview } }, Protocol.Json)));
    }

    [Theory]
    [InlineData("product-snapshot-normal-telemetry.json", typeof(FreshReading))]
    [InlineData("product-snapshot-stale-telemetry.json", typeof(StaleReading))]
    public void SnapshotParserPreservesTelemetryFreshness(string file, Type expectedReading)
    {
        var snapshot = Protocol.ParseSnapshot(Read(file));
        Assert.IsType(expectedReading, Assert.Single(snapshot.Metrics).Reading);
    }

    [Fact]
    public void OversizeSnapshotIsRejected() =>
        Assert.Throws<JsonException>(() => Protocol.ParseSnapshot(Read("product-applied-verified.json") + new string(' ', Protocol.MaxSnapshotBytes)));

    [Fact]
    public void CommandEnvelopeFormattingRedactsPayload()
    {
        var envelope = Protocol.Parse(Read("product-command-connect.json"));
        Assert.DoesNotContain("never-store-this-password", envelope.ToString());
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(FixtureDirectory, file));

    private static string Payload(string envelope) =>
        JsonDocument.Parse(envelope).RootElement.GetProperty("payload").GetRawText();

    private static string FindFixtureDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests-contract", "fixtures", "protocol");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("Could not locate tests-contract/fixtures/protocol from the test output directory.");
    }
}
