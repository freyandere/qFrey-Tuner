using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;

namespace QFrey.Core.Tuning;

// Owns one canonical snapshot. Returned DTO arrays/dictionaries never grant mutation authority.
public sealed class PlanReview(Guid targetSessionId)
{
    private byte[]? canonical;
    public Plan? Snapshot => canonical is null ? null : JsonSerializer.Deserialize<Plan>(canonical, Protocol.Json);
    public void Replace(Plan plan)
    {
        if (plan.TargetSessionId != targetSessionId || targetSessionId == Guid.Empty || plan.Approved)
            throw new QbittorrentException(ErrorCodes.SessionStale);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan, Protocol.Json);
        if (bytes.Length > Protocol.MaxSnapshotBytes) throw new QbittorrentException(ErrorCodes.InvalidOverride);
        canonical = bytes;
    }
    public Plan Approve(Guid planId, long revision)
    {
        var plan = Snapshot ?? throw new QbittorrentException(ErrorCodes.PlanStale);
        var approved = PlanBuilder.Approve(plan, planId, revision);
        canonical = JsonSerializer.SerializeToUtf8Bytes(approved, Protocol.Json);
        return Snapshot!;
    }
    public Plan RequireApproved(Guid planId, long revision)
    {
        var plan = Snapshot;
        if (plan is null || plan.Id != planId || plan.Revision != revision) throw new QbittorrentException(ErrorCodes.PlanStale);
        if (!plan.Approved || !plan.Applicable || plan.PreviewOnly) throw new QbittorrentException(ErrorCodes.PlanNotApproved);
        return plan;
    }
}
