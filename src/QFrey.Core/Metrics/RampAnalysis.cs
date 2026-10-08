namespace QFrey.Core.Metrics;

public sealed record RampPoint(double ElapsedSeconds, bool IsMeasurement, double? DownloadBytesPerSecond,
    double? Seeds, double? Peers);

public sealed record RampSummary(double? FirstTrafficSeconds, double? FirstSeedSeconds, double? FirstPeerSeconds,
    double? TimeTo50Seconds, double? TimeTo90Seconds, double? ReferenceDownloadBytesPerSecond,
    double? PeakConnectedSeeds, double? PeakConnectedPeers, bool FreshStart);

public static class RampAnalysis
{
    public static RampSummary Analyze(IReadOnlyList<RampPoint> points, bool freshStart)
    {
        if (points is null || points.Count > 100_000) throw new ArgumentException("INVALID_RAMP_INPUT");
        double previous = -1;
        foreach (var point in points)
        {
            if (point is null || !double.IsFinite(point.ElapsedSeconds) || point.ElapsedSeconds < 0
                || point.ElapsedSeconds <= previous || !NonnegativeOrNull(point.DownloadBytesPerSecond)
                || !NonnegativeOrNull(point.Seeds) || !NonnegativeOrNull(point.Peers))
                throw new ArgumentException("INVALID_RAMP_POINT");
            previous = point.ElapsedSeconds;
        }

        var measured = points.Where(x => x.IsMeasurement && x.DownloadBytesPerSecond is not null)
            .Select(x => x.DownloadBytesPerSecond!.Value).ToArray();
        var reference = measured.Length == 0 ? 0 : Median(measured.Skip(measured.Length / 2).ToArray());
        return new(First(points, x => x.DownloadBytesPerSecond > 0),
            First(points, x => x.Seeds > 0), First(points, x => x.Peers > 0),
            Sustained(points, reference, .5), Sustained(points, reference, .9), measured.Length == 0 ? null : reference,
            points.Where(x => x.Seeds is not null).Select(x => x.Seeds).Max(),
            points.Where(x => x.Peers is not null).Select(x => x.Peers).Max(), freshStart);
    }

    private static double? First(IReadOnlyList<RampPoint> points, Func<RampPoint, bool> predicate) =>
        points.FirstOrDefault(predicate)?.ElapsedSeconds;

    private static double? Sustained(IReadOnlyList<RampPoint> points, double reference, double fraction)
    {
        if (reference <= 0) return null;
        for (var i = 0; i + 2 < points.Count; i++)
        {
            var first = points[i]; var middle = points[i + 1]; var last = points[i + 2];
            if (first.DownloadBytesPerSecond >= reference * fraction
                && middle.DownloadBytesPerSecond >= reference * fraction
                && last.DownloadBytesPerSecond >= reference * fraction
                && last.ElapsedSeconds - first.ElapsedSeconds is >= 1.5 and <= 3.5)
                return first.ElapsedSeconds;
        }
        return null;
    }

    private static double Median(double[] values)
    {
        Array.Sort(values);
        var middle = values.Length / 2;
        return values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
    }

    private static bool NonnegativeOrNull(double? value) => value is null || double.IsFinite(value.Value) && value.Value >= 0;
}
