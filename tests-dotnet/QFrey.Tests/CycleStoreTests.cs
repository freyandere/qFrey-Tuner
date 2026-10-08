using QFrey.Core.Contracts;
using QFrey.Core.Persistence;
using QFrey.Core.Qbittorrent;
using Xunit;

namespace QFrey.Tests;

public sealed class CycleStoreTests
{
    [Fact]
    public async Task PreviewWithoutBaselineAuthorityCanBeSavedButCannotClaimApproval()
    {
        var (store, _) = NewStore();
        var record = Record(Guid.NewGuid(), Target());
        var preview = record.Plan with { PreviewOnly = true, BaselineFingerprint = "", Approved = false, Applicable = false };
        record = record with { Plan = preview, Experiment = record.Experiment with { Plan = preview } };
        await store.SaveAsync(record);
        Assert.True((await store.ReadAsync(record.CycleId))!.Plan.PreviewOnly);
        var approved = preview with { Approved = true };
        Assert.Throws<QbittorrentException>(() => CycleStore.Validate(record with { Plan = approved, Experiment = record.Experiment with { Plan = approved } }));
    }
    [Fact]
    public async Task BaselineAuthorityCannotBeReplacedOrRemoved()
    {
        var (store, _) = NewStore();
        var record = Record(Guid.NewGuid(), Target());
        var prefs = new Dictionary<string, PreferenceValue> { ["max_connec"] = new IntegerPreference(500) };
        var hash = new string('a', 40);
        var context = new MeasurementContext(prefs, QFrey.Core.Tuning.PlanBuilder.FingerprintPreferences(prefs),
            [hash], [new TorrentRunContext(hash, "downloading", .1)]);
        record = record with { BaselineContext = context };
        await store.SaveAsync(record);
        await Assert.ThrowsAsync<QbittorrentException>(() => store.SaveAsync(record with { BaselineContext = null }));
        await Assert.ThrowsAsync<QbittorrentException>(() => store.SaveAsync(record with
        { BaselineContext = context with { SelectedTorrents = [new(hash, "downloading", .2)] } }));
        Assert.Equal(.1, (await store.ReadAsync(record.CycleId))!.BaselineContext!.SelectedTorrents[0].Progress);
    }

    [Fact]
    public async Task RoundTripsAndPaginatesTypedSummaries()
    {
        var (store, _) = NewStore();
        var target = Target();
        var first = Record(Guid.Parse("00000000-0000-0000-0000-000000000001"), target);
        var second = Record(Guid.Parse("00000000-0000-0000-0000-000000000002"), target);
        await store.SaveAsync(first);
        await store.SaveAsync(second);

        var readback = await store.ReadAsync(first.CycleId, target);
        Assert.Equal(first.CycleId, readback!.CycleId);
        Assert.Equal(500, Assert.IsType<IntegerPreference>(readback.Original["max_connec"]).Value);
        var page = await store.ListAsync(pageSize: 1);
        Assert.Single(page.Items);
        Assert.NotNull(page.NextCursor);
        var next = await store.ListAsync(page.NextCursor, 1);
        Assert.Equal(second.CycleId, Assert.Single(next.Items).CycleId);
        Assert.Null(next.NextCursor);
    }

    [Fact]
    public async Task RejectsTargetMismatchAndDoesNotReplaceOriginalBackup()
    {
        var (store, _) = NewStore();
        var record = Record(Guid.NewGuid(), Target());
        await store.SaveAsync(record);

        await Assert.ThrowsAsync<QbittorrentException>(() => store.ReadAsync(record.CycleId,
            Target() with { QbittorrentVersion = "5.2.0" }));
        await Assert.ThrowsAsync<QbittorrentException>(() => store.SaveAsync(record with { Original = new Dictionary<string, PreferenceValue>() }));
        Assert.Equal(500, Assert.IsType<IntegerPreference>((await store.ReadAsync(record.CycleId))!.Original["max_connec"]).Value);
    }

    [Fact]
    public async Task AllowsOneInitialBackupAndKeepsCycleIdentityImmutableThereafter()
    {
        var (store, _) = NewStore();
        var draft = Record(Guid.NewGuid(), Target());
        var empty = new Dictionary<string, PreferenceValue>();
        var emptyPlan = draft.Plan with { Original = empty, Proposed = empty };
        draft = draft with
        {
            Plan = emptyPlan,
            Original = empty,
            IntendedApplied = empty,
            ObservedReadback = empty,
            Experiment = draft.Experiment with { Plan = emptyPlan }
        };
        await store.SaveAsync(draft);

        var original = new Dictionary<string, PreferenceValue> { ["max_connec"] = new IntegerPreference(500) };
        var intended = new Dictionary<string, PreferenceValue> { ["max_connec"] = new IntegerPreference(600) };
        var applyingPlan = emptyPlan with { Original = original, Proposed = intended };
        var applying = draft with
        {
            UpdatedUtc = draft.UpdatedUtc.AddSeconds(1),
            Plan = applyingPlan,
            Original = original,
            IntendedApplied = intended,
            OperationStage = "applying",
            ApplyStatus = ApplyStatus.Pending,
            Experiment = draft.Experiment with { Plan = applyingPlan }
        };
        await store.SaveAsync(applying);
        Assert.Equal(500, Assert.IsType<IntegerPreference>((await store.ReadAsync(draft.CycleId))!.Original["max_connec"]).Value);

        var changedBackup = new Dictionary<string, PreferenceValue> { ["max_connec"] = new IntegerPreference(501) };
        var changedPlan = applyingPlan with { Original = changedBackup };
        await Assert.ThrowsAsync<QbittorrentException>(() => store.SaveAsync(applying with
        {
            UpdatedUtc = applying.UpdatedUtc.AddSeconds(1),
            Original = changedBackup,
            Plan = changedPlan,
            Experiment = applying.Experiment with { Plan = changedPlan }
        }));
        await Assert.ThrowsAsync<QbittorrentException>(() => store.SaveAsync(applying with
        {
            UpdatedUtc = applying.UpdatedUtc.AddSeconds(1),
            CreatedUtc = applying.CreatedUtc.AddSeconds(1)
        }));
        await Assert.ThrowsAsync<QbittorrentException>(() => store.SaveAsync(applying with
        {
            UpdatedUtc = applying.UpdatedUtc.AddSeconds(1),
            Target = applying.Target with { QbittorrentVersion = "5.2.0" }
        }));

        var persisted = (await store.ReadAsync(draft.CycleId))!;
        Assert.Equal(draft.CycleId, persisted.CycleId);
        Assert.Equal(draft.CreatedUtc, persisted.CreatedUtc);
        Assert.Equal(draft.Target, persisted.Target);
        Assert.Equal(500, Assert.IsType<IntegerPreference>(persisted.Original["max_connec"]).Value);
    }

    [Fact]
    public async Task RejectsFutureSchemaAndCorruptRecords()
    {
        var (store, root) = NewStore();
        var futureId = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(root, futureId.ToString("N") + ".json"), "{\"schemaVersion\":3}");
        await Assert.ThrowsAsync<QbittorrentException>(() => store.ReadAsync(futureId));

        var corruptId = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(root, corruptId.ToString("N") + ".json"), "{\"schemaVersion\":2,");
        await Assert.ThrowsAsync<QbittorrentException>(() => store.ReadAsync(corruptId));
    }

    [Fact]
    public void RejectsNonFiniteValuesAndNonAllowlistedPreferences()
    {
        var valid = Record(Guid.NewGuid(), Target());
        Assert.Throws<QbittorrentException>(() => CycleStore.Validate(valid with
        {
            Original = new Dictionary<string, PreferenceValue> { ["web_ui_password"] = new StringPreference("secret") }
        }));
        Assert.Throws<QbittorrentException>(() => CycleStore.Validate(valid with
        {
            BaselineSamples = [new MeasurementSample(double.NaN, MeasurementStatus.Valid, [],
                new FreshReading(1, DateTimeOffset.UtcNow), new FreshReading(1, DateTimeOffset.UtcNow),
                new FreshReading(1, DateTimeOffset.UtcNow), new FreshReading(1, DateTimeOffset.UtcNow),
                new FreshReading(0, DateTimeOffset.UtcNow), new FreshReading(0, DateTimeOffset.UtcNow), null, null, null)]
        }));
    }

    private static (CycleStore Store, string Root) NewStore()
    {
        var root = Path.Combine(ProjectRoot(), ".cache", "tests", "cycle-store", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return (new CycleStore(root), root);
    }

    private static string ProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pyproject.toml"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Project root not found.");
    }

    private static TargetIdentity Target() => new("http://127.0.0.1:8080", "5.1.0", "2.11.0", "2.0.11");

    internal static CycleRecord Record(Guid id, TargetIdentity target)
    {
        var now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        var preferences = new Dictionary<string, PreferenceValue> { ["max_connec"] = new IntegerPreference(500) };
        var plan = new Plan(Guid.NewGuid(), Guid.NewGuid(), 1, now, new string('a', 64), new string('b', 64), false, false, false,
            [], [], [], preferences, preferences);
        var experiment = new ExperimentSummary(id, null, null, null, null, plan, []);
        return new CycleRecord(2, "analysis-1", id, now, now, target, new DraftInputs(
            new NetworkInputs(500, 100, ConnectionType.Fiber, false, "", false, InputSource.Manual, InputSource.Manual),
            new HardwareInputs(StorageType.SsdSata, 16, 8, false, 8, InputSource.Manual),
            new UsageInputs(TrackerType.Public, UserRole.Leecher, EnvironmentProfile.System), null), plan,
            preferences, preferences, preferences, "draft", ApplyStatus.NotApplied, experiment, [], []);
    }
}
