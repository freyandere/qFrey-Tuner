using System.Security.Cryptography;
using QFrey.Core.Contracts;
using QFrey.Core.Persistence;
using QFrey.Core.Qbittorrent;

namespace QFrey.Core.Tuning;

public sealed record LegacyRestoreJournal(int SchemaVersion, Guid RestoreId, Guid SourceCycleId, TargetIdentity Target,
    IReadOnlyDictionary<string, PreferenceValue> Original, IReadOnlyDictionary<string, PreferenceValue> BeforeRestore,
    IReadOnlyDictionary<string, PreferenceValue> ObservedReadback, string Status,
    DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);

public sealed record LegacyRestoreConfirmation(string Token, Guid SessionId, Guid SourceCycleId,
    DateTimeOffset ExpiresUtc, RestoreReview Review);

// Coordinates a guarded legacy restore. It never archives/imports the source file or stores credentials.
public sealed class LegacyRestoreCoordinator : IDisposable
{
    private sealed record Consent(Guid SessionId, Guid SourceCycleId, TargetIdentity Target, string Fingerprint,
        LegacyCycleImport Backup, DateTimeOffset ExpiresUtc);

    private readonly QbittorrentSession session;
    private readonly Guid sessionId;
    private readonly AtomicJsonStore journals;
    private readonly OperationGate gate = new(queueCapacity: 0);
    private readonly TimeProvider clock;
    private readonly Func<Task>? commitStarting;
    private readonly Dictionary<string, Consent> consents = new(StringComparer.Ordinal);

    public LegacyRestoreCoordinator(QbittorrentSession session, Guid sessionId, string journalDirectory, TimeProvider? clock = null,
        Func<Task>? commitStarting = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        if (sessionId == Guid.Empty) throw new ArgumentException("Session required.", nameof(sessionId));
        this.sessionId = sessionId;
        journals = new AtomicJsonStore(journalDirectory);
        this.clock = clock ?? TimeProvider.System;
        this.commitStarting = commitStarting;
    }

    public Task<RestoreReview> ReviewAsync(LegacyCycleImport backup, CancellationToken token = default) =>
        gate.RunAsync(async cancellation =>
        {
            ArgumentNullException.ThrowIfNull(backup);
            await RefreshTargetAsync(cancellation).ConfigureAwait(false);
            var live = await session.ReadPreferencesAsync(cancellation).ConfigureAwait(false);
            return RestoreReviewBuilder.Create(backup, CurrentTarget(), live);
        }, token);

    public Task<LegacyRestoreConfirmation> RequestConfirmationAsync(LegacyCycleImport backup,
        string expectedReviewFingerprint, CancellationToken token = default) => gate.RunAsync(async cancellation =>
    {
        ArgumentNullException.ThrowIfNull(backup);
        await RefreshTargetAsync(cancellation).ConfigureAwait(false);
        var live = await session.ReadPreferencesAsync(cancellation).ConfigureAwait(false);
        var target = CurrentTarget();
        var review = RestoreReviewBuilder.Create(backup, target, live);
        if (!review.CanRestore || !StringComparer.Ordinal.Equals(review.Fingerprint, expectedReviewFingerprint))
            throw Failure(ErrorCodes.PreferenceDrift);

        PruneConsents();
        if (consents.Count >= 32) throw Failure(ErrorCodes.OperationConflict);
        var safeBackup = SafeCopy(backup, review);
        var confirmation = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expires = clock.GetUtcNow().AddMinutes(2);
        consents.Add(confirmation, new(sessionId, safeBackup.CycleId, target, review.Fingerprint, safeBackup, expires));
        return new LegacyRestoreConfirmation(confirmation, sessionId, safeBackup.CycleId, expires, review);
    }, token);

    public Task<LegacyRestoreJournal> RestoreAsync(string confirmationToken, CancellationToken token = default) =>
        gate.RunAsync(async cancellation =>
        {
            var consent = Consume(confirmationToken);
            if (consent.ExpiresUtc <= clock.GetUtcNow()) throw Failure(ErrorCodes.ConfirmationExpired);
            if (consent.SessionId != sessionId || consent.SourceCycleId != consent.Backup.CycleId
                || consent.Target != CurrentTarget()) throw Failure(ErrorCodes.SessionStale);

            await RefreshTargetAsync(cancellation).ConfigureAwait(false);
            var current = await session.ReadPreferencesAsync(cancellation).ConfigureAwait(false);
            var review = RestoreReviewBuilder.Create(consent.Backup, CurrentTarget(), current);
            if (!review.CanRestore || !StringComparer.Ordinal.Equals(review.Fingerprint, consent.Fingerprint))
                throw Failure(ErrorCodes.PreferenceDrift);

            var restorable = review.Differences.Where(diff => diff.Disposition == RestoreKeyDisposition.RestoreRequired).ToArray();
            var desired = new Dictionary<string, PreferenceValue>(StringComparer.Ordinal);
            var before = new Dictionary<string, PreferenceValue>(StringComparer.Ordinal);
            foreach (var diff in restorable)
            {
                if (diff.Original is null || diff.Current is null) throw Failure(ErrorCodes.BackupInvalid);
                desired.Add(diff.Key, diff.Original);
                before.Add(diff.Key, diff.Current);
            }
            if (desired.Count == 0) throw Failure(ErrorCodes.PreferenceDrift);
            cancellation.ThrowIfCancellationRequested();

            var now = clock.GetUtcNow();
            var journal = new LegacyRestoreJournal(1, Guid.NewGuid(), consent.SourceCycleId, consent.Target,
                ReadOnly(desired), ReadOnly(before), Empty, "pending", now, now);
            // Durable evidence is required before even one POST can be sent.
            await journals.WriteAsync(journal.RestoreId, journal, cancellation).ConfigureAwait(false);
            if (commitStarting is not null) await commitStarting().ConfigureAwait(false);
            if (cancellation.IsCancellationRequested)
            {
                await SaveFinalAsync(journal with { Status = "cancelledBeforeWrite", UpdatedUtc = clock.GetUtcNow() })
                    .ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
            }

            using var critical = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            IReadOnlyDictionary<string, PreferenceValue>? actual = null;
            try
            {
                await session.WritePreferencesAsync(desired, before, critical.Token).ConfigureAwait(false);
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    actual = await session.ReadPreferencesAsync(critical.Token).ConfigureAwait(false);
                    if (Matches(actual, desired))
                        return await SaveFinalAsync(journal with
                        {
                            ObservedReadback = Select(actual, desired.Keys), Status = "verified", UpdatedUtc = clock.GetUtcNow()
                        }).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromMilliseconds(200), critical.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is QbittorrentException or OperationCanceledException or ObjectDisposedException)
            {
                // POST is never repeated. The write API can fail before or after transmission;
                // a fresh readback determines the only safe final status.
            }

            using (var recoveryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                try { actual = await session.ReadRecoveryPreferencesAsync(recoveryDeadline.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is QbittorrentException or OperationCanceledException or ObjectDisposedException)
                { }
            }
            var status = actual is not null && Matches(actual, desired) ? "verified" : "unverified";
            var final = journal with
            {
                ObservedReadback = actual is null ? Empty : Select(actual, before.Keys),
                Status = status,
                UpdatedUtc = clock.GetUtcNow()
            };
            return await SaveFinalAsync(final).ConfigureAwait(false);
        }, token);

    public Task<LegacyRestoreJournal> ReconcileAsync(Guid restoreId, CancellationToken token = default) =>
        gate.RunAsync(async cancellation =>
        {
            if (restoreId == Guid.Empty) throw Failure(ErrorCodes.PersistenceFailed);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            var journal = await journals.ReadAsync<LegacyRestoreJournal>(restoreId, deadline.Token).ConfigureAwait(false);
            ValidateJournal(journal, restoreId);
            await RefreshTargetAsync(deadline.Token).ConfigureAwait(false);
            if (journal!.Target != CurrentTarget()) throw Failure(ErrorCodes.SessionStale);
            var actual = await session.ReadPreferencesAsync(deadline.Token).ConfigureAwait(false);
            await RefreshTargetAsync(deadline.Token).ConfigureAwait(false);
            if (journal.Target != CurrentTarget()) throw Failure(ErrorCodes.SessionStale);
            var observed = Select(actual, journal.Original.Keys);
            if (!ValidMap(observed)) throw Failure(ErrorCodes.SchemaIncompatible);
            var status = Matches(actual, journal.Original) ? "verified"
                : Matches(actual, journal.BeforeRestore) ? "notApplied" : "unverified";
            var reconciled = journal with { Status = status, ObservedReadback = observed, UpdatedUtc = clock.GetUtcNow() };
            await journals.WriteAsync(restoreId, reconciled, deadline.Token).ConfigureAwait(false);
            return reconciled;
        }, token);

    private static void ValidateJournal(LegacyRestoreJournal? journal, Guid expectedId)
    {
        if (journal is null || journal.SchemaVersion != 1 || journal.RestoreId != expectedId
            || journal.SourceCycleId == Guid.Empty || !ValidTarget(journal.Target)
            || !ValidMap(journal.Original) || !ValidMap(journal.BeforeRestore) || !ValidMap(journal.ObservedReadback)
            || journal.Original.Count == 0 || journal.Original.Count != journal.BeforeRestore.Count
            || journal.Original.Keys.Any(key => !journal.BeforeRestore.ContainsKey(key))
            || journal.ObservedReadback.Keys.Any(key => !journal.Original.ContainsKey(key))
            || journal.CreatedUtc.Offset != TimeSpan.Zero || journal.UpdatedUtc.Offset != TimeSpan.Zero
            || journal.CreatedUtc == default || journal.UpdatedUtc < journal.CreatedUtc
            || journal.Status is not ("pending" or "unverified" or "verified" or "cancelledBeforeWrite" or "notApplied")
            || journal.Status == "verified" && !Matches(journal.ObservedReadback, journal.Original)
            || journal.Status == "notApplied" && !Matches(journal.ObservedReadback, journal.BeforeRestore)
            || journal.Status == "cancelledBeforeWrite" && journal.ObservedReadback.Count != 0)
            throw Failure(ErrorCodes.PersistenceFailed);
    }

    private static bool ValidMap(IReadOnlyDictionary<string, PreferenceValue>? values) => values is not null
        && values.Count <= 64 && values.All(pair => CycleStore.IsSafePreferenceKey(pair.Key)
            && CycleStore.IsSafePreferenceValue(pair.Key, pair.Value));

    private static bool ValidTarget(TargetIdentity? target)
    {
        if (target is null || target.Endpoint is not { Length: > 0 and <= 2048 }
            || !Uri.TryCreate(target.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.AbsoluteUri != target.Endpoint || endpoint.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment) || target.Endpoint.IndexOfAny(['?', '#']) >= 0) return false;
        try { Compatibility.Validate(target.QbittorrentVersion, target.ApiVersion, target.LibtorrentVersion); return true; }
        catch (QbittorrentException) { return false; }
    }

    public void Dispose()
    {
        gate.Dispose();
        consents.Clear();
    }

    private async Task RefreshTargetAsync(CancellationToken token)
    {
        if (!session.IsValidated) throw Failure(ErrorCodes.TargetNotValidated);
        await session.RevalidateVersionsAsync(token).ConfigureAwait(false);
    }

    private TargetIdentity CurrentTarget() => new(session.Endpoint.AbsoluteUri, session.Versions.Qbittorrent,
        session.Versions.WebApi, session.Versions.Libtorrent);

    private LegacyCycleImport SafeCopy(LegacyCycleImport backup, RestoreReview review)
    {
        var original = review.Differences.Where(diff => diff.Original is not null)
            .ToDictionary(diff => diff.Key, diff => diff.Original!, StringComparer.Ordinal);
        var intended = review.Differences.Where(diff => diff.Intended is not null)
            .ToDictionary(diff => diff.Key, diff => diff.Intended!, StringComparer.Ordinal);
        return new LegacyCycleImport(backup.CycleId, backup.CreatedUtc, backup.Target,
            ReadOnly(original), ReadOnly(intended), null, null, null, null, null, []);
    }

    private Consent Consume(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || !consents.Remove(token, out var consent))
            throw Failure(ErrorCodes.ConfirmationExpired);
        return consent;
    }

    private void PruneConsents()
    {
        var now = clock.GetUtcNow();
        foreach (var key in consents.Where(pair => pair.Value.ExpiresUtc <= now).Select(pair => pair.Key).ToArray())
            consents.Remove(key);
    }

    private async Task<LegacyRestoreJournal> SaveFinalAsync(LegacyRestoreJournal journal)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await journals.WriteAsync(journal.RestoreId, journal, deadline.Token).ConfigureAwait(false);
        return journal;
    }

    private static IReadOnlyDictionary<string, PreferenceValue> Select(IReadOnlyDictionary<string, PreferenceValue> values,
        IEnumerable<string> keys) => ReadOnly(keys.Where(values.ContainsKey).Distinct(StringComparer.Ordinal)
            .ToDictionary(key => key, key => values[key], StringComparer.Ordinal));

    private static bool Matches(IReadOnlyDictionary<string, PreferenceValue> actual,
        IReadOnlyDictionary<string, PreferenceValue> expected) => expected.Count > 0
        && expected.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static IReadOnlyDictionary<string, PreferenceValue> ReadOnly(Dictionary<string, PreferenceValue> values) =>
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, PreferenceValue>(values);

    private static IReadOnlyDictionary<string, PreferenceValue> Empty => ReadOnly(new(StringComparer.Ordinal));
    private static QbittorrentException Failure(string code) => new(code);
}
