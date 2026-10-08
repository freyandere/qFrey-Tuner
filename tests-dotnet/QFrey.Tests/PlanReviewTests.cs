using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Tuning;
using Xunit;

namespace QFrey.Tests;
public class PlanReviewTests
{
    [Fact]
    public void ApprovalIsBoundToOwnedSnapshotAndReplacingItInvalidatesApproval()
    {
        var session = Guid.NewGuid();
        var original = new Dictionary<string, PreferenceValue> { ["max_connec"] = new IntegerPreference(500) };
        var proposed = new Dictionary<string, PreferenceValue> { ["max_connec"] = new IntegerPreference(1000) };
        var plan = new Plan(Guid.NewGuid(), session, 1, DateTimeOffset.UtcNow, "inputs", "baseline", false, true, false,
            [], [], [], original, proposed);
        var review = new PlanReview(session); review.Replace(plan);
        Assert.Throws<QbittorrentException>(() => review.RequireApproved(plan.Id, 1));
        Assert.Throws<QbittorrentException>(() => review.Approve(Guid.NewGuid(), 1));
        var approved = review.Approve(plan.Id, 1);
        original["max_connec"] = new IntegerPreference(999);
        proposed["max_connec"] = new IntegerPreference(999);
        ((Dictionary<string, PreferenceValue>)approved.Proposed)["max_connec"] = new IntegerPreference(999);
        Assert.Equal(new IntegerPreference(1000), review.RequireApproved(plan.Id, 1).Proposed["max_connec"]);
        Assert.Equal(new IntegerPreference(500), review.RequireApproved(plan.Id, 1).Original["max_connec"]);
        review.Replace(plan with { Id = Guid.NewGuid(), Revision = 2 });
        Assert.Throws<QbittorrentException>(() => review.RequireApproved(plan.Id, 1));
        Assert.False(review.Snapshot!.Approved);
    }
}
