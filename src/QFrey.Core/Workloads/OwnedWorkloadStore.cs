using QFrey.Core.Contracts;
using QFrey.Core.Persistence;
using QFrey.Core.Qbittorrent;

namespace QFrey.Core.Workloads;

/// <summary>Durable creation identity only; callers persist it before making an add request.</summary>
public sealed class OwnedWorkloadStore
{
    private readonly AtomicJsonStore records;
    private readonly string root;
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public OwnedWorkloadStore(string trustedRoot)
    { records = new AtomicJsonStore(trustedRoot); root = Path.GetFullPath(trustedRoot); }

    // Recovery scans only a bounded number of validated creation journals, never arbitrary selected files.
    public async Task<OwnedWorkloadJournal[]> ReadAllAsync(int maximum = 100, CancellationToken cancellationToken = default)
    {
        if (maximum is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximum));
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(root)) return [];
        var journals = new List<OwnedWorkloadJournal>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.json").Take(maximum + 1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (journals.Count == maximum || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)
                    || id == Guid.Empty) throw Failed();
                journals.Add(await ReadAsync(id, cancellationToken).ConfigureAwait(false) ?? throw Failed());
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Failed(); }
        return journals.OrderBy(journal => journal.Id).ToArray();
    }

    public async Task SaveAsync(OwnedWorkloadJournal journal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = Restore(journal.ToData());
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await records.ReadAsync<OwnedWorkloadJournalData>(candidate.Id, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                var current = Restore(existing);
                if (current != candidate) throw Failed();
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
            await records.WriteAsync(candidate.Id, candidate, cancellationToken).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    public async Task<OwnedWorkloadJournal?> ReadAsync(Guid journalId, CancellationToken cancellationToken = default)
    {
        if (journalId == Guid.Empty) throw new ArgumentException("Journal id is required.", nameof(journalId));
        var data = await records.ReadAsync<OwnedWorkloadJournalData>(journalId, cancellationToken).ConfigureAwait(false);
        if (data is null) return null;
        var journal = RestoreJournal(data);
        if (journal.Id != journalId) throw Failed();
        return journal;
    }

    private static OwnedWorkloadJournalData Restore(OwnedWorkloadJournalData? data) => RestoreJournal(data).ToData();

    private static OwnedWorkloadJournal RestoreJournal(OwnedWorkloadJournalData? data)
    {
        var restored = OwnedWorkloadJournal.Restore(data);
        return restored.Journal ?? throw Failed();
    }

    private static QbittorrentException Failed() => new(ErrorCodes.PersistenceFailed);
}
