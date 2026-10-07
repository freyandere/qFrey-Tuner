using System.Diagnostics;
using System.IO;
using QFrey.Core.Platform;

namespace QFrey.Desktop.Platform;

public enum TargetLifecycleStatus { Stopped, Restarted, Blocked, TimedOut, Cancelled, ShutdownFailed, StartFailed }

public sealed record TargetLifecycleResult(TargetLifecycleStatus Status, string ReasonCode,
    ProcessIdentity? OriginalOwner = null, ProcessIdentity? RestartedProcess = null);

// Graceful lifecycle only: this class never terminates a process.
public sealed class TargetLifecycle
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private readonly Func<string, bool, ProcessOwnerObservation> findOwner;
    private readonly Func<ProcessIdentity, ProcessProbe> probeProcess;
    private readonly Func<ProcessLaunchContext, ProcessIdentity?> launch;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public TargetLifecycle() : this((endpoint, capture) => WindowsPlatformProbe.FindOwner(endpoint, capture),
        ProbeCurrentProcess, LaunchCapturedProcess, static (duration, token) => Task.Delay(duration, token)) { }

    internal TargetLifecycle(Func<string, bool, ProcessOwnerObservation> findOwner,
        Func<ProcessIdentity, ProcessProbe> probeProcess,
        Func<ProcessLaunchContext, ProcessIdentity?> launch,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        this.findOwner = findOwner;
        this.probeProcess = probeProcess;
        this.launch = launch;
        this.delay = delay;
    }

    public Task<TargetLifecycleResult> StopAsync(string endpoint,
        Func<CancellationToken, Task> gracefulShutdown, TimeSpan? timeout = null, CancellationToken token = default) =>
        RunAsync(endpoint, gracefulShutdown, restart: false, timeout, token);

    public Task<TargetLifecycleResult> RestartAsync(string endpoint,
        Func<CancellationToken, Task> gracefulShutdown, TimeSpan? timeout = null, CancellationToken token = default) =>
        RunAsync(endpoint, gracefulShutdown, restart: true, timeout, token);

    private async Task<TargetLifecycleResult> RunAsync(string endpoint, Func<CancellationToken, Task> gracefulShutdown,
        bool restart, TimeSpan? timeout, CancellationToken callerToken)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || gracefulShutdown is null)
            return new(TargetLifecycleStatus.Blocked, "invalidLifecycleRequest");
        var deadline = timeout ?? DefaultTimeout;
        if (deadline <= TimeSpan.Zero || deadline > MaximumTimeout)
            return new(TargetLifecycleStatus.Blocked, "lifecycleTimeoutOutOfRange");

        var first = findOwner(endpoint, restart);
        if (first.Status != OwnerLookupStatus.Available || first.Owner is not ProcessIdentity expected)
            return new(TargetLifecycleStatus.Blocked, first.ReasonCode ?? "exactOwnerUnavailable");
        if (restart && (first.RestartBlocked || first.LaunchContext is null
            || !TargetOwnership.SameProcess(expected, first.LaunchContext.Identity)))
            return new(TargetLifecycleStatus.Blocked, first.RestartReasonCode ?? "launchContextUnavailable", expected);

        using var deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        deadlineSource.CancelAfter(deadline);
        var token = deadlineSource.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            // Re-resolve the complete loopback listener set immediately before invoking the Web API shutdown.
            var current = findOwner(endpoint, restart);
            if (current.Status != OwnerLookupStatus.Available || current.Owner is not ProcessIdentity currentOwner
                || !TargetOwnership.SameProcess(expected, currentOwner))
                return new(TargetLifecycleStatus.Blocked, current.ReasonCode ?? "ownerChangedBeforeShutdown", expected);
            if (restart && (current.RestartBlocked || current.LaunchContext is null
                || !TargetOwnership.SameProcess(expected, current.LaunchContext.Identity)))
                return new(TargetLifecycleStatus.Blocked, current.RestartReasonCode ?? "launchContextChanged", expected);

            await gracefulShutdown(token).ConfigureAwait(false);
            var exited = await WaitForExactExitAsync(expected, token).ConfigureAwait(false);
            if (exited != ProcessProbeState.Exited)
                return new(exited is ProcessProbeState.IdentityChanged or ProcessProbeState.Unavailable
                        ? TargetLifecycleStatus.Blocked : TargetLifecycleStatus.TimedOut,
                    exited switch
                    {
                        ProcessProbeState.IdentityChanged => "ownerIdentityChangedWhileStopping",
                        ProcessProbeState.Unavailable => "ownerExitWaitUnavailable",
                        _ => "ownerExitTimeout",
                    }, expected);
            if (!restart) return new(TargetLifecycleStatus.Stopped, "gracefulShutdownComplete", expected);

            // Do not start a second instance if another process acquired the endpoint during shutdown.
            var afterExit = findOwner(endpoint, false);
            if (afterExit.Status != OwnerLookupStatus.NotFound)
                return new(TargetLifecycleStatus.Blocked, afterExit.ReasonCode ?? "endpointOwnerDidNotClear", expected);

            var context = current.LaunchContext!;
            if (!TargetOwnership.SameProcess(expected, context.Identity))
                return new(TargetLifecycleStatus.Blocked, "launchContextChanged", expected);
            ProcessIdentity? restarted;
            try { restarted = launch(context); }
            catch { return new(TargetLifecycleStatus.StartFailed, "restartStartFailed", expected); }
            return restarted is ProcessIdentity identity
                ? new(TargetLifecycleStatus.Restarted, "restartStarted", expected, identity)
                : new(TargetLifecycleStatus.StartFailed, "restartStartFailed", expected);
        }
        catch (OperationCanceledException)
        {
            return new(callerToken.IsCancellationRequested ? TargetLifecycleStatus.Cancelled : TargetLifecycleStatus.TimedOut,
                callerToken.IsCancellationRequested ? "lifecycleCancelled" : "lifecycleTimeout", expected);
        }
        catch
        {
            return new(TargetLifecycleStatus.ShutdownFailed, "gracefulShutdownFailed", expected);
        }
    }

    private async Task<ProcessProbeState> WaitForExactExitAsync(ProcessIdentity expected, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var probe = probeProcess(expected);
            if (probe.State != ProcessProbeState.Running) return probe.State;
            await delay(PollInterval, token).ConfigureAwait(false);
        }
    }

    private static ProcessProbe ProbeCurrentProcess(ProcessIdentity expected)
    {
        try
        {
            using var process = Process.GetProcessById(expected.ProcessId);
            if (process.HasExited) return new(ProcessProbeState.Exited);
            var observed = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks);
            return TargetOwnership.SameProcess(expected, observed)
                ? new(ProcessProbeState.Running)
                : new(ProcessProbeState.IdentityChanged);
        }
        catch (ArgumentException) { return new(ProcessProbeState.Exited); }
        catch (InvalidOperationException) { return new(ProcessProbeState.Exited); }
        catch (System.ComponentModel.Win32Exception) { return new(ProcessProbeState.Unavailable); }
        catch (UnauthorizedAccessException) { return new(ProcessProbeState.Unavailable); }
    }

    private static ProcessIdentity? LaunchCapturedProcess(ProcessLaunchContext context)
    {
        if (!File.Exists(context.ExecutablePath) || !Directory.Exists(context.WorkingDirectory)) return null;
        var start = new ProcessStartInfo(context.ExecutablePath)
        {
            WorkingDirectory = context.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in context.Arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        if (process is null) return null;
        return new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks);
    }

    internal enum ProcessProbeState { Running, Exited, IdentityChanged, Unavailable }
    internal readonly record struct ProcessProbe(ProcessProbeState State);
}
