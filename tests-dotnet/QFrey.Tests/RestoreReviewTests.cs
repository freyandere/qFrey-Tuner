using QFrey.Core.Contracts;
using QFrey.Core.Persistence;
using Xunit;

namespace QFrey.Tests;

public sealed class RestoreReviewTests
{
    private static readonly TargetIdentity Target = new("http://127.0.0.1:8080", "5.1.0", "2.11.0", "2.0.11");

    [Fact]
    public void ExactTargetAndPartialAppliedStateAllowOnlyKnownChangedKeys()
    {
        var original = Values(("max_connec", 500), ("max_uploads", 40));
        var intended = Values(("max_connec", 800), ("max_uploads", 60));
        var live = Values(("max_connec", 800), ("max_uploads", 40));

        var review = RestoreReviewBuilder.Create(Cycle(original, intended), Target, live);

        Assert.True(review.TargetMatches);
        Assert.True(review.CanRestore);
        Assert.False(review.AlreadyOriginal);
        Assert.Equal(RestoreKeyDisposition.RestoreRequired, review.Differences.Single(item => item.Key == "max_connec").Disposition);
        Assert.Equal(RestoreKeyDisposition.AlreadyOriginal, review.Differences.Single(item => item.Key == "max_uploads").Disposition);
        Assert.Equal(64, review.Fingerprint.Length);
        Assert.Throws<NotSupportedException>(() => ((IList<RestorePreferenceDiff>)review.Differences).Add(
            new("dht", new BooleanPreference(true), null, null, RestoreKeyDisposition.Conflict)));
    }

    [Fact]
    public void EndpointAndVersionMismatchesAreNeverRestorable()
    {
        var original = Values(("max_connec", 500));
        var intended = Values(("max_connec", 800));
        var backup = Cycle(original, intended);

        foreach (var liveTarget in new[]
        {
            Target with { Endpoint = "http://127.0.0.1:8081" },
            Target with { QbittorrentVersion = "5.2.0" },
            Target with { ApiVersion = "2.12.0" },
            Target with { LibtorrentVersion = "2.1.0" }
        })
        {
            var review = RestoreReviewBuilder.Create(backup, liveTarget, intended);
            Assert.False(review.TargetMatches);
            Assert.False(review.CanRestore);
            Assert.Contains("TARGET_MISMATCH", review.BlockReasonCodes);
        }
    }

    [Fact]
    public void ThirdPartyDriftIsAnExplicitConflictAndNoBypassIsOffered()
    {
        var review = RestoreReviewBuilder.Create(Cycle(Values(("max_connec", 500)), Values(("max_connec", 800))),
            Target, Values(("max_connec", 650)));

        Assert.False(review.CanRestore);
        Assert.Equal(RestoreKeyDisposition.Conflict, Assert.Single(review.Differences).Disposition);
        Assert.Contains("EXTERNAL_DRIFT", review.BlockReasonCodes);
    }

    [Fact]
    public void MatchingFutureVersionsCannotAuthorizeRestore()
    {
        var future = Target with { QbittorrentVersion = "6.0.0" };
        var backup = Legacy(Values(("max_connec", 500)), Values(("max_connec", 800))) with { Target = future };
        var review = RestoreReviewBuilder.Create(backup, future, Values(("max_connec", 800)));
        Assert.False(review.TargetMatches);
        Assert.False(review.CanRestore);
        Assert.Contains("INVALID_BACKUP_TARGET", review.BlockReasonCodes);
    }

    [Fact]
    public void MissingIntendedValueAndMissingLiveKeyDenyRestore()
    {
        var original = Values(("max_connec", 500), ("max_uploads", 40));
        var backup = Legacy(original, Values(("max_connec", 800)));
        var missingIntended = RestoreReviewBuilder.Create(backup, Target, Values(("max_connec", 800), ("max_uploads", 55)));
        Assert.False(missingIntended.CanRestore);
        Assert.Equal(RestoreKeyDisposition.IntendedUnknown, missingIntended.Differences.Single(item => item.Key == "max_uploads").Disposition);
        Assert.Contains("INTENDED_KEY_MISSING", missingIntended.BlockReasonCodes);

        var missingLive = RestoreReviewBuilder.Create(Cycle(Values(("max_connec", 500)), Values(("max_connec", 800))),
            Target, Values(("max_uploads", 40)));
        Assert.False(missingLive.CanRestore);
        Assert.Equal(RestoreKeyDisposition.MissingLiveValue, Assert.Single(missingLive.Differences).Disposition);
    }

    [Fact]
    public void UnknownKeysAndInvalidValuesAreBlockedWithoutEchoingUntrustedValues()
    {
        var secret = "do-not-copy-this-value";
        var unsafeBackup = Values(("max_connec", 500));
        unsafeBackup["web_ui_password"] = new StringPreference(secret);
        var unknownKey = Legacy(unsafeBackup, Values(("max_connec", 800)));
        var unknownReview = RestoreReviewBuilder.Create(unknownKey, Target, Values(("max_connec", 800)));
        Assert.False(unknownReview.CanRestore);
        Assert.Contains("INVALID_BACKUP_KEY", unknownReview.BlockReasonCodes);
        Assert.DoesNotContain(unknownReview.Differences, item => item.Key == "web_ui_password");
        Assert.DoesNotContain(unknownReview.Differences, item => item.Original?.ToString()?.Contains(secret, StringComparison.Ordinal) == true);

        var invalidType = new Dictionary<string, PreferenceValue> { ["max_connec"] = new UnsupportedPreference() };
        var invalidReview = RestoreReviewBuilder.Create(Legacy(invalidType, Values(("max_connec", 800))), Target,
            Values(("max_connec", 800)));
        Assert.False(invalidReview.CanRestore);
        Assert.Equal(RestoreKeyDisposition.InvalidBackupValue, Assert.Single(invalidReview.Differences).Disposition);
        Assert.Contains("INVALID_BACKUP_VALUE", invalidReview.BlockReasonCodes);

        var invalidIntended = new Dictionary<string, PreferenceValue> { ["max_connec"] = new UnsupportedPreference() };
        var invalidIntendedReview = RestoreReviewBuilder.Create(Legacy(Values(("max_connec", 500)), invalidIntended), Target,
            Values(("max_connec", 800)));
        Assert.False(invalidIntendedReview.CanRestore);
        Assert.Equal(RestoreKeyDisposition.InvalidIntendedValue, Assert.Single(invalidIntendedReview.Differences).Disposition);
        Assert.Contains("INVALID_INTENDED_VALUE", invalidIntendedReview.BlockReasonCodes);

        var invalidLive = RestoreReviewBuilder.Create(Cycle(Values(("max_connec", 500)), Values(("max_connec", 800))), Target,
            new Dictionary<string, PreferenceValue> { ["max_connec"] = new BooleanPreference(true) });
        Assert.False(invalidLive.CanRestore);
        Assert.Equal(RestoreKeyDisposition.InvalidLiveValue, Assert.Single(invalidLive.Differences).Disposition);
    }

    [Fact]
    public void LegacyWithoutIntendedValuesRemainsDiffOnlyAndAlreadyOriginalNeedsNoWrite()
    {
        var original = Values(("max_connec", 500));
        var legacy = Legacy(original, null);
        var changedLive = Values(("max_connec", 800));
        var diff = RestoreReviewBuilder.Create(legacy, Target, changedLive);

        Assert.True(diff.IsLegacy);
        Assert.False(diff.CanRestore);
        Assert.Equal(RestoreKeyDisposition.IntendedUnknown, Assert.Single(diff.Differences).Disposition);
        Assert.Contains("LEGACY_INTENDED_UNAVAILABLE", diff.BlockReasonCodes);
        var legacyAlreadyOriginal = RestoreReviewBuilder.Create(legacy, Target, original);
        Assert.True(legacyAlreadyOriginal.AlreadyOriginal);
        Assert.False(legacyAlreadyOriginal.CanRestore);

        var unchanged = RestoreReviewBuilder.Create(Cycle(original, Values(("max_connec", 800))), Target, original);
        Assert.True(unchanged.AlreadyOriginal);
        Assert.False(unchanged.CanRestore);
        Assert.Equal(RestoreKeyDisposition.AlreadyOriginal, Assert.Single(unchanged.Differences).Disposition);
    }

    [Fact]
    public void FingerprintIsStableForMapOrderAndChangesWithObservedValues()
    {
        var id = Guid.NewGuid();
        var original = Values(("max_connec", 500), ("max_uploads", 40));
        var intended = Values(("max_connec", 800), ("max_uploads", 60));
        var live = Values(("max_connec", 800), ("max_uploads", 40));
        var first = RestoreReviewBuilder.Create(Cycle(original, intended, id), Target, live);
        var reordered = RestoreReviewBuilder.Create(Cycle(Reverse(original), Reverse(intended), id), Target, Reverse(live));
        var changed = RestoreReviewBuilder.Create(Cycle(original, intended, id), Target, Values(("max_connec", 801), ("max_uploads", 40)));

        Assert.Equal(first.Fingerprint, reordered.Fingerprint);
        Assert.NotEqual(first.Fingerprint, changed.Fingerprint);
    }

    private static CycleRecord Cycle(IReadOnlyDictionary<string, PreferenceValue> original,
        IReadOnlyDictionary<string, PreferenceValue> intended, Guid? cycleId = null)
    {
        var id = cycleId ?? Guid.NewGuid();
        var baseRecord = CycleStoreTests.Record(id, Target);
        var plan = baseRecord.Plan with { Original = original, Proposed = intended, Applicable = true };
        var experiment = baseRecord.Experiment with { Plan = plan };
        return baseRecord with
        {
            Plan = plan, Experiment = experiment, Original = original, IntendedApplied = intended,
            ObservedReadback = intended, ApplyStatus = ApplyStatus.Verified, OperationStage = "appliedVerified"
        };
    }

    private static LegacyCycleImport Legacy(IReadOnlyDictionary<string, PreferenceValue> original,
        IReadOnlyDictionary<string, PreferenceValue>? intended) => new(Guid.NewGuid(), DateTimeOffset.UnixEpoch,
            Target, original, intended, null, null, null, null, true, []);

    private static Dictionary<string, PreferenceValue> Values(params (string Key, int Value)[] values) =>
        values.ToDictionary(pair => pair.Key, pair => (PreferenceValue)new IntegerPreference(pair.Value), StringComparer.Ordinal);

    private static Dictionary<string, PreferenceValue> Reverse(IReadOnlyDictionary<string, PreferenceValue> values) =>
        values.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private sealed record UnsupportedPreference : PreferenceValue;
}
