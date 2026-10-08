using System.Diagnostics;
using QFrey.Core.Platform;
using QFrey.Core.Contracts;
using QFrey.Desktop.Platform;
using Xunit;

namespace QFrey.Tests;
public class WindowsNativeTests
{
    [Fact]
    public async Task ExactOwnedHelperPreservesLaunchContextAndPortIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "pyproject.toml"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var executable = Path.Combine(root.FullName, ".cache", "bin", "QFrey.NativeHelper", configuration, "net10.0", "qbittorrent.exe");
        Assert.True(File.Exists(executable));
        var info = new ProcessStartInfo(executable) { WorkingDirectory = root.FullName, UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("owned test argument with spaces");
        info.ArgumentList.Add("--isolated-mock");
        using var child = Process.Start(info)!;
        try
        {
            var port = int.Parse((await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
            var expected = new ProcessIdentity(child.Id, child.StartTime.ToUniversalTime().Ticks);
            var owner = WindowsPlatformProbe.FindOwner($"http://127.0.0.1:{port}");
            Assert.Equal(OwnerLookupStatus.Available, owner.Status); Assert.Equal(expected, owner.Owner);
            var clock = new SampleClock();
            var sampler = new WindowsProcessMetrics($"http://127.0.0.1:{port}", clock);
            var local = sampler.Sample();
            Assert.IsType<UnavailableReading>(local.Single(m => m.Id == "local.cpu").Reading);
            Assert.True(Assert.IsType<FreshReading>(local.Single(m => m.Id == "local.privateMemory").Reading).Value > 0);
            Assert.All(local, metric => Assert.Equal(MetricScope.LocalProcess, metric.Scope));
            clock.Advance();
            var next = sampler.Sample();
            Assert.InRange(Assert.IsType<FreshReading>(next.Single(m => m.Id == "local.cpu").Reading).Value, 0, 100);
            Assert.All(new WindowsProcessMetrics("https://example.invalid").Sample(), metric =>
                Assert.Equal("REMOTE_TARGET", Assert.IsType<UnavailableReading>(metric.Reading).ReasonCode));
            if (Environment.OSVersion.Version.Build is < 19041 or > 26100)
            {
                Assert.False(ProcessLaunchContext.TryCapture(expected, out _, out var unsupported));
                Assert.Equal("launchContextPlatformUnsupported", unsupported);
                Assert.True(owner.RestartBlocked);
                return;
            }
            owner = WindowsPlatformProbe.FindOwner($"http://127.0.0.1:{port}", captureLaunchContext: true);
            Assert.True(ProcessLaunchContext.TryCapture(expected, out var context, out var reason), reason);
            Assert.False(owner.RestartBlocked); Assert.NotNull(context);
            Assert.Equal(Path.GetFullPath(executable), context.ExecutablePath, ignoreCase: true);
            Assert.Equal(root.FullName, Path.TrimEndingDirectorySeparator(context.WorkingDirectory), ignoreCase: true);
            Assert.Equal(new[] { "owned test argument with spaces", "--isolated-mock" }, context.Arguments);
            Assert.Equal("ProcessLaunchContext [redacted]", context.ToString());
            Assert.False(ProcessLaunchContext.TryCapture(expected with { StartTimeUtcTicks = expected.StartTimeUtcTicks + 1 }, out _, out _));
        }
        finally
        {
            await child.StandardInput.WriteLineAsync("quit"); child.StandardInput.Close();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(0, child.ExitCode);
    }

    private sealed class SampleClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance() => ticks += TimeSpan.FromSeconds(2).Ticks;
    }
}
