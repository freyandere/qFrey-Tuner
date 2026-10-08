using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;

namespace QFrey.Core.Tuning;

public sealed class OperationGate : IDisposable
{
    public const int DefaultQueueCapacity = 2;
    private const int MaxQueueCapacity = 32;
    private readonly object sync = new();
    private readonly LinkedList<WorkItem> queue = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly int queueCapacity;
    private bool running;
    private bool disposed;

    public OperationGate(int queueCapacity = DefaultQueueCapacity)
    {
        if (queueCapacity is < 0 or > MaxQueueCapacity) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        this.queueCapacity = queueCapacity;
    }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default,
        bool queueIfBusy = false)
    {
        ArgumentNullException.ThrowIfNull(operation);
        WorkItem<T> item;
        bool start;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            item = new WorkItem<T>(operation, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token));
            start = !running;
            if (start) running = true;
            else if (!queueIfBusy || queue.Count >= queueCapacity)
            {
                item.Dispose();
                throw new QbittorrentException(ErrorCodes.OperationConflict);
            }
            else
            {
                item.Node = queue.AddLast(item);
                var registration = item.Token.Register(static state =>
                {
                    var (gate, queued) = ((OperationGate, WorkItem))state!;
                    gate.CancelQueued(queued);
                }, (this, (WorkItem)item));
                if (item.Node?.List == queue) item.Registration = registration;
                else registration.Unregister();
            }
        }

        if (start) Start(item);
        return await item.Task.ConfigureAwait(false);
    }

    public async Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default,
        bool queueIfBusy = false)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await RunAsync(async token => { await operation(token).ConfigureAwait(false); return true; }, cancellationToken, queueIfBusy)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        WorkItem[] pending;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            pending = queue.ToArray();
            queue.Clear();
            foreach (var item in pending) item.Node = null;
        }
        lifetime.Cancel();
        foreach (var item in pending)
        {
            item.Cancel();
            item.Dispose();
        }
        lifetime.Dispose();
    }

    private void CancelQueued(WorkItem item)
    {
        bool removed;
        lock (sync)
        {
            removed = item.Node?.List == queue;
            if (removed)
            {
                queue.Remove(item.Node!);
                item.Node = null;
            }
        }
        if (removed)
        {
            item.Cancel();
            item.Dispose();
        }
    }

    private void Start(WorkItem item) => _ = ObserveProcessAsync(item);

    private async Task ObserveProcessAsync(WorkItem item)
    {
        try { await ProcessAsync(item).ConfigureAwait(false); }
        catch (Exception error) { item.Fail(error); }
    }

    private async Task ProcessAsync(WorkItem item)
    {
        Exception? failure = null;
        var canceled = false;
        try
        {
            item.Token.ThrowIfCancellationRequested();
            await item.RunAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (item.Token.IsCancellationRequested) { canceled = true; }
        catch (Exception error) { failure = error; }
        finally
        {
            WorkItem? next;
            lock (sync)
            {
                next = queue.First?.Value;
                if (next is null) running = false;
                else
                {
                    queue.RemoveFirst();
                    next.Node = null;
                }
            }
            item.Dispose();
            if (next is not null) Start(next);
            item.Complete(failure, canceled);
        }
    }

    private abstract class WorkItem(CancellationTokenSource cancellation) : IDisposable
    {
        protected CancellationTokenSource Cancellation { get; } = cancellation;
        public CancellationToken Token { get; } = cancellation.Token;
        public LinkedListNode<WorkItem>? Node { get; set; }
        public CancellationTokenRegistration Registration { get; set; }
        private int disposed;
        public abstract Task RunAsync();
        public abstract void Cancel();
        public abstract void Fail(Exception error);
        public abstract void Complete(Exception? error, bool canceled);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Registration.Unregister();
            Cancellation.Dispose();
        }
    }

    private sealed class WorkItem<T>(Func<CancellationToken, Task<T>> operation, CancellationTokenSource cancellation)
        : WorkItem(cancellation)
    {
        private readonly TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private T result = default!;
        public Task<T> Task => completion.Task;
        public override async Task RunAsync() => result = await operation(Token).ConfigureAwait(false);
        public override void Cancel() => completion.TrySetCanceled(Token);
        public override void Fail(Exception error) => completion.TrySetException(error);
        public override void Complete(Exception? error, bool canceled)
        {
            if (canceled) completion.TrySetCanceled(Token);
            else if (error is not null) completion.TrySetException(error);
            else completion.TrySetResult(result);
        }
    }
}
