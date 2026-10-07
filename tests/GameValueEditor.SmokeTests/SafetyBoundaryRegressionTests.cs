using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameValueEditor.Models;
using GameValueEditor.Services;
using GameValueEditor.Services.Adapters;
using GameValueEditor.ViewModels;

internal static class SafetyBoundaryRegressionTests
{
    private static int _assertions;

    internal static async Task RunAsync(string root)
    {
        _assertions = 0;
        await CheckProfileStructureAsync(Path.Combine(root, "profiles"));
        CheckWriteVerifier();
        await CheckWriteCommandsAsync(Path.Combine(root, "writes"));
        await CheckLogsAsync(Path.Combine(root, "logs"));
        Console.WriteLine($"Safety boundary regressions passed: {_assertions} assertions (profile structure, truthful write readback, bounded best-effort crash logs).");
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

    private static async Task CheckProfileStructureAsync(string root)
    {
        var store = new ProfileStore(root);
        var original = new LibraryDocument { Games = [new GameProfile { Name = "保留游戏", Versions =
            [new GameVersionProfile { Fields = [new SavedField { Name = "保留字段", Note = "不能丢失" }] }] }] };
        await store.SaveAsync(original);
        var backup = await File.ReadAllBytesAsync(store.BackupPath);
        foreach (var invalid in new[]
        {
            "{}", "[]", "null", "{\"Theme\":\"Dark\"}", "{\"SchemaVersion\":7}",
            "{\"Games\":null}", "{\"Games\":{}}", "{\"Games\":[null]}", "{\"Games\":[1]}",
            "{\"Games\":[{}]}", "{\"Games\":[{\"Versions\":null}]}", "{\"Games\":[{\"Versions\":{}}]}",
            "{\"Games\":[{\"Versions\":[null]}]}", "{\"Games\":[{\"Versions\":[{}]}]}",
            "{\"Games\":[{\"Versions\":[{\"Fields\":null}]}]}", "{\"Games\":[{\"Versions\":[{\"Fields\":{}}]}]}",
            "{\"Games\":[{\"Versions\":[{\"Fields\":[null]}]}]}", "{\"Games\":[{\"Versions\":[{\"Fields\":[1]}]}]}",
            "{\"Games\":[],\"games\":[]}", "{\"SchemaVersion\":7,\"schemaversion\":7,\"Games\":[]}",
            "{\"Games\":[{\"Versions\":[],\"versions\":[]}]}", "{\"Games\":[{\"Versions\":[{\"Fields\":[],\"fields\":[]}]}]}",
            "{\"Games\":[{\"Versions\":[{\"Fields\":[{\"Name\":\"A\",\"name\":\"B\"}]}]}]}",
            "{\"SchemaVersion\":0,\"Games\":[]}", "{\"SchemaVersion\":-1,\"Games\":[]}",
            "{\"SchemaVersion\":null,\"Games\":[]}", "{\"SchemaVersion\":\"7\",\"Games\":[]}",
            "{\"SchemaVersion\":true,\"Games\":[]}", "{\"SchemaVersion\":1.5,\"Games\":[]}"
        })
        {
            await File.WriteAllTextAsync(store.LibraryPath, invalid);
            var recovered = await store.LoadAsync();
            Check(recovered.Games.Single().Versions.Single().Fields.Single().Note == "不能丢失", "Incomplete JSON silently became an empty/default library: " + invalid);
            await store.SaveAsync(recovered);
            Check((await File.ReadAllBytesAsync(store.BackupPath)).SequenceEqual(backup), "Invalid primary replaced the good backup.");
        }

        foreach (var future in new[] { 8L, 99L, (long)int.MaxValue + 1 })
        {
            var text = $"{{\"SchemaVersion\":{future},\"Games\":[]}}";
            await File.WriteAllTextAsync(store.LibraryPath, text);
            await ExpectAsync<NotSupportedException>(async () => { await store.LoadAsync(); });
            await ExpectAsync<NotSupportedException>(() => store.SaveAsync(original));
            Check(await File.ReadAllTextAsync(store.LibraryPath) == text && (await File.ReadAllBytesAsync(store.BackupPath)).SequenceEqual(backup), "Future format was overwritten or hidden by a backup.");
        }

        await File.WriteAllTextAsync(store.LibraryPath, "{}");
        await File.WriteAllTextAsync(store.BackupPath, "{\"Theme\":\"Dark\"}");
        await ExpectAsync<InvalidDataException>(async () => { await store.LoadAsync(); });
        await ExpectAsync<InvalidDataException>(() => store.SaveAsync(original));
        Check(await File.ReadAllTextAsync(store.LibraryPath) == "{}" && await File.ReadAllTextAsync(store.BackupPath) == "{\"Theme\":\"Dark\"}", "Two structurally bad copies were overwritten.");
        await File.WriteAllTextAsync(store.BackupPath, "{\"SchemaVersion\":99,\"Games\":[]}");
        await ExpectAsync<NotSupportedException>(async () => { await store.LoadAsync(); });

        foreach (var declaration in Enumerable.Range(1, 7).Select(value => $"\"SchemaVersion\":{value},").Prepend(""))
        {
            await File.WriteAllTextAsync(store.LibraryPath, "{" + declaration + "\"games\":[{\"name\":\"旧库\",\"versions\":[{\"fields\":[{\"Name\":\"旧字段\"}]}]}],\"UnknownFutureProperty\":{}}");
            var loaded = await store.LoadAsync();
            Check(loaded.SchemaVersion == 7 && loaded.Games.Single().Versions.Single().Fields.Single().Group == "未分组", "Valid legacy format/case/default fields failed to migrate.");
        }
        await File.WriteAllTextAsync(store.LibraryPath, "{\"SchemaVersion\":7,\"Games\":[]}");
        Check((await store.LoadAsync()).Games.Count == 0, "An explicitly empty library is not corruption.");
        var fresh = new ProfileStore(Path.Combine(root, "new"));
        Check((await fresh.LoadAsync()).Games.Count == 0, "A new directory did not create an empty library.");
    }

    private static void CheckWriteVerifier()
    {
        foreach (var mode in Enum.GetValues<MemoryWriteState>())
        {
            using var memory = new FakeMemory(mode);
            var result = MemoryWriteVerifier.Write(memory, 1, BitConverter.GetBytes(42));
            Check(result.State == mode, "Write result conflated failure/readback/mismatch/confirmation.");
            Check(memory.Writes == 1 && memory.Reads == (mode == MemoryWriteState.WriteFailed ? 0 : 1), "Verifier retried or read after failed write.");
            Check(result.HasReadback == (mode is MemoryWriteState.ReadbackConfirmed or MemoryWriteState.ReadbackMismatch), "Unknown values presented as a successful readback.");
            if (result.HasReadback) Check(BitConverter.ToInt32(result.CurrentBytes) == (mode == MemoryWriteState.ReadbackMismatch ? 43 : 42), "Verifier returned requested bytes instead of the actual bytes.");
            else Check(result.CurrentBytes.Length == 0, "Verifier fabricated current data.");
        }
        using var shortRead = new FakeMemory(MemoryWriteState.ReadbackConfirmed) { ShortRead = true };
        Check(MemoryWriteVerifier.Write(shortRead, 1, BitConverter.GetBytes(42)).State == MemoryWriteState.ReadbackFailed, "Short read counted as confirmation.");
        foreach (var type in Enum.GetValues<MemoryValueType>())
        {
            Check(MemoryValueCodec.TryParseEncoded("21", type, 2, out var encoded), "Scaled write could not encode a numeric type.");
            using var memory = new FakeMemory(MemoryWriteState.ReadbackConfirmed);
            var result = MemoryWriteVerifier.Write(memory, 1, encoded);
            Check(result.State == MemoryWriteState.ReadbackConfirmed && MemoryValueCodec.FormatDecoded(result.CurrentBytes, type, 2) == "21", "Readback did not preserve numeric type/scale semantics.");
        }
    }

    private static async Task CheckWriteCommandsAsync(string root)
    {
        using (var fixture = new WriteFixture(Path.Combine(root, "batch")))
        {
            using var memory = new FakeMemory(MemoryWriteState.ReadbackConfirmed);
            memory.Modes[2] = MemoryWriteState.ReadbackFailed; memory.Modes[3] = MemoryWriteState.ReadbackMismatch; memory.Modes[4] = MemoryWriteState.WriteFailed;
            fixture.ViewModel.MemoryWriteAccessFactory = _ => memory;
            var candidates = fixture.SetCandidates([1, 2, 3, 4]);
            await ExpectAsync<InvalidOperationException>(() => fixture.ViewModel.WriteCandidatesAsync(candidates, "42"));
            var status = fixture.ViewModel.StatusText;
            foreach (var phrase in new[] { "回读一致 1", "回读失败 1", "回读不一致 1", "写入失败 1" }) Check(status.Contains(phrase), "Batch outcome lost an independent count.");
            Check(candidates[0].CurrentDisplay == "42" && candidates[1].CurrentDisplay == "?" && candidates[2].CurrentDisplay == "43" && candidates[3].CurrentDisplay == "?", "Batch current values were fabricated.");
            Check(candidates.All(candidate => candidate.FirstDisplay == "7" && candidate.PreviousDisplay == "7"), "Write changed historical scan values.");
            Check(memory.Writes == 4 && memory.Reads == 3 && memory.InstanceChecked, "Batch skipped process identity or retried.");
            await ExpectAsync<InvalidOperationException>(() => fixture.ViewModel.WriteCandidatesAsync(candidates, "1.5"));
            Check(memory.Writes == 4, "Invalid batch encoding partially wrote data.");
            await ExpectAsync<InvalidOperationException>(() => fixture.ViewModel.WriteCandidatesAsync([new ScanCandidate { Address = 1, ValueType = MemoryValueType.Int32 }], "42"));
            Check(memory.Writes == 4, "Foreign scan generation wrote data.");
        }

        foreach (var mode in Enum.GetValues<MemoryWriteState>())
        {
            using var fixture = new WriteFixture(Path.Combine(root, mode.ToString()));
            using var memory = new FakeMemory(mode);
            fixture.ViewModel.MemoryWriteAccessFactory = _ => memory;
            var verified = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var field = new SavedField { Name = "测试字段", ValueType = MemoryValueType.Int32, LastAddress = 1,
                ProcessStartTimeUtcTicks = fixture.Process.StartTimeUtc.Ticks, LastVerifiedUtc = verified, IsValueLocked = true, LockedValue = "12" };
            fixture.Version.Fields.Add(field); fixture.ViewModel.SelectedSavedField = field;
            if (mode == MemoryWriteState.ReadbackConfirmed) await fixture.ViewModel.WriteSelectedFieldAsync("42");
            else await ExpectAsync<InvalidOperationException>(() => fixture.ViewModel.UpdateSelectedFieldAsync("已改备注", "新分组", "42"));
            Check(field.CurrentValue == (mode == MemoryWriteState.ReadbackConfirmed ? "42" : mode == MemoryWriteState.ReadbackMismatch ? "43" : "—"), "Saved field displays an unverified target value.");
            Check((field.LastVerifiedUtc > verified) == (mode is MemoryWriteState.ReadbackConfirmed or MemoryWriteState.ReadbackMismatch), "Unknown/failed readback advanced verification time.");
            Check(fixture.ViewModel.StatusText.Contains(mode == MemoryWriteState.WriteFailed ? "写入失败" : mode == MemoryWriteState.ReadbackFailed ? "回读失败" : mode == MemoryWriteState.ReadbackMismatch ? "回读不一致" : "回读一致"), "Outer field edit masked the actual write outcome.");
            if (mode == MemoryWriteState.ReadbackConfirmed) Check(field.IsValueLocked && field.LockedValue == "42", "Confirmed write did not update the lock target.");
            else if (mode == MemoryWriteState.WriteFailed) Check(field.IsValueLocked && field.LockedValue == "12", "Failed write silently changed the prior lock target.");
            else
            {
                Check(!field.IsValueLocked && field.LockedValue.Length == 0 && field.Status.Contains("已暂停"), "Uncertain manual write did not pause the existing lock.");
                var saved = await new ProfileStore(fixture.Root).LoadAsync();
                Check(!saved.Games.Single().Versions.Single().Fields.Single().IsValueLocked, "Paused lock was not persisted.");
            }
            fixture.Version.ExecutableSha256 = "another-build";
            var previousWrites = memory.Writes;
            await ExpectAsync<InvalidOperationException>(() => fixture.ViewModel.WriteSelectedFieldAsync("45"));
            Check(memory.Writes == previousWrites, "A mismatched build reached the write operation.");
        }

        foreach (var mode in Enum.GetValues<MemoryWriteState>())
        {
            using var fixture = new WriteFixture(Path.Combine(root, "maintenance-" + mode));
            using var memory = new FakeMemory(mode);
            fixture.ViewModel.MemoryWriteAccessFactory = _ => memory;
            var verified = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var field = new SavedField { Name = "锁定反馈", ValueType = MemoryValueType.Int32, IsValueLocked = true, LockedValue = "42", LastAddress = 1,
                ProcessStartTimeUtcTicks = fixture.Process.StartTimeUtc.Ticks, LastVerifiedUtc = verified };
            fixture.Version.Fields.Add(field);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            field.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(SavedField.Status)) cancellation.Cancel(); };
            await RunMaintenanceAsync(fixture, cancellation.Token);
            Check(memory.Writes == 1 && memory.Reads == (mode == MemoryWriteState.WriteFailed ? 1 : 2), "Periodic lock introduced retries or skipped actual readback.");
            Check(field.CurrentValue == (mode == MemoryWriteState.ReadbackConfirmed ? "42" : mode == MemoryWriteState.ReadbackMismatch ? "43" : "—"), "Periodic lock displayed expected bytes as actual data.");
            Check((field.LastVerifiedUtc > verified) == (mode is MemoryWriteState.ReadbackConfirmed or MemoryWriteState.ReadbackMismatch), "Periodic lock falsely advanced verification time.");
        }

        using (var fixture = new WriteFixture(Path.Combine(root, "maintenance-no-write")))
        using (var memory = new FakeMemory(MemoryWriteState.ReadbackConfirmed))
        {
            fixture.ViewModel.MemoryWriteAccessFactory = _ => memory;
            var field = new SavedField { IsValueLocked = true, LockedValue = "7", LastAddress = 1, ProcessStartTimeUtcTicks = fixture.Process.StartTimeUtc.Ticks };
            fixture.Version.Fields.Add(field);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            field.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(SavedField.Status)) cancellation.Cancel(); };
            await RunMaintenanceAsync(fixture, cancellation.Token);
            Check(memory.Writes == 0 && field.CurrentValue == "7" && field.Status.Contains("目标一致") && !field.Status.Contains("已写入"), "An unchanged locked value falsely claimed a write.");
        }

        using (var fixture = new WriteFixture(Path.Combine(root, "maintenance-cancelled")))
        using (var memory = new FakeMemory(MemoryWriteState.ReadbackConfirmed))
        {
            fixture.ViewModel.MemoryWriteAccessFactory = _ => memory;
            var field = new SavedField { IsValueLocked = true, LockedValue = "42", CurrentValue = "旧显示", LastAddress = 1, ProcessStartTimeUtcTicks = fixture.Process.StartTimeUtc.Ticks };
            fixture.Version.Fields.Add(field);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            memory.AfterWriteRead = cancellation.Cancel;
            await RunMaintenanceAsync(fixture, cancellation.Token);
            Check(memory.Writes == 1 && field.CurrentValue == "旧显示", "Cancelled lock callback overwrote the current UI state.");
        }

        var payload = BitConverter.GetBytes(7);
        var pin = GCHandle.Alloc(payload, GCHandleType.Pinned);
        try
        {
            using var fixture = new WriteFixture(Path.Combine(root, "actual-self-buffer"));
            var address = unchecked((ulong)pin.AddrOfPinnedObject().ToInt64());
            var candidates = fixture.SetCandidates([address]);
            await fixture.ViewModel.WriteCandidatesAsync(candidates, "42");
            Check(BitConverter.ToInt32(payload) == 42 && candidates[0].CurrentDisplay == "42" && fixture.ViewModel.StatusText.Contains("回读一致 1"), "Actual self-process write/readback failed.");
            using var actual = new ProcessMemoryAccessor(Environment.ProcessId);
            try { actual.EnsureInstance(Environment.ProcessId, fixture.Process.StartTimeUtc.AddTicks(1)); throw new Exception("Incorrect process instance accepted."); }
            catch (InvalidOperationException) { _assertions++; }

            var field = new SavedField { Name = "锁定自有缓冲区", IsValueLocked = true, LockedValue = "43", ValueType = MemoryValueType.Int32,
                LastAddress = address, ProcessStartTimeUtcTicks = fixture.Process.StartTimeUtc.Ticks };
            fixture.Version.Fields.Add(field);
            using var cancellation = new CancellationTokenSource(100);
            await RunMaintenanceAsync(fixture, cancellation.Token);
            Check(BitConverter.ToInt32(payload) == 43 && field.CurrentValue == "43" && field.Status.Contains("回读一致"), "Periodic lock did not report the actual readback.");
        }
        finally { pin.Free(); }
    }

    private static Task RunMaintenanceAsync(WriteFixture fixture, CancellationToken cancellation) =>
        (Task)typeof(MainViewModel).GetMethod("MaintainLockedValuesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.ViewModel, [fixture.Process, fixture.Version, null, cancellation])!;

    private static async Task CheckLogsAsync(string root)
    {
        Directory.CreateDirectory(root);
        var normal = Path.Combine(root, "normal");
        var service = new CrashLogService(normal, Path.Combine(root, "normal-fallback"), 128 * 1024);
        var paths = await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() => service.TryWrite(new Exception($"原始错误-{index:D2} 中文")))));
        Check(paths.All(path => path == Path.Combine(normal, "crash.log")), "Concurrent logging failed or unexpectedly fell back.");
        var contents = File.ReadAllText(paths[0]!);
        Check(Enumerable.Range(0, 32).All(index => contents.Contains($"原始错误-{index:D2} 中文")), "Concurrent logger lost an entry.");

        var bounded = Path.Combine(root, "bounded");
        var boundedService = new CrashLogService(bounded, Path.Combine(root, "bounded-fallback"), 1024);
        for (var index = 0; index < 14; index++)
            Check(boundedService.TryWrite(new Exception($"顺序 {index} " + new string('中', 150))) is not null, "Bounded logger failed to rotate.");
        var files = Directory.GetFiles(bounded);
        Check(files.Length == 3 && files.All(file => new FileInfo(file).Length <= 1024), "Log count/size limits were exceeded.");
        Check(File.ReadAllText(Path.Combine(bounded, "crash.log")).Contains("顺序 13"), "Newest log entry was lost during rotation.");
        Check(boundedService.TryWrite(new Exception(string.Concat(Enumerable.Repeat("中😀", 10000)))) is not null, "Long Unicode entry failed.");
        Check(File.ReadAllText(Path.Combine(bounded, "crash.log")).Contains("已截断"), "Oversized error details were not marked as truncated.");
        Check(Directory.GetFiles(bounded).All(file => new FileInfo(file).Length <= 1024), "An oversized entry exceeded the log budget.");
        foreach (var file in Directory.GetFiles(bounded)) new UTF8Encoding(false, true).GetString(File.ReadAllBytes(file));
        _assertions++;

        foreach (var name in new[] { "crash.log", "crash.1.log", "crash.2.log" })
            File.WriteAllText(Path.Combine(bounded, name), new string('中', 3000) + "保留末尾");
        Check(boundedService.TryWrite(new Exception("缩减旧大日志")) is not null, "Old oversized logs prevented logging.");
        Check(Directory.GetFiles(bounded).Length == 3 && Directory.GetFiles(bounded).All(file => new FileInfo(file).Length <= 1024), "Legacy oversized logs remained unbounded or left temp files.");
        Check(File.ReadAllText(Path.Combine(bounded, "crash.1.log")).Contains("保留末尾"), "Trimming discarded the recent log tail.");
        foreach (var file in Directory.GetFiles(bounded)) new UTF8Encoding(false, true).GetString(File.ReadAllBytes(file));
        _assertions++;

        var locked = Path.Combine(root, "locked"); Directory.CreateDirectory(locked);
        var originalLog = Path.Combine(locked, "crash.log"); File.WriteAllText(originalLog, "原日志不能破坏");
        var fallback = Path.Combine(root, "locked-fallback");
        using (var lease = new FileStream(originalLog, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(new CrashLogService(locked, fallback).TryWrite(new Exception("文件占用时原错误")) == Path.Combine(fallback, "crash.log"), "Locked log did not fall back.");
        Check(File.ReadAllText(originalLog) == "原日志不能破坏" && File.ReadAllText(Path.Combine(fallback, "crash.log")).Contains("文件占用时原错误"), "Logging damaged evidence or lost original exception.");

        File.SetAttributes(originalLog, FileAttributes.ReadOnly);
        try
        {
            Check(new CrashLogService(locked, fallback).TryWrite(new Exception("只读文件的原错误")) == Path.Combine(fallback, "crash.log"), "Read-only log file did not fall back.");
            Check(File.ReadAllText(originalLog) == "原日志不能破坏", "Read-only log evidence was changed.");
        }
        finally { File.SetAttributes(originalLog, FileAttributes.Normal); }

        var blocked = Path.Combine(root, "not-a-directory"); File.WriteAllText(blocked, "不是日志目录");
        var blockedFallback = Path.Combine(root, "fallback-also-blocked"); File.WriteAllText(blockedFallback, "也不可写");
        var exception = new Exception("原始故障仍然可读");
        Check(new CrashLogService(blocked, blockedFallback).TryWrite(exception) is null && exception.Message == "原始故障仍然可读", "Double logging failure threw or fabricated a successful path.");
        var display = GameValueEditor.App.FormatCrashMessage(exception, null);
        Check(display.Contains("原始故障仍然可读") && display.Contains("日志无法写入") && !display.Contains("已经记录"), "Error prompt concealed the original error or claimed nonexistent logs.");
        Check(File.ReadAllText(blocked) == "不是日志目录" && File.ReadAllText(blockedFallback) == "也不可写", "Failure overwrote non-log files.");

        var defaultFallbacks = new List<string>();
        for (var index = 0; index < 2; index++)
        {
            var source = Path.Combine(root, "default-fallback-source-" + index);
            File.WriteAllText(source, "阻止创建主日志目录");
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToUpperInvariant())))[..16];
            var ownedFallback = Path.Combine(Path.GetTempPath(), "GameValueEditor", "crash-logs", identity);
            Check(!Directory.Exists(ownedFallback), "Default fallback fixture collided with an existing directory.");
            var expectedPath = Path.Combine(ownedFallback, "crash.log");
            try
            {
                var defaultService = new CrashLogService(source);
                Check(defaultService.TryWrite(new Exception("默认兜底目录")) == expectedPath && defaultService.TryWrite(new Exception("同目录重复错误")) == expectedPath,
                    "Default fallback was not stable for one data-directory identity.");
                defaultFallbacks.Add(expectedPath);
                Check(File.ReadAllText(expectedPath).Contains("同目录重复错误"), "Default fallback lost the original exception.");
            }
            finally
            {
                // This exact directory did not exist before this fixture; no parent/tree cleanup.
                if (File.Exists(expectedPath)) File.Delete(expectedPath);
                if (Directory.Exists(ownedFallback)) Directory.Delete(ownedFallback, false);
            }
        }
        Check(defaultFallbacks[0] != defaultFallbacks[1], "Different installations shared a fallback log file.");
        var formattingPath = service.TryWrite(new UnformattableException());
        Check(formattingPath is not null && File.ReadAllText(formattingPath).Contains("无法格式化异常详情"), "Exception formatting failure defeated the logger.");

        var outside = Path.Combine(root, "link-target"); Directory.CreateDirectory(outside);
        var link = Path.Combine(root, "linked-log-directory");
        try
        {
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (Exception linkError) when (linkError is UnauthorizedAccessException or IOException)
            {
                // NTFS junctions do not need the symbolic-link privilege; creation only, no shell deletion.
                using var creator = System.Diagnostics.Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{outside}\"")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
                await creator.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (creator.ExitCode != 0) throw new IOException("Link fixture could not be created.");
            }
            var linkedFallback = Path.Combine(root, "link-fallback");
            Check(new CrashLogService(link, linkedFallback).TryWrite(new Exception("拒绝链接")) == Path.Combine(linkedFallback, "crash.log"), "Linked log path was not rejected.");
            Check(Directory.GetFiles(outside).Length == 0, "Logger wrote through a link.");
        }
        catch (Exception linkError) when (linkError is UnauthorizedAccessException or IOException)
        { Console.WriteLine("Log link fixture unavailable: " + linkError.GetType().Name); }
    }

    private sealed class UnformattableException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("Formatting failed.");
    }

    private sealed class FakeMemory(MemoryWriteState mode) : IMemoryWriteAccess
    {
        private readonly Dictionary<ulong, byte[]> _written = [];
        internal readonly Dictionary<ulong, MemoryWriteState> Modes = [];
        internal int Writes, Reads;
        internal bool InstanceChecked, ShortRead;
        internal Action? AfterWriteRead;
        public void EnsureInstance(int processId, DateTime startTimeUtc) => InstanceChecked = true;
        public bool TryGetModuleBase(string moduleName, out ulong baseAddress) { baseAddress = 0; return false; }
        public bool TryWrite(ulong address, byte[] data, out string error)
        {
            Writes++; error = "模拟写入失败";
            if (Modes.GetValueOrDefault(address, mode) == MemoryWriteState.WriteFailed) return false;
            _written[address] = data.ToArray(); error = ""; return true;
        }
        public bool TryRead(ulong address, int length, out byte[] data)
        {
            Reads++; data = [];
            var state = Modes.GetValueOrDefault(address, mode);
            if (!_written.TryGetValue(address, out var written)) { data = BitConverter.GetBytes(7); return true; }
            AfterWriteRead?.Invoke();
            if (state == MemoryWriteState.ReadbackFailed) return false;
            data = written.ToArray();
            if (state == MemoryWriteState.ReadbackMismatch) data[0] ^= 1;
            if (ShortRead) data = data[..^1];
            return true;
        }
        public void Dispose() { }
    }

    internal sealed class WriteFixture : IDisposable
    {
        internal string Root { get; }
        internal MainViewModel ViewModel { get; }
        internal GameVersionProfile Version { get; } = new() { ExecutableSha256 = "fixture-build" };
        internal ProcessItem Process { get; }
        private readonly GameAdapterRegistry _registry;

        internal WriteFixture(string root)
        {
            Root = root;
            _registry = new GameAdapterRegistry(Path.Combine(root, "modules"));
            ViewModel = ModuleLifecycleRegressionTests.CreateViewModel(root, new GameModuleCatalogService(Path.Combine(root, "modules")), _registry);
            var game = new GameProfile { Name = "写入测试", Versions = [Version] };
            ViewModel.Games.Add(game); ViewModel.SelectedGame = game; ViewModel.SelectedVersion = Version;
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            Process = new ProcessItem { ProcessId = Environment.ProcessId, StartTimeUtc = self.StartTime.ToUniversalTime() };
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.AttachedProcess))!.SetValue(ViewModel, Process);
            SetField("_attachedGameId", game.Id);
            SetField("_attachedFingerprint", new VersionFingerprint("", "", "", "fixture-build", 0, "x64", "fixture-build", "", ""));
        }

        internal IReadOnlyList<ScanCandidate> SetCandidates(ulong[] addresses)
        {
            var folder = Path.Combine(Root, "scan", "owned-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            var dataPath = Path.Combine(folder, "part.bin");
            using (var writer = new BinaryWriter(File.Create(dataPath)))
                for (var index = 0; index < 100; index++) { writer.Write(addresses[index % addresses.Length]); writer.Write(7); writer.Write(7); }
            var partition = new ScanCandidatePartition { FilePath = dataPath, ValueType = MemoryValueType.Int32, SearchRoutineId = SearchRoutineIds.DirectNumeric,
                SearchRoutineName = "直接数值", ScaleMultiplier = 1, FirstBytes = BitConverter.GetBytes(7), Count = 100 };
            var store = new ScanCandidateStore(Path.Combine(Root, "scan"), folder, [partition], new FileStream(Path.Combine(folder, "store.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                { ProcessId = Process.ProcessId, ProcessStartTimeUtc = Process.StartTimeUtc };
            SetField("_scanCandidates", store);
            return store.ReadCandidates(addresses.Length);
        }

        private void SetField(string name, object? value) => typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ViewModel, value);
        public void Dispose() { ViewModel.Shutdown(); _registry.Dispose(); }
    }
}
