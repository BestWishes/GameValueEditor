namespace GameValueEditor.Services;

// FIFO primitive for a semantic target or native byte, owned by FieldOperationCoordinator.
internal sealed class SavedFieldOperationQueue
{
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;
    private long _generation;
    private int _pendingWrites;

    internal Lease EnterRead() => Enter(false);
    internal Lease EnterWrite() => Enter(true);
    internal void Invalidate() { lock (_sync) _generation++; }

    private Lease Enter(bool write)
    {
        lock (_sync)
        {
            if (write) { _generation++; _pendingWrites++; }
            return Append(write);
        }
    }

    internal bool TryEnterMaintenance(out Lease? lease)
    {
        lock (_sync)
        {
            lease = null;
            if (_pendingWrites != 0 || !_tail.IsCompleted) return false;
            lease = Append(false);
            return true;
        }
    }

    private Lease Append(bool write)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new Lease(this, _tail, completed, write, _generation);
        _tail = completed.Task;
        return lease;
    }

    internal sealed class Lease(SavedFieldOperationQueue owner, Task ready, TaskCompletionSource completed, bool write, long generation) : IDisposable
    {
        private int _disposed;
        internal Task Ready => ready;
        internal bool IsCurrent => Volatile.Read(ref owner._generation) == generation;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (ready.IsCompleted) Release();
            else _ = ReleaseWhenReadyAsync();
        }
        private async Task ReleaseWhenReadyAsync() { await ready.ConfigureAwait(false); Release(); }
        private void Release()
        {
            lock (owner._sync) { if (write) owner._pendingWrites--; }
            completed.SetResult();
        }
    }
}
