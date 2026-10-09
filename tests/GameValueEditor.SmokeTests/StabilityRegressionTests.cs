using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using GameValueEditor.Models;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Services;
using GameValueEditor.Services.Adapters;
using GameValueEditor.Updates;
using GameValueEditor.ViewModels;

internal static class StabilityRegressionTests
{
    internal static async Task RunAsync(string root, string[] args)
    {
        CheckNumericEncoding();
        await CheckStorageAsync(Path.Combine(root, "storage"));
        await CheckCrossProcessStorageAsync(Path.Combine(root, "cross-process-storage"));
        await CheckComparisonAndLeaseAsync(Path.Combine(root, "numeric-scan"));
        await CheckManualSaveAsync(Path.Combine(root, "manual-save"));
        foreach (var scenario in new[] { "other-game", "return-game", "other-version", "reset", "disconnect", "shutdown" })
            CheckCompletedScanSwitch(Path.Combine(root, "stale-scan-" + scenario), scenario);
        await CheckCompatibilityAndRecoveryAsync(Path.Combine(root, "compatibility"));
        CheckModuleBuildDiagnostics(Path.Combine(root, "module-diagnostics"), args);
        Console.WriteLine("Stability regressions passed: stale operations, exact numeric encoding, scan identity/lease, durable storage, recovery gates and apply-time compatibility.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CheckNumericEncoding()
    {
        foreach (var text in new[] { "9007199254740993", long.MaxValue.ToString(), long.MinValue.ToString() })
        {
            Check(MemoryValueCodec.TryParseEncoded(text, MemoryValueType.Int64, 1, out var bytes) &&
                  BitConverter.ToInt64(bytes).ToString() == text, "Int64 encoding rounded or overflowed: " + text);
            Check(MemoryValueCodec.FormatDecoded(bytes, MemoryValueType.Int64, 1) == text, "Int64 display lost precision");
        }
        foreach (var text in new[] { "9223372036854775808", "-9223372036854775809", "1.1", "1.00000000000000000000000000001", "1e999", "NaN" })
            Check(!MemoryValueCodec.TryParseEncoded(text, MemoryValueType.Int64, 1, out _), "Invalid integer accepted: " + text);
        Check(MemoryValueCodec.TryParseEncoded("900719925474099.3", MemoryValueType.Int64, 10, out var scaled) &&
              BitConverter.ToInt64(scaled) == 9007199254740993L &&
              MemoryValueCodec.FormatDecoded(scaled, MemoryValueType.Int64, 10) == "900719925474099.3", "Scaled integer lost precision");
        Check(MemoryValueCodec.TryParseEncoded("1e2", MemoryValueType.Int32, .1, out var scientific) &&
              BitConverter.ToInt32(scientific) == 10, "Exact scientific/scaled encoding failed");
        foreach (var type in new[] { MemoryValueType.Float, MemoryValueType.Double })
            foreach (var text in new[] { "NaN", "Infinity", "-Infinity", "1e999" })
                Check(!MemoryValueCodec.TryParse(text, type, out _) && !MemoryValueCodec.TryParseEncoded(text, type, 1, out _), "Non-finite value accepted");
        Check(!MemoryValueCodec.TryParseEncoded("1e308", MemoryValueType.Double, 10, out _), "Encoded infinity accepted");
    }

    private static async Task CheckStorageAsync(string root)
    {
        var store = new ProfileStore(root);
        var document = new LibraryDocument { Games = [new GameProfile { Name = "before" }] };
        var first = store.SaveAsync(document);
        document.Games[0].Name = "after";
        await first;
        Check((await store.LoadAsync()).Games.Single().Name == "before", "Save used mutable UI state instead of invocation snapshot");
        var secondStore = new ProfileStore(root);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(index => (index % 2 == 0 ? store : secondStore).SaveAsync(document)));
        await File.WriteAllTextAsync(store.LibraryPath, "broken-json");
        var restored = await store.LoadAsync();
        Check(restored.Games.Single().Name == "after", "Valid backup did not load");
        await store.SaveAsync(restored);
        Check(File.ReadAllText(store.BackupPath) != "broken-json", "Corrupt primary replaced valid backup");
        File.Delete(store.LibraryPath);
        Check((await store.LoadAsync()).Games.Count == 1, "Missing primary ignored valid backup");
        await File.WriteAllTextAsync(store.LibraryPath, "{\"SchemaVersion\":99,\"Games\":[]}");
        try { await store.LoadAsync(); throw new Exception("Future schema silently fell back"); }
        catch (NotSupportedException) { }
        await File.WriteAllTextAsync(store.LibraryPath, "{\"Games\":[null]}");
        Check((await store.LoadAsync()).Games.Count == 1, "Invalid structure did not use backup");
        await File.WriteAllTextAsync(store.BackupPath, "broken-backup");
        try { await store.LoadAsync(); throw new Exception("Two broken copies silently became an empty library"); }
        catch (InvalidDataException) { }
        try { await store.SaveAsync(document); throw new Exception("Both damaged copies were overwritten"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllText(store.BackupPath) == "broken-backup", "Failed save destroyed evidence");
    }

    private static ScanCandidateStore CreateStore(string root, ulong address, byte[] previous, long count = 1)
    {
        var directory = Path.Combine(root, "generation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "part.bin");
        using (var writer = new BinaryWriter(File.Create(file)))
            for (long index = 0; index < count; index++) { writer.Write(address); writer.Write(previous); writer.Write(previous); }
        var partition = new ScanCandidatePartition { ValueType = previous.Length == 8 ? MemoryValueType.Int64 : MemoryValueType.Int32,
            SearchRoutineId = SearchRoutineIds.DirectNumeric, SearchRoutineName = "Fixture", ScaleMultiplier = 1,
            FirstBytes = previous, FilePath = file, Count = count };
        using var self = Process.GetCurrentProcess();
        return new ScanCandidateStore(root, directory, [partition],
            new FileStream(Path.Combine(directory, "store.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        { ProcessId = Environment.ProcessId, ProcessStartTimeUtc = self.StartTime.ToUniversalTime() };
    }

    private static async Task CheckCrossProcessStorageAsync(string root)
    {
        var store = new ProfileStore(root);
        var lease = new FileStream(store.LibraryPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--profile-save-target=" + root);
        using var child = Process.Start(start)!;
        try
        {
            Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == "READY", "Storage child did not start");
            await Task.Delay(150);
            Check(!child.HasExited && !File.Exists(store.LibraryPath), "Another process bypassed library file lock");
            lease.Dispose();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(child.ExitCode == 0 && (await store.LoadAsync()).Games.Single().Name == "child-process",
                "Storage child could not save after lock release: " + await child.StandardError.ReadToEndAsync());
        }
        finally { lease.Dispose(); if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
    }

    private static async Task CheckComparisonAndLeaseAsync(string root)
    {
        const long previous = 9007199254740992L;
        var payload = BitConverter.GetBytes(previous + 1);
        var pinned = GCHandle.Alloc(payload, GCHandleType.Pinned);
        try
        {
            var scanner = new MemoryScanService(root);
            using var source = CreateStore(root, unchecked((ulong)pinned.AddrOfPinnedObject().ToInt64()), BitConverter.GetBytes(previous));
            var run = await scanner.NextScanAsync(Environment.ProcessId, source, ScanComparison.Increased, _ => null, null, CancellationToken.None);
            using var result = run.Candidates;
            Check(result.Count == 1, "Int64 increased comparison lost one-unit difference above 2^53");
            Check(result.ReadCandidates(1)[0].ScanGenerationId == result.GenerationId, "Candidate lacks scan generation identity");
            using var accessor = new ProcessMemoryAccessor(Environment.ProcessId);
            try { accessor.EnsureInstance(Environment.ProcessId, source.ProcessStartTimeUtc.AddTicks(1)); throw new Exception("Reused process identity accepted"); }
            catch (InvalidOperationException) { }
            var path = source.Partitions[0].FilePath;
            var readLease = source.AcquireReadLease();
            source.Dispose();
            Check(File.Exists(path), "Source deleted while read lease active");
            readLease.Dispose();
            Check(!File.Exists(path), "Retired source was not cleaned after final reader");
        }
        finally { pinned.Free(); }
    }

    private static async Task CheckManualSaveAsync(string root)
    {
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "a.exe");
        using (var file = File.Create(executable)) file.SetLength(64 * 1024 * 1024);
        using var registry = new GameAdapterRegistry(Path.Combine(root, "modules"));
        var vm = ModuleLifecycleRegressionTests.CreateViewModel(root, new GameModuleCatalogService(Path.Combine(root, "modules")), registry);
        var b = new GameProfile { Name = "B", ProcessName = "game-b", ExecutablePath = "B.exe" };
        vm.Games.Add(b);
        var attached = typeof(MainViewModel).GetProperty(nameof(MainViewModel.AttachedProcess))!;
        attached.SetValue(vm, new ProcessItem { ProcessId = int.MaxValue, ProcessName = "game-a", ExecutablePath = executable });
        var adding = vm.AddCurrentProcessToLibraryAsync("A");
        Check(!adding.IsCompleted, "Fingerprint fixture did not suspend");
        vm.SelectedGame = b;
        attached.SetValue(vm, new ProcessItem { ProcessId = int.MaxValue - 1, ProcessName = "game-b", ExecutablePath = "B.exe" });
        typeof(MainViewModel).GetField("_attachedGameId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, b.Id);
        try { await adding; throw new Exception("Stale library save was applied"); }
        catch (OperationCanceledException) { }
        finally { vm.Shutdown(); }
        Check(b.Name == "B" && b.ProcessName == "game-b" && b.ExecutablePath == "B.exe" && b.Versions.Count == 0,
            "A fingerprint/save mutated B profile");
    }

    private static void CheckCompletedScanSwitch(string root, string scenario)
    {
        using var registry = new GameAdapterRegistry(Path.Combine(root, "modules"));
        var vm = ModuleLifecycleRegressionTests.CreateViewModel(root, new GameModuleCatalogService(Path.Combine(root, "modules")), registry);
        var a = new GameProfile { Name = "A", Versions = [new()] }; var b = new GameProfile { Name = "B", Versions = [new()] };
        vm.Games.Add(a); vm.Games.Add(b); vm.SelectedGame = a;
        using var self = Process.GetCurrentProcess();
        var process = new ProcessItem { ProcessId = Environment.ProcessId, ProcessName = "fixture", StartTimeUtc = self.StartTime.ToUniversalTime() };
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.AttachedProcess))!.SetValue(vm, process);
        var payload = BitConverter.GetBytes(123);
        var pinned = GCHandle.Alloc(payload, GCHandleType.Pinned);
        var original = SynchronizationContext.Current;
        var queue = new GatedContext();
        try
        {
            using var source = CreateStore(Path.Combine(root, "scan"), unchecked((ulong)pinned.AddrOfPinnedObject().ToInt64()), payload, 100000);
            typeof(MainViewModel).GetField("_scanCandidates", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, source);
            vm.SelectedComparison = ScanComparison.Unchanged;
            SynchronizationContext.SetSynchronizationContext(queue);
            var scan = vm.RunScanAsync(false);
            Check(queue.Ready.Wait(TimeSpan.FromSeconds(10)), "Completed scan did not reach isolated UI queue");
            if (scenario == "other-version") vm.SelectedVersion = new GameVersionProfile();
            else if (scenario == "reset") vm.ResetScan();
            else if (scenario == "disconnect") typeof(MainViewModel).GetProperty(nameof(MainViewModel.AttachedProcess))!.SetValue(vm, null);
            else if (scenario == "shutdown") vm.Shutdown();
            else
            {
                vm.SelectedGame = b;
                if (scenario == "return-game") vm.SelectedGame = a;
            }
            var expectedStatus = vm.StatusText;
            queue.Drain();
            Check(scan.IsCompleted, "Scan continuation remained incomplete");
            scan.GetAwaiter().GetResult();
            Check(vm.ScanResultCount == 0 && !vm.IsBusy && vm.StatusText == expectedStatus,
                "Completed old scan contaminated current page or left busy state");
            try { vm.WriteCandidatesAsync([new ScanCandidate { Address = 1, ValueType = MemoryValueType.Int32 }], "1").GetAwaiter().GetResult(); throw new Exception("Foreign candidate accepted"); }
            catch (InvalidOperationException) { }
        }
        finally { SynchronizationContext.SetSynchronizationContext(original); vm.Shutdown(); pinned.Free(); }
    }

    private static async Task CheckCompatibilityAndRecoveryAsync(string root)
    {
        Directory.CreateDirectory(root);
        var compatibility = new ApplicationUpdateCompatibility(2, 7, 5);
        ApplicationUpdateCompatibilityValidator.Validate(root, "0.4.7", compatibility);
        var compatibilityArchive = Path.Combine(root, "compatibility.zip");
        using (var zip = ZipFile.Open(compatibilityArchive, ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry(ApplicationUpdateCompatibilityValidator.ArchiveManifestName).Open();
            JsonSerializer.Serialize(stream, new ApplicationReleaseManifest(1, "0.4.7", compatibility));
        }
        Check(ApplicationUpdateCompatibilityValidator.ResolveVerifiedArchive(compatibilityArchive, "0.4.7", null) == compatibility,
            "Verified archive did not bridge the old host pending format");
        try { ApplicationUpdateCompatibilityValidator.ResolveVerifiedArchive(compatibilityArchive, "0.4.6", null); throw new Exception("Mismatched target metadata accepted"); }
        catch (InvalidDataException) { }
        var oldArchive = Path.Combine(root, "old-archive.zip");
        using (ZipFile.Open(oldArchive, ZipArchiveMode.Create)) { }
        try { ApplicationUpdateCompatibilityValidator.ResolveVerifiedArchive(oldArchive, "0.4.6", null); throw new Exception("Missing compatibility was guessed"); }
        catch (InvalidOperationException) { }
        var modules = Path.Combine(root, "data", "modules");
        Directory.CreateDirectory(modules);
        await File.WriteAllTextAsync(Path.Combine(modules, "installed.json"), "{\"SchemaVersion\":1,\"Modules\":[{\"Id\":\"game.fixture\",\"Version\":\"1.0.0\"}]}");
        var package = Path.Combine(modules, "packages", "game.fixture", "1.0.0"); Directory.CreateDirectory(package);
        var manifest = Path.Combine(package, "module.json");
        await File.WriteAllTextAsync(manifest, "{\"id\":\"game.fixture\",\"version\":\"1.0.0\",\"displayName\":\"Fixture\",\"hostApiVersion\":7,\"minimumHostVersion\":\"0.4.4\"}");
        ApplicationUpdateCompatibilityValidator.Validate(root, "0.4.7", compatibility);
        using (var held = await ModuleMutationLock.AcquireAsync(modules))
        {
            using var cancel = new CancellationTokenSource(100);
            try { using var other = await ModuleMutationLock.AcquireAsync(modules, cancel.Token); throw new Exception("Concurrent module mutation accepted"); }
            catch (OperationCanceledException) { }
        }
        await File.WriteAllTextAsync(manifest, "{\"id\":\"game.fixture\",\"version\":\"1.0.0\",\"hostApiVersion\":8,\"minimumHostVersion\":\"0.4.8\"}");
        try { ApplicationUpdateCompatibilityValidator.Validate(root, "0.4.7", compatibility); throw new Exception("Changed installed module ignored"); }
        catch (InvalidOperationException) { }
        File.Delete(manifest);
        try { ApplicationUpdateCompatibilityValidator.Validate(root, "0.4.7", compatibility); throw new Exception("Missing module manifest ignored"); }
        catch (FileNotFoundException) { }
        var transaction = Path.Combine(root, ".update-transaction-fixture"); Directory.CreateDirectory(transaction);
        var journal = Path.Combine(transaction, "recovery.json");
        await UpdateRecovery.SaveAsync(journal, new RecoveryJournal(root, []));
        var updates = Path.Combine(root, "data", "updates");
        var service = new ApplicationUpdateService(updates, applicationDirectory: root);
        try { service.EnsureNoRecoveryRequired(); throw new Exception("Incomplete journal without marker ignored"); }
        catch (ApplicationRecoveryRequiredException) { }
        await UpdateRecovery.RecordRequiredAsync(root, journal);
        Check(File.Exists(service.RecoveryRequiredPath), "Applying journal lacks durable marker");
        await UpdateRecovery.SaveAsync(journal, new RecoveryJournal(root, [], "Committed"));
        try { await UpdateRecovery.RestoreAsync(journal, root); throw new Exception("Committed transaction was restored"); }
        catch (InvalidOperationException) { }
        _ = new ApplicationUpdateService(updates, applicationDirectory: root);
        Check(!Directory.Exists(transaction) && !File.Exists(service.RecoveryRequiredPath), "Committed residue incorrectly blocked startup");
    }

    private static void CheckModuleBuildDiagnostics(string root, string[] args)
    {
        foreach (var path in args.Where(arg => arg.StartsWith("--verify-module-package=", StringComparison.Ordinal)).Select(arg => arg["--verify-module-package=".Length..]))
        {
            using var zip = ZipFile.OpenRead(path);
            using var source = zip.Entries.Single(entry => entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Open();
            using var bytes = new MemoryStream(); source.CopyTo(bytes);
            var assembly = Assembly.Load(bytes.ToArray());
            var type = assembly.GetExportedTypes().SingleOrDefault(candidate => !candidate.IsAbstract && typeof(IGameAdapter).IsAssignableFrom(candidate));
            if (type is null) continue;
            var adapter = (IGameAdapter)Activator.CreateInstance(type)!;
            var context = new GameProcessContext(int.MaxValue, "fixture", "missing.exe", DateTime.UtcNow);
            var unknown = new GameBuildIdentity("unknown", "", "unknown", "unknown");
            Check(!adapter.Supports(context, unknown), "Module accepts an unrelated process: " + adapter.Id);
            if (assembly.GetType("GameValueEditor.Modules.Runtime.Il2CppRuntimeResolver") is not null)
            {
                using var manifestSource = zip.GetEntry("module.json")!.Open();
                using var manifest = JsonDocument.Parse(manifestSource);
                Check(manifest.RootElement.GetProperty("supportsUnlistedBuildValidation").GetBoolean(),
                    "Current-metadata module is not declared for small updates");
                var name = manifest.RootElement.GetProperty("processNames")[0].GetString()!;
                var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, "GameAssembly.dll"), []);
                var named = context with { ProcessName = name, ExecutablePath = Path.Combine(directory, name + ".exe") };
                // Identity is a cheap name check. This fixture is not a live process,
                // and must never be treated as evidence that data is safe to read/write.
                foreach (var identity in new[] { unknown, new GameBuildIdentity("changed-exe", "", "changed-assembly", "changed-metadata") })
                    Check(adapter.Supports(named, identity), "Named small update rejected by historical hashes: " + adapter.Id);
                foreach (var identity in new[] { unknown, new GameBuildIdentity("changed-exe", "", "changed-assembly", "changed-metadata") })
                {
                    var diagnostics = ((IGameCompatibilityDiagnosticsProvider)adapter).GetCompatibilityDiagnostics(named, identity);
                    Check(diagnostics.Any(item => item.Status == GameCompatibilityDiagnosticStatus.Information) &&
                          !diagnostics.Any(item => item.Status == GameCompatibilityDiagnosticStatus.Failed),
                        "Static diagnostics reject a small update or assert all runtime data was validated");
                }
                try { ((IInventoryGameAdapter)adapter).ReadInventory(named); throw new Exception("Direct entry accepted a nonexistent process"); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            if (adapter is IGameEditorPageFactoryProvider)
            {
                var items = new List<ModuleCompatibilityReportItem>();
                typeof(ModuleCompatibilityDiagnosticsService).GetMethod("AddEditorItems", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, [items, adapter, new ProcessItem { ProcessId = int.MaxValue }]);
                Check(items.Count == adapter.Editors.Count && items.All(item => item.Status == GameCompatibilityDiagnosticStatus.Information),
                    "Host reports self-owned page registration as runtime compatibility");
            }
        }
    }

    private sealed class GatedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        internal ManualResetEventSlim Ready { get; } = new(false);
        public override void Post(SendOrPostCallback callback, object? state) { _queue.Enqueue((callback, state)); if (state is Action) Ready.Set(); }
        internal void Drain() { while (_queue.TryDequeue(out var item)) item.Callback(item.State); }
    }
}
