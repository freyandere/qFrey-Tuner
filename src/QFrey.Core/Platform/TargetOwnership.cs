namespace QFrey.Core.Platform;

public enum OwnerLookupStatus { Available, RemoteTarget, NotFound, Ambiguous, AccessDenied, Unknown }

public sealed record ProcessIdentity(int ProcessId, long StartTimeUtcTicks);
public sealed record OwnerLookupResult(OwnerLookupStatus Status, int? ProcessId, string? ReasonCode);

public static class TargetOwnership
{
    public static OwnerLookupResult Resolve(bool isLoopback, IEnumerable<int> listenerPids, bool hadUnavailableListeners)
    {
        if (!isLoopback) return new(OwnerLookupStatus.RemoteTarget, null, "remoteTarget");
        var pids = listenerPids.Where(pid => pid > 0).Distinct().ToArray();
        if (hadUnavailableListeners)
            return new(pids.Length == 0 ? OwnerLookupStatus.AccessDenied : OwnerLookupStatus.Unknown, null,
                pids.Length == 0 ? "ownerAccessDenied" : "ownerSetIncomplete");
        return pids.Length switch
        {
            0 => new(OwnerLookupStatus.NotFound, null, "listenerNotFound"),
            1 => new(OwnerLookupStatus.Available, pids[0], null),
            _ => new(OwnerLookupStatus.Ambiguous, null, "listenerAmbiguous")
        };
    }

    public static bool SameProcess(ProcessIdentity expected, ProcessIdentity observed) =>
        expected.ProcessId == observed.ProcessId && expected.StartTimeUtcTicks != 0
        && observed.StartTimeUtcTicks != 0 && expected.StartTimeUtcTicks == observed.StartTimeUtcTicks;
}

public static class StorageDetectionPolicy
{
    // STORAGE_BUS_TYPE: BusTypeAta = 3, BusTypeSata = 11, BusTypeNvme = 17.
    public static string? Classify(int? busType, bool? hasSeekPenalty) => busType == 17
        ? "Nvme"
        : hasSeekPenalty == true ? "Hdd"
        : hasSeekPenalty == false && busType is 3 or 11 ? "SsdSata" : null;
}
