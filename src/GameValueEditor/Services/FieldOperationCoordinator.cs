using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Services;

// A runtime is shared across profile aliases and reconnections to the same live process.
// Active targets retain the scope; idle scopes can be collected without losing an in-flight fence.
internal sealed class FieldOperationCoordinator
{
    private readonly object _sync = new();
    private readonly Dictionary<(int Pid, long Started, string Build), WeakReference<Scope>> _scopes = [];

    internal Scope For(int pid, long started, string build)
    {
        lock (_sync)
        {
            var key = (pid, started, build.ToUpperInvariant());
            if (_scopes.TryGetValue(key, out var weak) && weak.TryGetTarget(out var existing)) return existing;
            foreach (var stale in _scopes.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToList())
                _scopes.Remove(stale);
            var scope = new Scope();
            _scopes[key] = new(scope);
            return scope;
        }
    }

    internal static string CanonicalKey(string key) =>
        ModuleFieldKey.TryParse(key, out var editor, out var entity, out var field)
            ? ModuleFieldKey.Create(editor, entity, field) : key;

    internal sealed class Scope
    {
        private readonly object _sync = new();
        private readonly Dictionary<(string Module, string Key), SavedFieldOperationQueue> _module = [];
        private readonly Dictionary<ulong, SavedFieldOperationQueue> _bytes = [];
        private readonly Dictionary<string, ModuleRevision> _revisions = [];

        private ModuleRevision Revision(string moduleId)
        {
            if (!_revisions.TryGetValue(moduleId, out var revision)) _revisions[moduleId] = revision = new();
            return revision;
        }
        internal Snapshot CaptureModuleSnapshot(string moduleId)
        {
            lock (_sync)
            {
                var revision = Revision(moduleId);
                return new(this, revision, revision.Generation, revision.Writes == 0);
            }
        }
        internal sealed class ModuleRevision
        {
            internal long Generation;
            internal int Writes;
        }
        internal sealed class Snapshot(Scope owner, ModuleRevision revision, long generation, bool idleAtStart)
        {
            private bool Current => idleAtStart && revision.Writes == 0 && revision.Generation == generation;
            internal bool IsCurrent { get { lock (owner._sync) return Current; } }
            internal bool TryApply(Action apply)
            {
                lock (owner._sync)
                {
                    if (!Current) return false;
                    apply();
                    return true;
                }
            }
        }
        private sealed class Mutation(Scope owner, ModuleRevision revision) : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                lock (owner._sync) { revision.Writes--; revision.Generation++; }
            }
        }
        private IDisposable? BeginMutation(ModuleRevision? revision)
        {
            if (revision is null) return null;
            lock (_sync) { revision.Writes++; revision.Generation++; return new Mutation(this, revision); }
        }

        internal Target Module(string moduleId, string key)
        {
            lock (_sync)
            {
                var identity = (moduleId, CanonicalKey(key));
                if (!_module.TryGetValue(identity, out var queue)) _module[identity] = queue = new();
                return new(this, [queue], revision: Revision(moduleId));
            }
        }
        internal Target Native(ulong address, int size)
        {
            if (size is not (4 or 8) || address > ulong.MaxValue - (ulong)(size - 1))
                throw new InvalidOperationException("字段地址范围无效。");
            lock (_sync)
            {
                var queues = new SavedFieldOperationQueue[size];
                for (var i = 0; i < size; i++)
                {
                    var key = address + (ulong)i;
                    if (!_bytes.TryGetValue(key, out var queue)) _bytes[key] = queue = new();
                    queues[i] = queue;
                }
                return new(this, queues, address);
            }
        }

        internal sealed class Target(Scope owner, SavedFieldOperationQueue[] queues, ulong? nativeAddress = null,
            ModuleRevision? revision = null)
        {
            internal ulong? NativeAddress => nativeAddress;
            internal Turn EnterRead() => Enter(false);
            internal Turn EnterWrite() => Enter(true);
            private Turn Enter(bool write)
            {
                lock (owner._sync) return new(this, queues.Select(q => write ? q.EnterWrite() : q.EnterRead()).ToArray(),
                    write ? owner.BeginMutation(revision) : null);
            }
            // Maintenance reserves a read turn first, and registers a mutation only if it must write.
            internal IDisposable? BeginMutation() => owner.BeginMutation(revision);
            internal void Invalidate() { lock (owner._sync) foreach (var q in queues) q.Invalidate(); }
            internal bool TryEnterMaintenance(out Turn? turn)
            {
                lock (owner._sync)
                {
                    var leases = new List<SavedFieldOperationQueue.Lease>();
                    foreach (var q in queues)
                    {
                        if (!q.TryEnterMaintenance(out var lease))
                        {
                            foreach (var acquired in leases) acquired.Dispose();
                            turn = null; return false;
                        }
                        leases.Add(lease!);
                    }
                    turn = new(this, leases.ToArray()); return true;
                }
            }
        }
        internal sealed class Turn(Target target, SavedFieldOperationQueue.Lease[] leases, IDisposable? mutation = null) : IDisposable
        {
            // Retain the target and scope until all predecessors have completed, even if cancelled early.
            private readonly Target _target = target;
            private int _disposed;
            internal Task Ready { get; } = Task.WhenAll(leases.Select(l => l.Ready));
            internal bool IsCurrent => leases.All(l => l.IsCurrent);
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                foreach (var lease in leases) lease.Dispose();
                if (Ready.IsCompleted) mutation?.Dispose();
                else _ = RetainUntilReadyAsync();
            }
            private async Task RetainUntilReadyAsync()
            { await Ready.ConfigureAwait(false); mutation?.Dispose(); GC.KeepAlive(_target); }
        }
    }
}
