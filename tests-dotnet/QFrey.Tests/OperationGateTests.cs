using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Tuning;
using Xunit;

namespace QFrey.Tests;

public sealed class OperationGateTests
{
    [Fact]
    public async Task SerializesExplicitlyQueuedCallbacksWithoutOverlap()
    {
        using var gate = new OperationGate(queueCapacity: 1);
        var firstEntered = Signal();
        var releaseFirst = Signal();
        var overlap = 0;
        var maximum = 0;

        var first = gate.RunAsync(async token =>
        {
            Enter();
            firstEntered.SetResult();
            try { await releaseFirst.Task.WaitAsync(token); return 1; }
            finally { Interlocked.Decrement(ref overlap); }
        });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = gate.RunAsync(async token =>
        {
            Enter();
            try { await Task.Yield(); token.ThrowIfCancellationRequested(); return 2; }
            finally { Interlocked.Decrement(ref overlap); }
        }, queueIfBusy: true);

        Assert.False(second.IsCompleted);
        releaseFirst.SetResult();
        Assert.Equal(1, await first);
        Assert.Equal(2, await second);
        Assert.Equal(1, maximum);

        void Enter()
        {
            var active = Interlocked.Increment(ref overlap);
            Interlocked.Exchange(ref maximum, Math.Max(maximum, active));
        }
    }

    [Fact]
    public async Task RejectsConflictsAndQueueOverflow()
    {
        using var gate = new OperationGate(queueCapacity: 1);
        var entered = Signal();
        var release = Signal();
        var active = gate.RunAsync(async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = gate.RunAsync(_ => Task.CompletedTask, queueIfBusy: true);

        await Assert.ThrowsAsync<QbittorrentException>(() => gate.RunAsync(_ => Task.CompletedTask));
        var overflow = await Assert.ThrowsAsync<QbittorrentException>(() => gate.RunAsync(_ => Task.CompletedTask, queueIfBusy: true));
        Assert.Equal(ErrorCodes.OperationConflict, overflow.Code);

        release.SetResult();
        await active;
        await queued;
    }

    [Fact]
    public async Task CancellationRemovesQueuedWorkAndReleasesCapacity()
    {
        using var gate = new OperationGate(queueCapacity: 1);
        var entered = Signal();
        var release = Signal();
        var active = gate.RunAsync(async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var invoked = false;
        var cancelled = gate.RunAsync(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, cancellation.Token, queueIfBusy: true);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        var replacement = gate.RunAsync(_ => Task.CompletedTask, queueIfBusy: true);
        release.SetResult();
        await active;
        await replacement;
        Assert.False(invoked);
    }

    [Fact]
    public async Task CallbackFailureIsObservedAndDoesNotStallSuccessor()
    {
        using var gate = new OperationGate(queueCapacity: 1);
        var entered = Signal();
        var fail = Signal();
        var active = gate.RunAsync<int>(async token =>
        {
            entered.SetResult();
            await fail.Task.WaitAsync(token);
            throw new InvalidOperationException("expected");
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var successor = gate.RunAsync(_ => Task.FromResult(42), queueIfBusy: true);
        fail.SetResult();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => active);
        Assert.Equal("expected", error.Message);
        Assert.Equal(42, await successor);
    }

    [Fact]
    public async Task ImmediateCompletionAndQueuedCancellationRaceDoesNotInvokeCancelledCallback()
    {
        using var gate = new OperationGate(queueCapacity: 1);
        using var cancellation = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        var first = gate.RunAsync(async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var invoked = 0;
        var queued = gate.RunAsync(async token =>
        {
            Interlocked.Increment(ref invoked);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, cancellation.Token, queueIfBusy: true);

        using var barrier = new Barrier(3);
        var finish = Task.Run(() => { barrier.SignalAndWait(); release.SetResult(); });
        var cancel = Task.Run(() => { barrier.SignalAndWait(); cancellation.Cancel(); });
        barrier.SignalAndWait();
        await Task.WhenAll(finish, cancel);
        await first;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.InRange(Volatile.Read(ref invoked), 0, 1);
        await gate.RunAsync(_ => Task.CompletedTask);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
