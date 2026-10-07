using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using GameValueEditor.Models;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Services;
using GameValueEditor.Services.Adapters;
using GameValueEditor.ViewModels;

internal static class BackgroundOperationRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _assertions;

    internal static async Task RunAsync(string root, string[]? args = null)
    {
        _assertions = 0;
        var area = args?.SingleOrDefault(arg => arg.StartsWith("--boundary-area=", StringComparison.Ordinal))?["--boundary-area=".Length..];
        if (area is not (null or "locks" or "modules" or "scan")) throw new ArgumentException("Unknown boundary test area.");
        if (area is null or "locks") await CheckModuleLockCancellationAsync(Path.Combine(root, "locks"));
        if (area is null or "modules") await CheckUninstallIsolationAsync(Path.Combine(root, "modules"));
        if (area is null or "scan") await CheckNextScanReadsAsync(Path.Combine(root, "scan"));
        Console.WriteLine($"Background operation regressions passed: {_assertions} assertions (lock cancellation, module isolation, next-scan read failures).");
    }

    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { _assertions++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static async Task CheckModuleLockCancellationAsync(string root)
    {
        foreach (var scenario in new[] { "cancel-read", "unlock-read", "change-target", "remove-field", "cancel-write", "cancel-error", "changed-error", "unchanged", "normal", "policy" })
        {
            using var fixture = new Fixture(Path.Combine(root, scenario));
            var version = new GameVersionProfile();
            var originalVerified = DateTime.UnixEpoch;
            var field = new SavedField { LocatorKind = "GameAdapter", AdapterId = "game.review", AdapterFieldKey = ModuleFieldKey.Create("fixture.inventory", "item", "count"),
                IsValueLocked = true, LockedValue = "42", CurrentValue = "旧显示", Status = "旧状态", LastVerifiedUtc = originalVerified };
            version.Fields.Add(field);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var adapter = new ProbeAdapter("game.review", "review") { CanLock = scenario != "policy", Value = scenario == "unchanged" ? "42" : "7" };
            adapter.OnRead = () =>
            {
                if (adapter.Reads > 1) { cancellation.Cancel(); return; }
                if (scenario is "cancel-read" or "cancel-error") cancellation.Cancel();
                if (scenario is "unlock-read" or "change-target" or "changed-error" or "remove-field")
                    fixture.Dispatcher.Invoke(() =>
                    {
                        if (scenario == "unlock-read") { field.IsValueLocked = false; field.LockedValue = ""; }
                        else if (scenario == "remove-field") version.Fields.Remove(field);
                        else field.LockedValue = "99";
                    });
                if (scenario is "cancel-error" or "changed-error") throw new IOException("迟到读取错误");
            };
            adapter.OnWrite = () => { if (scenario == "cancel-write") cancellation.Cancel(); };
            if (scenario is "normal" or "unchanged") field.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(SavedField.Status)) cancellation.Cancel(); };
            await Task.Run(() => (Task)Invoke(fixture.Vm, "MaintainLockedValuesAsync", SelfProcess("review"), version, adapter.Adapter, cancellation.Token)!);
            if (scenario is "normal" or "unchanged")
            {
                Check(adapter.Writes == (scenario == "normal" ? 1 : 0) && field.CurrentValue == "42", "Valid module lock stopped working or introduced extra writes.");
                Check(field.Status.StartsWith("锁定中", StringComparison.Ordinal) && field.LastVerifiedUtc > originalVerified, "Valid module lock did not refresh its actual result.");
            }
            else
            {
                Check(adapter.Writes == (scenario == "cancel-write" ? 1 : 0), $"Stale module lock initiated a write: {scenario}.");
                Check(field.CurrentValue == "旧显示" && field.Status == "旧状态" && field.LastVerifiedUtc == originalVerified, $"Late module result/error changed the UI: {scenario}.");
                if (scenario == "cancel-write") Check(adapter.WrittenValue == "42", "Already-entered module write did not use its captured target.");
            }
            if (scenario == "policy") Check(adapter.Reads == 0, "A field policy that forbids locking was ignored.");
        }
    }

    private static async Task CheckUninstallIsolationAsync(string root)
    {
        foreach (var operation in new[] { "uninstall", "deactivate", "legacy-uninstall", "legacy-deactivate" })
        foreach (var selectA in new[] { true, false })
        {
            using var fixture = new Fixture(Path.Combine(root, operation, selectA ? "active-a" : "background-a"));
            var a = fixture.AddGame("A", "game.a");
            var b = fixture.AddGame("B", "game.b");
            var adapterA = new ProbeAdapter("game.a", "A");
            var targetId = operation.StartsWith("legacy", StringComparison.Ordinal) ? "game.old-a" : "game.a";
            if (targetId != adapterA.Id) adapterA.Aliases = [targetId];
            var adapterB = new ProbeAdapter("game.b", "B");
            fixture.Adapters.Add(adapterA.Adapter); fixture.Adapters.Add(adapterB.Adapter);
            var sa = fixture.AddSession(a, adapterA.Adapter);
            var sb = fixture.AddSession(b, adapterB.Adapter);
            fixture.Vm.SelectedGame = selectA ? a : b;
            // These tokens stand for already running maintenance. Native writes are never used.
            var cancellationA = new CancellationTokenSource();
            var cancellationB = new CancellationTokenSource();
            sa.LockMaintenanceCancellation = cancellationA;
            sb.LockMaintenanceCancellation = cancellationB;
            var fieldB = new SavedField { IsValueLocked = true, LockedValue = "42" };
            b.Versions[0].Fields.Add(fieldB);
            fixture.Register(targetId);
            if (operation.EndsWith("uninstall", StringComparison.Ordinal))
                Check(await (Task<bool>)Invoke(fixture.Vm, "RemoveInstalledModuleCoreAsync", targetId)!, "Module uninstall failed.");
            else Invoke(fixture.Vm, "DeactivateModuleUntilRestart", targetId);
            Check(!cancellationB.IsCancellationRequested && ReferenceEquals(sb.LockMaintenanceCancellation, cancellationB), "Uninstall cancelled or replaced another game's maintenance.");
            Check(fieldB.IsValueLocked && sb.Adapter == adapterB.Adapter && b.IsConnected, "Uninstall changed another game's lock, adapter or connection.");
            Check(cancellationA.IsCancellationRequested && sa.Adapter is null, "Removed module's maintenance/adapter remained active.");
            if (selectA) Check(Get<IGameAdapter?>(fixture.Vm, "_activeAdapter") is null, "Canonical/legacy stopped module remained exposed by the active view.");
            Check((fixture.Catalog.FindInstalled(targetId) is null) == operation.EndsWith("uninstall", StringComparison.Ordinal), "Uninstall/deactivate changed the wrong registration state.");
        }

        foreach (var scenario in new[] { "native-restart", "stale-build", "disconnected", "shutdown" })
        {
            using var fixture = new Fixture(Path.Combine(root, scenario));
            var a = fixture.AddGame("A", "game.a");
            var b = fixture.AddGame("B", "game.b");
            var adapterA = new ProbeAdapter("game.a", "A");
            fixture.Adapters.Add(adapterA.Adapter);
            var sa = fixture.AddSession(a, adapterA.Adapter);
            fixture.AddSession(b, null);
            fixture.Vm.SelectedGame = b;
            var field = new SavedField { IsValueLocked = true, LockedValue = "42", LastAddress = 1, ProcessStartTimeUtcTicks = sa.Process.StartTimeUtc.Ticks };
            a.Versions[0].Fields.Add(field);
            using var memory = new ProbeMemory();
            fixture.Vm.MemoryWriteAccessFactory = _ => memory;
            var previous = new CancellationTokenSource(); sa.LockMaintenanceCancellation = previous;
            fixture.Register("game.a");
            if (scenario == "stale-build") a.Versions[0].ExecutableSha256 = "different";
            FileStream? occupied = null;
            try
            {
                if (scenario is "disconnected" or "shutdown")
                    occupied = new FileStream(Path.Combine(root, scenario, "modules", "packages", "game.a", "0.1.0", "module.json"), FileMode.Open, FileAccess.Read, FileShare.None);
                var removal = (Task<bool>)Invoke(fixture.Vm, "RemoveInstalledModuleCoreAsync", "game.a")!;
                if (occupied is not null)
                {
                    Check(!removal.IsCompleted && sa.LockMaintenanceCancellation is null, "Uninstall did not suspend the affected task before its asynchronous wait.");
                    if (scenario == "disconnected") Invoke(fixture.Vm, "DisconnectSession", sa, false);
                    else fixture.Vm.Shutdown();
                    occupied.Dispose(); occupied = null;
                }
                await removal;
                if (scenario == "native-restart")
                {
                    Check(sa.LockMaintenanceCancellation is not null && !ReferenceEquals(sa.LockMaintenanceCancellation, previous), "Background game's native locks were not restored after module uninstall.");
                    var deadline = Stopwatch.StartNew();
                    while (memory.Reads == 0 && deadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10);
                    Check(memory.Reads > 0 && memory.Writes == 0, "Restored background lock did not run the verified read pipeline.");
                }
                else Check(memory.Reads == 0 && memory.Writes == 0, "Invalid/disconnected/closing session restarted memory maintenance.");
            }
            finally { occupied?.Dispose(); previous.Dispose(); }
        }
    }

    private static async Task CheckNextScanReadsAsync(string root)
    {
        var process = SelfProcess("review");
        foreach (var scenario in new[] { "all-failed", "zero-match", "partial", "page-fallback", "empty", "short-read", "cancel", "identity", "requested-pid" })
        {
            var reader = new ProbeReader(process) { FailAll = scenario == "all-failed", FailPage = scenario is "page-fallback" or "short-read", ShortRead = scenario == "short-read" };
            if (scenario == "partial") reader.UnreadableAddress = 0x20004;
            if (scenario == "identity") reader.StartTimeUtc = reader.StartTimeUtc.AddTicks(1);
            using var source = CreateSource(Path.Combine(root, scenario), process, scenario == "empty" ? [] : [0x20000, 0x20004]);
            var scanner = new MemoryScanService(Path.Combine(root, scenario), _ => reader);
            using var cancellation = new CancellationTokenSource();
            if (scenario == "cancel") reader.OnRead = cancellation.Cancel;
            var pid = scenario == "requested-pid" ? process.ProcessId + 1 : process.ProcessId;
            if (scenario is "all-failed" or "short-read")
                await ExpectAsync<IOException>(async () => { using var output = (await scanner.NextScanAsync(pid, source, ScanComparison.Unchanged, _ => null, null, cancellation.Token)).Candidates; });
            else if (scenario is "identity" or "requested-pid")
                await ExpectAsync<InvalidOperationException>(async () => { using var output = (await scanner.NextScanAsync(pid, source, ScanComparison.Unchanged, _ => null, null, cancellation.Token)).Candidates; });
            else if (scenario == "cancel")
                await ExpectAsync<OperationCanceledException>(async () => { using var output = (await scanner.NextScanAsync(pid, source, ScanComparison.Unchanged, _ => null, null, cancellation.Token)).Candidates; });
            else
            {
                var comparison = scenario == "zero-match" ? ScanComparison.Exact : ScanComparison.Unchanged;
                using var output = (await scanner.NextScanAsync(pid, source, comparison, _ => BitConverter.GetBytes(99), null, cancellation.Token)).Candidates;
                Check(output.Count == (scenario is "empty" or "zero-match" ? 0 : scenario == "partial" ? 1 : 2), $"Wrong next-scan result: {scenario}.");
            }
            Check(reader.Disposed, "Next scan retained its memory reader.");
            Check(source.Count == (scenario == "empty" ? 0 : 2) && source.ReadCandidates(10).Count == source.Count, "Next scan damaged its source candidates.");
            Check(Directory.EnumerateDirectories(Path.Combine(root, scenario)).Count() == 1, "Failed/completed next scan left a temporary output behind.");
        }

        // Real ReadProcessMemory failure; the page belongs only to this test process.
        var page = VirtualAlloc(0, (nuint)Environment.SystemPageSize, 0x3000, 4);
        Check(page != 0, "Native next-scan test allocation failed.");
        try
        {
            Marshal.WriteInt32(page, 7);
            Check(VirtualProtect(page, (nuint)Environment.SystemPageSize, 1, out _), "Native next-scan page could not become NOACCESS.");
            using var fixture = new Fixture(Path.Combine(root, "native-view"));
            var game = fixture.AddGame("review", "");
            fixture.Vm.SelectedGame = game;
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.AttachedProcess))!.SetValue(fixture.Vm, process);
            var address = unchecked((ulong)page.ToInt64());
            using var source = CreateSource(Path.Combine(root, "native-current"), process, Enumerable.Repeat(address, 100).ToArray());
            using var previous = CreateSource(Path.Combine(root, "native-history"), process, [address]);
            Set(fixture.Vm, "_scanCandidates", source);
            var history = Get<Stack<ScanCandidateStore>>(fixture.Vm, "_scanHistory"); history.Push(previous);
            var preview = new ObservableCollection<ScanCandidate>(source.ReadCandidates(100));
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.VisibleScanResults))!.SetValue(fixture.Vm, preview);
            fixture.Vm.SelectedScanResult = preview[0]; fixture.Vm.SelectedComparison = ScanComparison.Unchanged;
            await ExpectAsync<IOException>(() => fixture.Vm.RunScanAsync(false));
            Check(ReferenceEquals(Get<ScanCandidateStore>(fixture.Vm, "_scanCandidates"), source) && source.Count == 100, "All-unreadable next scan replaced the current generation.");
            Check(history.Count == 1 && ReferenceEquals(history.Peek(), previous), "Failed next scan changed undo history.");
            Check(ReferenceEquals(fixture.Vm.VisibleScanResults, preview) && fixture.Vm.SelectedScanResult == preview[0] && !fixture.Vm.IsBusy, "Failed next scan changed preview/selection or kept the UI busy.");
        }
        finally { Check(VirtualFree(page, 0, 0x8000), "Native next-scan page could not be released."); }
    }

    private static ScanCandidateStore CreateSource(string root, ProcessItem process, IReadOnlyList<ulong> addresses)
    {
        var directory = Path.Combine(root, "scan-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "part.bin");
        using (var writer = new BinaryWriter(File.Create(path)))
            foreach (var address in addresses) { writer.Write(address); writer.Write(7); writer.Write(7); }
        return new ScanCandidateStore(root, directory, [new ScanCandidatePartition { FilePath = path, Count = addresses.Count,
            ValueType = MemoryValueType.Int32, FirstBytes = BitConverter.GetBytes(7), SearchRoutineId = SearchRoutineIds.DirectNumeric, SearchRoutineName = "test", ScaleMultiplier = 1 }],
            new FileStream(Path.Combine(directory, "store.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            { ProcessId = process.ProcessId, ProcessStartTimeUtc = process.StartTimeUtc };
    }

    private static ProcessItem SelfProcess(string name)
    {
        using var process = Process.GetCurrentProcess();
        return new() { ProcessId = process.Id, StartTimeUtc = process.StartTime.ToUniversalTime(), ProcessName = name };
    }

    private static object? Invoke(object target, string name, params object?[] args)
    {
        try { return target.GetType().GetMethod(name, Private)!.Invoke(target, args); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
    }
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly GameAdapterRegistry _registry;
        internal MainViewModel Vm { get; }
        internal GameModuleCatalogService Catalog { get; }
        internal Dispatcher Dispatcher { get; } = Dispatcher.CurrentDispatcher;
        internal Dictionary<Guid, GameConnectionSession> Sessions => Get<Dictionary<Guid, GameConnectionSession>>(Vm, "_sessions");
        internal List<IGameAdapter> Adapters => Get<List<IGameAdapter>>(_registry, "_adapters");
        internal Fixture(string root)
        {
            _root = root; Catalog = new(Path.Combine(root, "modules")); _registry = new(Path.Combine(root, "modules"));
            Vm = ModuleLifecycleRegressionTests.CreateViewModel(root, Catalog, _registry);
        }
        internal GameProfile AddGame(string name, string moduleId)
        {
            var game = new GameProfile { Name = name, ModuleId = moduleId, Versions = [new() { ExecutableSha256 = name }] };
            Vm.Games.Add(game); return game;
        }
        internal GameConnectionSession AddSession(GameProfile game, IGameAdapter? adapter)
        {
            var process = SelfProcess(game.Name);
            var session = new GameConnectionSession(new() { SeedProcess = process, RootProcess = process, DataProcess = process, Members = [process], RuntimeKind = GameRuntimeKind.Native }, game.Id)
                { Adapter = adapter, VersionId = game.Versions[0].Id, Fingerprint = new("", "", "", game.Name, 0, "x64", game.Name, "", "") };
            Sessions.Add(game.Id, session); game.IsConnected = true; return session;
        }
        internal void Register(string id)
        {
            var directory = Path.Combine(_root, "modules", "packages", id, "0.1.0"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "module.json"), System.Text.Json.JsonSerializer.Serialize(new InstalledModuleManifest { Id = id, Version = "0.1.0" }));
            Catalog.RestoreRegistration(new(id, "0.1.0", DateTime.UtcNow));
        }
        public void Dispose() { Vm.Shutdown(); _registry.Dispose(); }
    }

    // Reuse the single sample export: another IGameAdapter type in this assembly
    // would also be discovered when the smoke-test DLL is installed as a module.
    internal sealed class ProbeAdapter
    {
        internal string Id { get; }
        internal string ProcessName { get; }
        internal SmokeTestModuleAdapter Adapter { get; }
        internal ProbeAdapter(string id, string processName)
        {
            Id = id; ProcessName = processName;
            Adapter = new SmokeTestModuleAdapter { BoundaryProbe = this };
        }
        internal string[] Aliases = [];
        internal string Value = "7";
        internal string? WrittenValue;
        internal int Reads, Writes;
        internal bool CanLock = true;
        internal Action? OnRead, OnWrite;
        internal AdapterFieldValue ReadField(string fieldKey) { Reads++; OnRead?.Invoke(); return new(fieldKey, Value, "test read"); }
        internal AdapterFieldValue WriteField(string fieldKey, string displayValue) { Writes++; WrittenValue = displayValue; OnWrite?.Invoke(); return new(fieldKey, displayValue, "test write"); }
    }

    private sealed class ProbeMemory : IMemoryWriteAccess
    {
        private int _reads;
        internal int Reads => Volatile.Read(ref _reads);
        internal int Writes;
        public void EnsureInstance(int processId, DateTime startTimeUtc) { }
        public bool TryGetModuleBase(string moduleName, out ulong baseAddress) { baseAddress = 0; return false; }
        public bool TryRead(ulong address, int length, out byte[] data) { Interlocked.Increment(ref _reads); data = BitConverter.GetBytes(42); return true; }
        public bool TryWrite(ulong address, byte[] data, out string error) { Interlocked.Increment(ref Writes); error = ""; return true; }
        public void Dispose() { }
    }

    private sealed class ProbeReader(ProcessItem process) : IScanMemoryReader
    {
        internal bool FailAll, FailPage, ShortRead, Disposed;
        internal ulong? UnreadableAddress;
        internal Action? OnRead;
        public int ProcessId => process.ProcessId;
        public DateTime StartTimeUtc { get; set; } = process.StartTimeUtc;
        public int RegionQueryCount => 0;
        public int RegionQueryError => 0;
        public IReadOnlyList<MemoryRegion> EnumerateReadableRegions(bool writableOnly) => throw new InvalidOperationException("Next scan must not enumerate the process.");
        public bool TryRead(ulong address, int length, out byte[] data)
        {
            OnRead?.Invoke(); data = [];
            if (FailAll || (length == 4096 && (FailPage || UnreadableAddress.HasValue)) || address == UnreadableAddress) return false;
            data = length == 4096 ? Enumerable.Range(0, 1024).SelectMany(_ => BitConverter.GetBytes(7)).ToArray() : BitConverter.GetBytes(7);
            if (ShortRead) data = data[..^1];
            return true;
        }
        public void Dispose() => Disposed = true;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint oldProtection);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool VirtualFree(nint address, nuint size, uint freeType);
}
