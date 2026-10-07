using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;

namespace QFrey.Core.Persistence;

public enum RestoreKeyDisposition
{
    AlreadyOriginal,
    RestoreRequired,
    Conflict,
    MissingLiveValue,
    InvalidBackupValue,
    InvalidLiveValue,
    InvalidIntendedValue,
    IntendedUnknown
}

public sealed record RestorePreferenceDiff(string Key, PreferenceValue? Original, PreferenceValue? Intended,
    PreferenceValue? Current, RestoreKeyDisposition Disposition);

// Read-only classification only. A positive review is evidence for a future confirmation flow, not write authority.
public sealed record RestoreReview(bool IsLegacy, bool TargetMatches, bool CanRestore, bool AlreadyOriginal,
    string Fingerprint, IReadOnlyList<string> BlockReasonCodes, IReadOnlyList<RestorePreferenceDiff> Differences);

public static class RestoreReviewBuilder
{
    public static RestoreReview Create(CycleRecord backup, TargetIdentity liveTarget,
        IReadOnlyDictionary<string, PreferenceValue> livePreferences)
    {
        ArgumentNullException.ThrowIfNull(backup);
        try { CycleStore.Validate(backup); }
        catch (QFrey.Core.Qbittorrent.QbittorrentException)
        {
            return Blocked(false, backup.CycleId, backup.Target, liveTarget, "INVALID_BACKUP");
        }
        return Create(backup.CycleId, backup.Target, backup.Original, backup.IntendedApplied, false, liveTarget, livePreferences);
    }

    public static RestoreReview Create(LegacyCycleImport backup, TargetIdentity liveTarget,
        IReadOnlyDictionary<string, PreferenceValue> livePreferences)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return Create(backup.CycleId, backup.Target, backup.Original, backup.IntendedApplied, true, liveTarget, livePreferences);
    }

    private static RestoreReview Create(Guid sourceId, TargetIdentity backupTarget,
        IReadOnlyDictionary<string, PreferenceValue>? original,
        IReadOnlyDictionary<string, PreferenceValue>? intended, bool legacy, TargetIdentity liveTarget,
        IReadOnlyDictionary<string, PreferenceValue> livePreferences)
    {
        ArgumentNullException.ThrowIfNull(liveTarget);
        ArgumentNullException.ThrowIfNull(livePreferences);
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        if (sourceId == Guid.Empty) codes.Add("INVALID_BACKUP_ID");
        if (!IsSafeTarget(backupTarget)) codes.Add("INVALID_BACKUP_TARGET");
        if (!IsSafeTarget(liveTarget)) codes.Add("INVALID_LIVE_TARGET");
        var targetMatches = IsSafeTarget(backupTarget) && IsSafeTarget(liveTarget) && backupTarget == liveTarget;
        if (!targetMatches) codes.Add("TARGET_MISMATCH");
        if (original is null || original.Count == 0) codes.Add("BACKUP_EMPTY");
        if (original is null) original = new Dictionary<string, PreferenceValue>();

        var safeOriginal = new Dictionary<string, PreferenceValue>(StringComparer.Ordinal);
        var diffs = new List<RestorePreferenceDiff>();
        foreach (var (key, value) in original.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!CycleStore.IsSafePreferenceKey(key))
            {
                codes.Add("INVALID_BACKUP_KEY");
                continue; // Never expose an unknown key or its value in the review model.
            }
            if (!CycleStore.IsSafePreferenceValue(key, value))
            {
                codes.Add("INVALID_BACKUP_VALUE");
                diffs.Add(new(key, null, null, null, RestoreKeyDisposition.InvalidBackupValue));
                continue;
            }
            safeOriginal.Add(key, value);
        }

        var safeIntended = new Dictionary<string, PreferenceValue>(StringComparer.Ordinal);
        var invalidIntendedKeys = new HashSet<string>(StringComparer.Ordinal);
        if (intended is null)
        {
            codes.Add("INTENDED_UNKNOWN");
        }
        else
        {
            foreach (var (key, value) in intended.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!CycleStore.IsSafePreferenceKey(key))
                {
                    codes.Add("INVALID_INTENDED_KEY");
                    continue;
                }
                if (!safeOriginal.ContainsKey(key))
                {
                    codes.Add("INTENDED_KEY_NOT_IN_BACKUP");
                    continue;
                }
                if (!CycleStore.IsSafePreferenceValue(key, value))
                {
                    codes.Add("INVALID_INTENDED_VALUE");
                    invalidIntendedKeys.Add(key);
                    continue;
                }
                safeIntended.Add(key, value);
            }
        }

        var safeCurrent = new Dictionary<string, PreferenceValue>(StringComparer.Ordinal);
        foreach (var key in safeOriginal.Keys.Order(StringComparer.Ordinal))
        {
            var originalValue = safeOriginal[key];
            if (!livePreferences.TryGetValue(key, out var current))
            {
                codes.Add("LIVE_KEY_MISSING");
                diffs.Add(new(key, originalValue, safeIntended.GetValueOrDefault(key), null, RestoreKeyDisposition.MissingLiveValue));
                continue;
            }
            if (!CycleStore.IsSafePreferenceValue(key, current))
            {
                codes.Add("INVALID_LIVE_VALUE");
                diffs.Add(new(key, originalValue, safeIntended.GetValueOrDefault(key), null, RestoreKeyDisposition.InvalidLiveValue));
                continue;
            }
            safeCurrent.Add(key, current);
            if (current == originalValue)
            {
                diffs.Add(new(key, originalValue, safeIntended.GetValueOrDefault(key), current, RestoreKeyDisposition.AlreadyOriginal));
            }
            else if (invalidIntendedKeys.Contains(key))
            {
                diffs.Add(new(key, originalValue, null, current, RestoreKeyDisposition.InvalidIntendedValue));
            }
            else if (!safeIntended.TryGetValue(key, out var intendedValue))
            {
                codes.Add("INTENDED_KEY_MISSING");
                diffs.Add(new(key, originalValue, null, current, RestoreKeyDisposition.IntendedUnknown));
            }
            else if (current == intendedValue)
            {
                diffs.Add(new(key, originalValue, intendedValue, current, RestoreKeyDisposition.RestoreRequired));
            }
            else
            {
                codes.Add("EXTERNAL_DRIFT");
                diffs.Add(new(key, originalValue, intendedValue, current, RestoreKeyDisposition.Conflict));
            }
        }

        if (legacy && intended is null) codes.Add("LEGACY_INTENDED_UNAVAILABLE");
        var readOnlyCodes = Array.AsReadOnly(codes.ToArray());
        var differences = Array.AsReadOnly(diffs.OrderBy(diff => diff.Key, StringComparer.Ordinal).ToArray());
        var fingerprint = Fingerprint(sourceId, legacy, backupTarget, liveTarget, safeOriginal, safeIntended, safeCurrent, readOnlyCodes);
        var hasConflict = differences.Any(diff => diff.Disposition is RestoreKeyDisposition.Conflict
            or RestoreKeyDisposition.MissingLiveValue or RestoreKeyDisposition.InvalidBackupValue
            or RestoreKeyDisposition.InvalidLiveValue or RestoreKeyDisposition.InvalidIntendedValue
            or RestoreKeyDisposition.IntendedUnknown);
        var restoreCount = differences.Count(diff => diff.Disposition == RestoreKeyDisposition.RestoreRequired);
        var hasUnsafeSourceOrTarget = codes.Any(code => code is "INVALID_BACKUP_ID" or "INVALID_BACKUP_TARGET"
            or "INVALID_LIVE_TARGET" or "TARGET_MISMATCH" or "INVALID_BACKUP_KEY" or "INVALID_BACKUP_VALUE" or "BACKUP_EMPTY");
        var alreadyOriginal = !hasUnsafeSourceOrTarget && differences.Count > 0
            && differences.All(diff => diff.Disposition == RestoreKeyDisposition.AlreadyOriginal);
        var canRestore = targetMatches && codes.Count == 0 && !hasConflict && restoreCount > 0;
        return new(legacy, targetMatches, canRestore, alreadyOriginal, fingerprint, readOnlyCodes, differences);
    }

    private static RestoreReview Blocked(bool legacy, Guid id, TargetIdentity backupTarget, TargetIdentity liveTarget, string code)
    {
        var targetMatches = IsSafeTarget(backupTarget) && IsSafeTarget(liveTarget) && backupTarget == liveTarget;
        var codes = new SortedSet<string>(StringComparer.Ordinal) { code };
        if (!targetMatches) codes.Add("TARGET_MISMATCH");
        var readOnlyCodes = Array.AsReadOnly(codes.ToArray());
        return new(legacy, targetMatches, false, false,
            Fingerprint(id, legacy, backupTarget, liveTarget, Empty, Empty, Empty, readOnlyCodes),
            readOnlyCodes, Array.AsReadOnly(Array.Empty<RestorePreferenceDiff>()));
    }

    private static readonly IReadOnlyDictionary<string, PreferenceValue> Empty = new ReadOnlyDictionary<string, PreferenceValue>(
        new Dictionary<string, PreferenceValue>(StringComparer.Ordinal));

    private static bool IsSafeTarget(TargetIdentity? target) => target is not null && target.Endpoint is { Length: > 0 and <= 2048 }
        && Uri.TryCreate(target.Endpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme is "http" or "https"
        && string.IsNullOrEmpty(endpoint.UserInfo) && string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment)
        && target.QbittorrentVersion is { Length: > 0 and <= 64 }
        && target.ApiVersion is { Length: > 0 and <= 64 }
        && target.LibtorrentVersion is { Length: > 0 and <= 64 } && IsCompatible(target);

    private static bool IsCompatible(TargetIdentity target)
    {
        try { Compatibility.Validate(target.QbittorrentVersion, target.ApiVersion, target.LibtorrentVersion); return true; }
        catch (QbittorrentException) { return false; }
    }

    private static string Fingerprint(Guid sourceId, bool legacy, TargetIdentity backupTarget, TargetIdentity liveTarget,
        IReadOnlyDictionary<string, PreferenceValue> original, IReadOnlyDictionary<string, PreferenceValue> intended,
        IReadOnlyDictionary<string, PreferenceValue> current, IReadOnlyList<string> codes)
    {
        var canonical = new StringBuilder();
        Add(canonical, sourceId.ToString("N"));
        Add(canonical, legacy ? "legacy" : "cycle");
        AddTarget(canonical, backupTarget);
        AddTarget(canonical, liveTarget);
        AddMap(canonical, original);
        AddMap(canonical, intended);
        AddMap(canonical, current);
        foreach (var code in codes) Add(canonical, code);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static void AddTarget(StringBuilder builder, TargetIdentity? target)
    {
        if (target is null) { Add(builder, "null"); return; }
        Add(builder, target.Endpoint); Add(builder, target.QbittorrentVersion); Add(builder, target.ApiVersion); Add(builder, target.LibtorrentVersion);
    }

    private static void AddMap(StringBuilder builder, IReadOnlyDictionary<string, PreferenceValue> values)
    {
        Add(builder, values.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Add(builder, key);
            switch (value)
            {
                case IntegerPreference integer: Add(builder, "i"); Add(builder, integer.Value.ToString(CultureInfo.InvariantCulture)); break;
                case BooleanPreference boolean: Add(builder, "b"); Add(builder, boolean.Value ? "1" : "0"); break;
                case StringPreference text: Add(builder, "s"); Add(builder, text.Value); break;
            }
        }
    }

    private static void Add(StringBuilder builder, string? value)
    {
        value ??= "";
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }
}
