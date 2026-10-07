using QFrey.Core.Platform;
using Xunit;

namespace QFrey.Tests;

public sealed class WindowsOwnershipTests
{
    [Fact]
    public void Remote_endpoint_never_returns_a_local_owner()
    {
        var result = TargetOwnership.Resolve(false, [42], false);
        Assert.Equal(OwnerLookupStatus.RemoteTarget, result.Status);
        Assert.Null(result.ProcessId);
    }

    [Fact]
    public void Distinct_listener_owners_are_ambiguous()
    {
        var result = TargetOwnership.Resolve(true, [42, 42, 43], false);
        Assert.Equal(OwnerLookupStatus.Ambiguous, result.Status);
        Assert.Null(result.ProcessId);
    }

    [Fact]
    public void Incomplete_owner_table_fails_closed_even_with_one_visible_pid()
    {
        var result = TargetOwnership.Resolve(true, [42], true);
        Assert.Equal(OwnerLookupStatus.Unknown, result.Status);
        Assert.Null(result.ProcessId);
    }

    [Fact]
    public void Access_denied_with_no_visible_pid_is_reported()
    {
        var result = TargetOwnership.Resolve(true, [], true);
        Assert.Equal(OwnerLookupStatus.AccessDenied, result.Status);
    }

    [Fact]
    public void Reused_pid_does_not_match_a_different_process_lifetime()
    {
        Assert.False(TargetOwnership.SameProcess(new(42, 100), new(42, 101)));
        Assert.False(TargetOwnership.SameProcess(new(42, 0), new(42, 0)));
        Assert.True(TargetOwnership.SameProcess(new(42, 100), new(42, 100)));
    }

    [Theory]
    [InlineData(17, true, "Nvme")]
    [InlineData(3, true, "Hdd")]
    [InlineData(3, false, "SsdSata")]
    [InlineData(7, false, null)]
    [InlineData(null, null, null)]
    public void Storage_classification_uses_the_selected_volume_properties(int? bus, bool? seekPenalty, string? expected)
    {
        Assert.Equal(expected, StorageDetectionPolicy.Classify(bus, seekPenalty));
    }
}
