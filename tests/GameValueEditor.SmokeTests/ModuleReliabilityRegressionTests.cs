using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GameValueEditor.Models;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Services;
using GameValueEditor.Services.Adapters;
using GameValueEditor.Updates;

internal static class ModuleReliabilityRegressionTests
{
    private static int _assertions;
    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task RunAsync(string root)
    {
        _assertions = 0;
        CheckState(Path.Combine(root, "records"));
        CheckTransactionFailures(Path.Combine(root, "failures"));
        await CheckInterruptedTransactionsAsync(Path.Combine(root, "interruptions"));
        CheckDiagnostics(Path.Combine(root, "diagnostics"));
        await CheckInstancesAsync(Path.Combine(root, "instances"));
        Console.WriteLine($"Module/instance reliability regressions passed: {_assertions} assertions, including killed child processes.");
    }

    private static void Rejected(Action action, string message)
    {
        try { action(); }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or IOException or InvalidOperationException) { _assertions++; return; }
        throw new InvalidOperationException(message);
    }

    private static InstalledModuleDocument Records(string version = "1.0.0") => new()
    {
        Modules = [new("game.keep", "1.0.0", DateTime.UnixEpoch), new("game.test", version, DateTime.UnixEpoch)]
    };

    private static void CheckState(string root)
    {
        foreach (var bad in new[] { "broken", "null", "{}", "{\"SchemaVersion\":1,\"Modules\":null}",
            "{\"SchemaVersion\":99,\"Modules\":[]}", "{\"SchemaVersion\":1,\"Modules\":[null]}",
            "{\"SchemaVersion\":1,\"Modules\":[{\"Id\":\"../outside\",\"Version\":\"1.0.0\"}]}",
            "{\"SchemaVersion\":1,\"Modules\":[{\"Id\":\"game.test\",\"Version\":\"1.0.10\"}]}",
            "{\"SchemaVersion\":1,\"Modules\":[{\"Id\":\"game.test\",\"Version\":\"1.0.0\"},{\"Id\":\"GAME.TEST\",\"Version\":\"1.0.1\"}]}" })
        {
            var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            var store = new ModuleStateStore(directory);
            store.SaveInstalled(Records());
            var backup = File.ReadAllBytes(store.InstalledPath + ".backup");
            File.WriteAllText(store.InstalledPath, bad);
            Rejected(() => store.ReadInstalled(), "Invalid registration was read as empty.");
            Rejected(() => store.SaveInstalled(Records("1.0.1")), "Invalid registration was overwritten.");
            var catalog = new GameModuleCatalogService(directory);
            Check(catalog.StorageErrors.Count > 0 && File.ReadAllText(store.InstalledPath) == bad &&
                  File.ReadAllBytes(store.InstalledPath + ".backup").SequenceEqual(backup), "Registration evidence was destroyed.");
            Rejected(() => catalog.Unregister("game.test"), "Uninstall bypassed invalid registration.");
        }
        var missing = new ModuleStateStore(Path.Combine(root, "missing-primary"));
        missing.SaveInstalled(Records());
        File.Delete(missing.InstalledPath);
        Rejected(() => missing.ReadInstalled(), "Missing primary silently loaded empty or chose backup versions.");
        Rejected(() => missing.SaveInstalled(new()), "Missing primary overwrote recovery evidence.");
        var pending = new ModuleStateStore(Path.Combine(root, "deletions"));
        pending.SaveDeletions(new() { ModuleIds = ["game.test"] });
        pending.SaveDeletions(new());
        Check(File.Exists(pending.DeletionsPath) && pending.ReadDeletions().ModuleIds.Count == 0,
            "Completed deletion removed its empty tombstone.");
        File.WriteAllText(pending.DeletionsPath, "bad-deletion-record");
        var damaged = new GameModuleCatalogService(pending.Root);
        Check(damaged.StorageErrors.Count > 0 && File.ReadAllText(pending.DeletionsPath) == "bad-deletion-record",
            "Startup silently deleted a bad pending record.");
        Rejected(() => damaged.DeletePackageAsync("game.test").GetAwaiter().GetResult(), "Cleanup bypassed damaged deletion record.");
        foreach (var bad in new[] { "{\"SchemaVersion\":99,\"ModuleIds\":[]}", "{\"SchemaVersion\":1,\"ModuleIds\":null}",
            "{\"SchemaVersion\":1,\"ModuleIds\":[\"game.test\",\"game.test\"]}" })
        {
            File.WriteAllText(pending.DeletionsPath, bad);
            Rejected(() => pending.ReadDeletions(), "Invalid deletion records were interpreted as empty.");
            Rejected(() => pending.SaveDeletions(new()), "Invalid deletion records were overwritten.");
            Check(File.ReadAllText(pending.DeletionsPath) == bad, "Deletion evidence changed.");
        }
        var occupied = new ModuleStateStore(Path.Combine(root, "occupied-record"));
        occupied.SaveInstalled(Records());
        using (var held = new FileStream(occupied.InstalledPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Rejected(() => occupied.ReadInstalled(), "Read failure was mistaken for a new empty directory.");
            Rejected(() => occupied.SaveInstalled(new()), "Occupied records were overwritten.");
        }
        Check(occupied.ReadInstalled().Modules.SequenceEqual(Records().Modules), "Occupied record changed after lock release.");
    }

    private static (ModuleStateStore Store, string Target) Fixture(string root, bool retained = true)
    {
        var store = new ModuleStateStore(root);
        store.SaveInstalled(Records());
        var original = store.Resolve(Path.Combine("packages", "game.test", "1.0.0"));
        Directory.CreateDirectory(original);
        File.WriteAllText(Path.Combine(original, "untouched.txt"), "active-original");
        var target = store.Resolve(Path.Combine("packages", "game.test", "1.0.1"));
        if (retained) { Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "module.json"), "retained-original"); }
        return (store, target);
    }

    private static string Prepare(ModuleInstallTransaction transaction)
    {
        var directory = transaction.CreateDirectory();
        Directory.CreateDirectory(Path.Combine(directory, "staged"));
        File.WriteAllText(Path.Combine(directory, "staged", "module.json"), "new-target");
        return directory;
    }

    private static void CheckOriginal(ModuleStateStore store, string target, bool retained)
    {
        Check(store.ReadInstalled().Modules.SequenceEqual(Records().Modules), "Failed installation changed registrations.");
        Check(retained ? File.ReadAllText(Path.Combine(target, "module.json")) == "retained-original" : !Directory.Exists(target),
            "Failed installation lost the preexisting target or retained a partial new package.");
        Check(File.ReadAllText(store.Resolve("packages/game.test/1.0.0/untouched.txt")) == "active-original", "Another version was changed.");
    }

    private static void CheckTransactionFailures(string root)
    {
        foreach (var phase in new[] { "prepared", "backed-up", "package-placed", "registered" })
        {
            var store = new ModuleStateStore(Path.Combine(root, "initial-" + phase));
            var transaction = new ModuleInstallTransaction(store);
            var directory = Prepare(transaction);
            var after = new InstalledModuleDocument { Modules = [new("game.test", "1.0.1", DateTime.UnixEpoch)] };
            Rejected(() => transaction.Commit(directory, "game.test", "1.0.1", new(), after,
                current => { if (current == phase) throw new IOException("Initial installation interrupted."); }), "Initial fault was ignored.");
            Check(store.ReadInstalled().Modules.Count == 0 && !Directory.Exists(store.Resolve("packages/game.test/1.0.1")) &&
                  !Directory.Exists(directory), "Failed initial installation left an active or ambiguous module.");
        }
        foreach (var retained in new[] { true, false })
        foreach (var phase in new[] { "prepared", "backed-up", "package-placed", "registered" })
        {
            var fixture = Fixture(Path.Combine(root, phase + retained), retained);
            var transaction = new ModuleInstallTransaction(fixture.Store);
            using var gate = ModuleMutationLock.Acquire(fixture.Store.Root);
            var directory = Prepare(transaction);
            Rejected(() => transaction.Commit(directory, "game.test", "1.0.1", Records(), Records("1.0.1"),
                current => { if (current == phase) throw new IOException("Injected installation failure."); }), "Fault injection was ignored.");
            CheckOriginal(fixture.Store, fixture.Target, retained);
            transaction.RecoverAll(); transaction.RecoverAll();
            Check(!Directory.Exists(directory), "Recovered transaction did not finish idempotently.");
        }
        var locked = Fixture(Path.Combine(root, "blocked-recovery"));
        var blockedTransaction = new ModuleInstallTransaction(locked.Store);
        var blockedDirectory = Prepare(blockedTransaction);
        FileStream? fileLock = null;
        try
        {
            Rejected(() => blockedTransaction.Commit(blockedDirectory, "game.test", "1.0.1", Records(), Records("1.0.1"), phase =>
            {
                if (phase == "registered")
                {
                    fileLock = new FileStream(locked.Store.InstalledPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    throw new IOException("Keep registration occupied during recovery.");
                }
            }), "Occupied recovery succeeded unexpectedly.");
            Check(Directory.Exists(Path.Combine(blockedDirectory, "backup-package")) && File.Exists(Path.Combine(blockedDirectory, "transaction.json")),
                "Failed recovery consumed its backup or journal.");
            var blockedCatalog = new GameModuleCatalogService(locked.Store.Root);
            Check(blockedCatalog.StorageErrors.Count > 0, "Unfinished recovery was not exposed.");
        }
        finally { fileLock?.Dispose(); }
        blockedTransaction.RecoverAll();
        CheckOriginal(locked.Store, locked.Target, true);
        var corrupted = Fixture(Path.Combine(root, "corrupt-journal"));
        var corruptTransaction = new ModuleInstallTransaction(corrupted.Store);
        var corruptDirectory = Prepare(corruptTransaction);
        File.WriteAllText(Path.Combine(corruptDirectory, "transaction.json"), "{}");
        Rejected(() => corruptTransaction.RecoverAll(), "Bad journal was accepted.");
        Check(Directory.Exists(corruptDirectory), "Bad journal evidence was removed.");
        var preparationStore = new ModuleStateStore(Path.Combine(root, "unfinished-preparation"));
        var preparation = new ModuleInstallTransaction(preparationStore);
        var unpreparedDirectory = Prepare(preparation);
        preparation.RecoverAll();
        Check(!Directory.Exists(unpreparedDirectory), "Known preparation-only interruption blocked startup.");
        var tampered = Fixture(Path.Combine(root, "externally-modified"));
        var tamperedTransaction = new ModuleInstallTransaction(tampered.Store);
        var tamperedDirectory = Prepare(tamperedTransaction);
        Rejected(() => tamperedTransaction.Commit(tamperedDirectory, "game.test", "1.0.1", Records(), Records("1.0.1"), phase =>
        {
            if (phase != "package-placed") return;
            File.WriteAllText(Path.Combine(tampered.Target, "foreign.txt"), "external-change");
            throw new IOException("Target modified outside transaction.");
        }), "External target modification was overwritten.");
        Check(File.Exists(Path.Combine(tampered.Target, "foreign.txt")) && Directory.Exists(Path.Combine(tamperedDirectory, "backup-package")),
            "External target evidence or original backup was deleted.");
        foreach (var change in new[] { "root", "path", "unrelated-record" })
        {
            var journalPath = Path.Combine(tamperedDirectory, "transaction.json");
            var original = File.ReadAllText(journalPath);
            var journal = JsonSerializer.Deserialize<ModuleInstallTransaction.Journal>(original)!;
            if (change == "root") journal.ModulesRoot = root;
            else if (change == "path") journal.TargetFiles = new() { ["../outside"] = new string('A', 64) };
            else journal.Before.Modules.RemoveAll(item => item.Id == "game.keep");
            File.WriteAllText(journalPath, JsonSerializer.Serialize(journal));
            Rejected(() => tamperedTransaction.RecoverAll(), "Tampered journal permitted recovery: " + change);
            Check(File.Exists(Path.Combine(tampered.Target, "foreign.txt")), "Tampered recovery changed target files.");
            File.WriteAllText(journalPath, original);
        }
        ApplicationUpdateCompatibilityValidator.Validate(Path.Combine(root, "empty-app"), "0.4.9", new(2, 7, 5));
        var blockedApp = Path.Combine(root, "app");
        Directory.CreateDirectory(Path.Combine(blockedApp, "data", "modules", ModuleInstallTransaction.Prefix + Guid.NewGuid().ToString("N")));
        Rejected(() => ApplicationUpdateCompatibilityValidator.Validate(blockedApp, "0.4.9", new(2, 7, 5)), "Updater ignored module transaction.");
        var committed = Fixture(Path.Combine(root, "committed-cleanup-locked"));
        var committedTransaction = new ModuleInstallTransaction(committed.Store);
        var committedDirectory = Prepare(committedTransaction);
        FileStream? backupLock = null;
        try
        {
            committedTransaction.Commit(committedDirectory, "game.test", "1.0.1", Records(), Records("1.0.1"), phase =>
            {
                if (phase == "committed") backupLock = new FileStream(Path.Combine(committedDirectory, "backup-package", "module.json"),
                    FileMode.Open, FileAccess.Read, FileShare.Read);
            });
            Check(JsonSerializer.Deserialize<ModuleInstallTransaction.Journal>(File.ReadAllText(Path.Combine(committedDirectory, "transaction.json")))!.State == "Committed",
                "Partial cleanup erased the committed journal.");
            committedTransaction.RecoverAll();
            Check(committed.Store.ReadInstalled().Modules.SequenceEqual(Records("1.0.1").Modules), "Locked cleanup selected an old version.");
        }
        finally { backupLock?.Dispose(); }
        committedTransaction.RecoverAll();
        Check(!Directory.Exists(committedDirectory) && File.ReadAllText(Path.Combine(committed.Target, "module.json")) == "new-target",
            "Committed cleanup did not finish without rollback.");
    }

    internal static void InterruptTarget(string root, string phase)
    {
        var store = new ModuleStateStore(root);
        using var gate = ModuleMutationLock.Acquire(root);
        var transaction = new ModuleInstallTransaction(store);
        transaction.Commit(Prepare(transaction), "game.test", "1.0.1", Records(), Records("1.0.1"), current =>
        {
            if (current != phase) return;
            Console.WriteLine("CHECKPOINT " + current);
            Console.Out.Flush();
            Console.ReadLine();
        });
    }

    private static Process StartChild(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static async Task StopChildAsync(Process child)
    {
        if (!child.HasExited) child.Kill(true);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task CheckInterruptedTransactionsAsync(string root)
    {
        foreach (var phase in new[] { "prepared", "backed-up", "package-placed", "registered", "committed" })
        {
            var fixture = Fixture(Path.Combine(root, phase));
            using var child = StartChild("--module-interruption-target=" + fixture.Store.Root, "--checkpoint=" + phase);
            try
            {
                Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "CHECKPOINT " + phase,
                    "Installation child did not reach checkpoint: " + phase);
            }
            finally { await StopChildAsync(child); }
            using var gate = ModuleMutationLock.Acquire(fixture.Store.Root);
            new ModuleInstallTransaction(fixture.Store).RecoverAll();
            if (phase == "committed")
            {
                Check(fixture.Store.ReadInstalled().Modules.SequenceEqual(Records("1.0.1").Modules) &&
                      File.ReadAllText(Path.Combine(fixture.Target, "module.json")) == "new-target", "Committed installation was rolled back after process termination.");
            }
            else CheckOriginal(fixture.Store, fixture.Target, true);
        }
    }

    private static void CheckDiagnostics(string root)
    {
        var store = new ModuleStateStore(root);
        store.SaveInstalled(Records());
        foreach (var scenario in new[] { "manifest-missing", "dll-missing", "identity-mismatch", "assembly-escape" })
        {
            var directory = store.Resolve("packages/game.test/1.0.0");
            Directory.CreateDirectory(directory);
            var manifest = new InstalledModuleManifest { Id = scenario == "identity-mismatch" ? "game.other" : "game.test",
                Version = "1.0.0", AssemblyFile = scenario == "assembly-escape" ? "../outside.dll" : "missing.dll" };
            var manifestPath = Path.Combine(directory, "module.json");
            if (scenario == "manifest-missing") { if (File.Exists(manifestPath)) File.Delete(manifestPath); }
            else File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
            using var registry = new GameAdapterRegistry(root);
            var report = ModuleCompatibilityDiagnosticsService.Create(new("0.4.9", new GameVersionProfile(), null, null,
                "game.test", store.ReadInstalled().Modules[1], null, null, null, registry.LoadErrors));
            Check(registry.LoadErrors.Any(error => error.StartsWith("game.test v", StringComparison.Ordinal)) &&
                  report.Items.Any(item => item.DisplayName == "模块加载" && item.Status == GameCompatibilityDiagnosticStatus.Failed),
                "A missing/bad module silently passed diagnostics: " + scenario);
            Check(!report.Text.Contains("game.keep v", StringComparison.Ordinal), "Another module's failure leaked into selected diagnostics.");
        }
        var uninstalled = ModuleCompatibilityDiagnosticsService.Create(new("0.4.9", null, null, null, "game.test", null, null, null, null, []));
        Check(uninstalled.Items.Single(item => item.DisplayName == "模块加载").Status == GameCompatibilityDiagnosticStatus.Information,
            "An uninstalled module was reported as a successful load.");
        File.WriteAllText(store.InstalledPath, "damaged");
        var catalog = new GameModuleCatalogService(root);
        using var damagedRegistry = new GameAdapterRegistry(root);
        var vm = ModuleLifecycleRegressionTests.CreateViewModel(Path.GetDirectoryName(root)!, catalog, damagedRegistry);
        try
        {
            var game = new GameProfile { ModuleId = "game.test", Versions = [new()] };
            vm.Games.Add(game); vm.SelectedGame = game; vm.SelectedVersion = game.Versions[0];
            var report = vm.CreateModuleCompatibilityReport();
            Check(report.Items.Any(item => item.DisplayName == "本地安装" && item.Status == GameCompatibilityDiagnosticStatus.Failed) &&
                  report.Items.Any(item => item.DisplayName == "模块加载" && item.Status == GameCompatibilityDiagnosticStatus.Failed),
                "Global storage error was filtered out of the report.");
            Check(!vm.CanInstallGameModule && !vm.CanRollbackGameModule && !vm.CanUninstallGameModule && vm.CanViewModuleCompatibilityDiagnostics &&
                  vm.ModuleStatusText.Contains("资料", StringComparison.Ordinal) && game.ModuleId == "game.test", "Broken storage was hidden or lost semantic module identity.");
            Check(!vm.CanRemoveCurrentGame, "Library removal bypassed unknown module storage state.");
            Rejected(() => vm.DeleteSelectedGameAsync().GetAwaiter().GetResult(), "Linked game was removed despite invalid module registration.");
            Check(vm.Games.Contains(game) && game.ModuleId == "game.test", "Rejected removal changed library identity.");
        }
        finally { vm.Shutdown(); }
        var sensitive = ModuleCompatibilityDiagnosticsService.Create(new("0.4.9", null, null, null, "game.test", null, null, null, null,
            ["installed.json: C:\\Users\\private\\record.json PID 424242"]));
        Check(!sensitive.Text.Contains("424242", StringComparison.Ordinal) && sensitive.Text.Contains("已隐藏", StringComparison.Ordinal), "Global errors bypassed sanitization.");
    }

    private static async Task CheckInstancesAsync(string root)
    {
        Check(ApplicationInstanceLease.TryAcquire(root, out var first, out _), "First instance lease failed.");
        using (first)
        {
            Check(!ApplicationInstanceLease.TryAcquire(root, out var duplicate, out var owner) && duplicate is null && owner?.ProcessId == Environment.ProcessId,
                "Same data directory allowed two owners.");
            Check(ApplicationInstanceLease.TryAcquire(root + "-other", out var independent, out _), "Independent directory was blocked.");
            independent!.Dispose();
            using var blocked = StartChild("--instance-lease-target=" + root);
            try
            {
                Check(await blocked.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "BUSY", "Cross-process lease was bypassed.");
                await blocked.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Check(blocked.ExitCode == 11, "Duplicate lease child returned success.");
            }
            finally { await StopChildAsync(blocked); }
        }
        using var holder = StartChild("--instance-lease-target=" + root);
        try
        {
            Check(await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "ACQUIRED", "Child could not acquire released lease.");
            Check(!ApplicationInstanceLease.TryAcquire(root, out _, out _), "Child-held data directory was not protected.");
        }
        finally { await StopChildAsync(holder); }
        Check(ApplicationInstanceLease.TryAcquire(root, out var recovered, out _), "Killed owner left a permanent lock.");
        recovered!.Dispose();
    }
}
