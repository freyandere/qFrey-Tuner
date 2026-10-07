using QFrey.Core.Metrics;
using Xunit;

namespace QFrey.Tests;

public class RampAnalysisTests
{
    [Fact]
    public void MatchesLegacyGoldenRampAndObservedConnectionTimes()
    {
        double[] mib = [0, 10, 20, 55, 70, 100, 100, 100, 100, 100, 100];
        var points = mib.Select((speed, i) => new RampPoint(i, i >= 5, speed * 1024 * 1024,
            i < 2 ? 0 : 3, i == 0 ? 0 : 5)).ToArray();
        var ramp = RampAnalysis.Analyze(points, true);
        Assert.Equal(1, ramp.FirstTrafficSeconds);
        Assert.Equal(2, ramp.FirstSeedSeconds);
        Assert.Equal(1, ramp.FirstPeerSeconds);
        Assert.Equal(3, ramp.TimeTo50Seconds);
        Assert.Equal(5, ramp.TimeTo90Seconds);
        Assert.Equal(100 * 1024d * 1024, ramp.ReferenceDownloadBytesPerSecond);
        Assert.Equal(3, ramp.PeakConnectedSeeds);
        Assert.Equal(5, ramp.PeakConnectedPeers);
        Assert.True(ramp.FreshStart);
    }

    [Fact]
    public void SpikeDoesNotCountAndElapsedGapMustBeWithinLegacyBounds()
    {
        var points = new[] { 0d, 100, 0, 0, 0, 100, 100, 100, 100 }
            .Select((value, i) => new RampPoint(i, true, value, null, null)).ToArray();
        Assert.Equal(5, RampAnalysis.Analyze(points, false).TimeTo90Seconds);

        var tooClose = new[] { 0d, .1, .2 }.Select(t => new RampPoint(t, true, 100, null, null)).ToArray();
        Assert.Null(RampAnalysis.Analyze(tooClose, false).TimeTo90Seconds);
        var tooFar = new[] { 0d, 2, 4 }.Select(t => new RampPoint(t, true, 100, null, null)).ToArray();
        Assert.Null(RampAnalysis.Analyze(tooFar, false).TimeTo90Seconds);
    }

    [Fact]
    public void MissingMeasurementsStayUnknownAndZeroReferenceHasNoThreshold()
    {
        var unknown = RampAnalysis.Analyze([new RampPoint(0, false, 10, null, null)], false);
        Assert.Null(unknown.FirstSeedSeconds);
        Assert.Null(unknown.FirstPeerSeconds);
        Assert.Null(unknown.PeakConnectedSeeds);
        Assert.Null(unknown.ReferenceDownloadBytesPerSecond);

        var zero = Enumerable.Range(0, 4).Select(i => new RampPoint(i, true, 0, 0, 0)).ToArray();
        var result = RampAnalysis.Analyze(zero, false);
        Assert.Null(result.TimeTo50Seconds);
        Assert.Null(result.TimeTo90Seconds);
    }
}
