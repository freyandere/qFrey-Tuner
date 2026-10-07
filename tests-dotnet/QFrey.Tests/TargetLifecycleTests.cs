using System.Reflection;
using QFrey.Core.Platform;
using QFrey.Desktop.Platform;
using Xunit;

namespace QFrey.Tests;

public sealed class TargetLifecycleTests
{
    private static readonly ProcessIdentity Owner = new(71, 123456);
    private const string Endpoint = "http://127.0.0.1:8080";

    [Fact]
    public async Task Remote_or_ambiguous_owner_never_receives_shutdown()
    {
        foreach (var status in new[] { OwnerLookupStatus.RemoteTarget, OwnerLookupStatus.Ambiguous, OwnerLookupStatus.AccessDenied })
        {
            var calls = 0;
            var lifecycle = Create((_, _) => new(status, null, "ownerUnavailable", true, "ownerUnavailable"));
            var result = await lifecycle.StopAsync(Endpoint, _ => { calls++; return Task.CompletedTask; });
            Assert.Equal(TargetLifecycleStatus.Blocked, result.Status);
            Assert.Equal(0, calls);
        }
    }

    [Fact]
    public async Task Changed_owner_on_immediate_recheck_never_receives_shutdown()
    {
        var calls = 0;
        var lookup = 0;
        var lifecycle = Create((_, _) => ++lookup == 1
            ? Available(Owner)
            : Available(Owner with { StartTimeUtcTicks = Owner.StartTimeUtcTicks + 1 }));

        var result = await lifecycle.StopAsync(Endpoint, _ => { calls++; return Task.CompletedTask; });

        Assert.Equal(TargetLifecycleStatus.Blocked, result.Status);
        Assert.Equal("ownerChangedBeforeShutdown", result.ReasonCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Stop_waits_for_exact_process_exit_and_never_starts_a_process()
    {
        var probes = 0;
        var calls = 0;
        var launches = 0;
        var lifecycle = Create((_, _) => Available(Owner),
            _ => new(++probes == 1 ? TargetLifecycle.ProcessProbeState.Running : TargetLifecycle.ProcessProbeState.Exited),
            _ => { launches++; return Owner; },
            (_, _) => Task.CompletedTask);

        var result = await lifecycle.StopAsync(Endpoint, _ => { calls++; return Task.CompletedTask; });

        Assert.Equal(TargetLifecycleStatus.Stopped, result.Status);
        Assert.Equal(1, calls);
        Assert.Equal(0, launches);
        Assert.Equal(2, probes);
    }

    [Fact]
    public async Task Restart_reuses_only_the_captured_executable_arguments_and_working_directory()
    {
        var context = CreateContext(Owner);
        string? executable = null, workingDirectory = null;
        IReadOnlyList<string>? arguments = null;
        var lookups = 0;
        var lifecycle = Create((_, capture) =>
        {
            lookups++;
            if (!capture && lookups == 3) return new(OwnerLookupStatus.NotFound, null, "listenerNotFound", true, "listenerNotFound");
            return Available(Owner, capture ? context : null);
        }, _ => new(TargetLifecycle.ProcessProbeState.Exited), captured =>
        {
            executable = captured.ExecutablePath;
            workingDirectory = captured.WorkingDirectory;
            arguments = captured.Arguments.ToArray();
            return new ProcessIdentity(72, 223456);
        }, (_, _) => Task.CompletedTask);

        var result = await lifecycle.RestartAsync(Endpoint, _ => Task.CompletedTask);

        Assert.Equal(TargetLifecycleStatus.Restarted, result.Status);
        Assert.Equal("C:\\qbittorrent\\qbittorrent.exe", executable);
        Assert.Equal("C:\\qbittorrent", workingDirectory);
        Assert.Equal(new[] { "--profile", "QA Profile", "--webui-port=8080" }, arguments);
        Assert.Equal(new ProcessIdentity(72, 223456), result.RestartedProcess);
    }

    [Fact]
    public async Task Restart_is_blocked_when_launch_context_is_unavailable()
    {
        var calls = 0;
        var lifecycle = Create((_, _) => new(OwnerLookupStatus.Available, Owner, null, true, "launchContextUnavailable")
            { RestartBlocked = true, RestartReasonCode = "launchContextUnavailable" });

        var result = await lifecycle.RestartAsync(Endpoint, _ => { calls++; return Task.CompletedTask; });

        Assert.Equal(TargetLifecycleStatus.Blocked, result.Status);
        Assert.Equal("launchContextUnavailable", result.ReasonCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Restart_does_not_launch_if_another_listener_appears_after_exit()
    {
        var context = CreateContext(Owner);
        var lookups = 0;
        var launches = 0;
        var lifecycle = Create((_, capture) =>
        {
            lookups++;
            if (!capture && lookups == 3) return new(OwnerLookupStatus.Ambiguous, null, "listenerAmbiguous", true, "listenerAmbiguous");
            return Available(Owner, capture ? context : null);
        }, _ => new(TargetLifecycle.ProcessProbeState.Exited), _ => { launches++; return Owner; }, (_, _) => Task.CompletedTask);

        var result = await lifecycle.RestartAsync(Endpoint, _ => Task.CompletedTask);

        Assert.Equal(TargetLifecycleStatus.Blocked, result.Status);
        Assert.Equal("listenerAmbiguous", result.ReasonCode);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task Caller_cancellation_is_reported_without_process_termination()
    {
        using var cancellation = new CancellationTokenSource();
        var lifecycle = Create((_, _) => Available(Owner));
        var result = await lifecycle.StopAsync(Endpoint, token =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        }, token: cancellation.Token);

        Assert.Equal(TargetLifecycleStatus.Cancelled, result.Status);
        Assert.Equal("lifecycleCancelled", result.ReasonCode);
    }

    [Fact]
    public async Task Owner_exit_wait_has_a_bounded_deadline()
    {
        var shutdownCalls = 0;
        var lifecycle = Create((_, _) => Available(Owner),
            _ => new(TargetLifecycle.ProcessProbeState.Running),
            delay: static (duration, token) => Task.Delay(duration, token));

        var result = await lifecycle.StopAsync(Endpoint, _ => { shutdownCalls++; return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(TargetLifecycleStatus.TimedOut, result.Status);
        Assert.Equal("lifecycleTimeout", result.ReasonCode);
        Assert.Equal(1, shutdownCalls);
    }

    [Fact]
    public async Task Shutdown_failure_never_attempts_a_restart()
    {
        var launches = 0;
        var lifecycle = Create((_, capture) => Available(Owner, capture ? CreateContext(Owner) : null),
            _ => new(TargetLifecycle.ProcessProbeState.Exited), _ => { launches++; return Owner; });

        var result = await lifecycle.RestartAsync(Endpoint,
            _ => Task.FromException(new InvalidOperationException("mock shutdown failed")));

        Assert.Equal(TargetLifecycleStatus.ShutdownFailed, result.Status);
        Assert.Equal("gracefulShutdownFailed", result.ReasonCode);
        Assert.Equal(0, launches);
    }

    private static TargetLifecycle Create(Func<string, bool, ProcessOwnerObservation> owner,
        Func<ProcessIdentity, TargetLifecycle.ProcessProbe>? probe = null,
        Func<ProcessLaunchContext, ProcessIdentity?>? launch = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(owner, probe ?? (_ => new(TargetLifecycle.ProcessProbeState.Exited)),
            launch ?? (_ => null), delay ?? ((_, _) => Task.CompletedTask));

    private static ProcessOwnerObservation Available(ProcessIdentity identity, ProcessLaunchContext? context = null) =>
        new(OwnerLookupStatus.Available, identity, null, context is null, context is null ? "launchContextUnavailable" : null)
        { LaunchContext = context };

    private static ProcessLaunchContext CreateContext(ProcessIdentity identity) =>
        (ProcessLaunchContext)Activator.CreateInstance(typeof(ProcessLaunchContext), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [identity, "C:\\qbittorrent\\qbittorrent.exe", "C:\\qbittorrent",
                new[] { "--profile", "QA Profile", "--webui-port=8080" }], culture: null)!;
}
