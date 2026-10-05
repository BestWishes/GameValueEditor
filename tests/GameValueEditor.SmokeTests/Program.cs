using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameValueEditor;
using GameValueEditor.Dialogs;
using GameValueEditor.Models;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Services;
using GameValueEditor.Services.Adapters;
using GameValueEditor.ViewModels;

if (args.Contains("--speed-target", StringComparer.OrdinalIgnoreCase))
{
    var targetClock = Stopwatch.StartNew();
    Console.WriteLine($"READY {Environment.ProcessId}");
    for (var sample = 0; sample < 4; sample++)
    {
        Thread.Sleep(1000);
        Console.WriteLine(targetClock.ElapsedMilliseconds);
    }
    return 0;
}

if (args.Contains("--speed-stress-target", StringComparer.OrdinalIgnoreCase))
    return await SpeedStressTarget.RunAsync();

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

try
{
    Assert(MemoryValueCodec.TryParse("123456", MemoryValueType.Int32, out var integerBytes), "Int32 parse failed");
    Assert(MemoryValueCodec.Format(integerBytes, MemoryValueType.Int32) == "123456", "Int32 roundtrip failed");
    Assert(MemoryValueCodec.TryParse("1.95", MemoryValueType.Double, out var doubleBytes), "Double parse failed");
    Assert(Math.Abs(BitConverter.ToDouble(doubleBytes) - 1.95) < 0.0000001, "Double roundtrip failed");
    Assert(MemoryValueCodec.TryParseEncoded("123", MemoryValueType.Int32, 3, out var scaledBytes), "Scaled Int32 parse failed");
    Assert(BitConverter.ToInt32(scaledBytes) == 369, "Scaled Int32 encoding failed");
    Assert(MemoryValueCodec.FormatDecoded(scaledBytes, MemoryValueType.Int32, 3) == "123", "Scaled Int32 decoding failed");

    var semanticFieldKey = ModuleFieldKey.Create("game.test.materials", "container:rune shattering", "quantity");
    Assert(ModuleFieldKey.TryParse(semanticFieldKey, out var editorId, out var entityId, out var fieldId) &&
           editorId == "game.test.materials" && entityId == "container:rune shattering" && fieldId == "quantity",
        "Generic entity editor semantic field key roundtrip failed");
    var genericField = new AdapterEditorField("quantity", "数量", 12, 0, 99);
    Assert(genericField.ValueDisplay == "12" && genericField.RangeDisplay == "0 ~ 99",
        "Generic entity editor field display contract failed");

    const int marker = 0x13579BDF;
    const int replacement = 0x2468ACE;
    var payload = new byte[128];
    BitConverter.GetBytes(marker).CopyTo(payload, 19);
    var handle = GCHandle.Alloc(payload, GCHandleType.Pinned);
    try
    {
        var expectedAddress = unchecked((ulong)handle.AddrOfPinnedObject().ToInt64()) + 19;
        var scanner = new MemoryScanService();
        var scanResult = await scanner.InitialExactScanAsync(
            Environment.ProcessId,
            [new ScanTargetDefinition(
                MemoryValueType.Int32,
                SearchRoutineIds.DirectNumeric,
                "直接数值",
                1d,
                BitConverter.GetBytes(marker))],
            writableOnly: true,
            alignedOnly: false,
            progress: null,
            CancellationToken.None);
        using var markerCandidates = scanResult.Candidates;
        var markerPreview = markerCandidates.ReadCandidates(10_000);
        Assert(markerPreview.Any(candidate => candidate.Address == expectedAddress), "Pinned marker was not found by memory scan");
        var markerCandidate = markerPreview.First(candidate => candidate.Address == expectedAddress);
        Assert(markerCandidate.FirstBytes.SequenceEqual(BitConverter.GetBytes(marker)), "Initial scan value was not preserved");
        Assert(markerCandidate.FirstDisplay == marker.ToString(), "Initial scan display value is invalid");

        using var memory = new ProcessMemoryAccessor(Environment.ProcessId);
        Assert(memory.TryWrite(expectedAddress, BitConverter.GetBytes(replacement), out var error), $"Memory write failed: {error}");
        Assert(BitConverter.ToInt32(payload, 19) == replacement, "Memory write did not update the target value");
    }
    finally
    {
        handle.Free();
    }

    const int overflowMarker = 0x31415926;
    const int overflowReplacement = 0x27182818;
    var overflowPayload = Enumerable.Repeat(overflowMarker, 250_500).ToArray();
    var overflowHandle = GCHandle.Alloc(overflowPayload, GCHandleType.Pinned);
    try
    {
        var finalAddress = unchecked((ulong)overflowHandle.AddrOfPinnedObject().ToInt64()) +
                           (ulong)((overflowPayload.Length - 1) * sizeof(int));
        var scanner = new MemoryScanService();
        var initial = await scanner.InitialExactScanAsync(
            Environment.ProcessId,
            [new ScanTargetDefinition(
                MemoryValueType.Int32,
                SearchRoutineIds.DirectNumeric,
                "直接数值",
                1d,
                BitConverter.GetBytes(overflowMarker))],
            writableOnly: true,
            alignedOnly: true,
            progress: null,
            CancellationToken.None);
        using var initialCandidates = initial.Candidates;
        Assert(initialCandidates.Count >= overflowPayload.Length,
            "Initial scan still discarded candidates at the former 250,000-result limit");
        Assert(initialCandidates.ReadCandidates(1_000).Count == 1_000,
            "Candidate preview did not respect its requested display limit");

        overflowPayload[^1] = overflowReplacement;
        var filtered = await scanner.NextScanAsync(
            Environment.ProcessId,
            initialCandidates,
            ScanComparison.Exact,
            _ => BitConverter.GetBytes(overflowReplacement),
            progress: null,
            CancellationToken.None);
        using var filteredCandidates = filtered.Candidates;
        Assert(filteredCandidates.ReadCandidates(1_000).Any(candidate => candidate.Address == finalAddress),
            "A candidate beyond the former result limit was not retained for the next scan");
    }
    finally
    {
        overflowHandle.Free();
    }

    var executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
    Assert(!string.IsNullOrWhiteSpace(executable), "Unable to resolve test executable path");
    var fingerprint = await new VersionFingerprintService().CreateAsync(executable!);
    Assert(fingerprint.Sha256.Length == 64, "SHA-256 fingerprint is invalid");
    Assert(fingerprint.BuildSha256.Length == 64, "Build fingerprint is invalid");
    Assert(fingerprint.FileSize > 0, "Executable size is invalid");
    Assert(Path.GetFullPath(ProfileStore.DefaultRoot).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase),
        "Default profile directory must stay beside the application");

    Assert(!SemanticVersion.TryParse("0.3.0-preview.1", out _),
        "Application versions must not accept prerelease suffixes");
    Assert(SemanticVersion.TryParse("0.3.0", out var formalVersion) &&
           formalVersion == new SemanticVersion(0, 3, 0), "Formal version parse failed");
    Assert(SemanticVersion.TryParse("1.2.10", out var higherPatch) &&
           SemanticVersion.TryParse("1.2.9", out var lowerPatch) && higherPatch.CompareTo(lowerPatch) > 0,
        "Semantic patch comparison failed");
    Assert(ModuleHostApi.CurrentVersion == 4, "Host API version was not advanced for module-owned page contracts");
    var api4Adapter = new SmokeTestModuleAdapter();
    GameEditorPageResolver.ValidateApi4Provider(api4Adapter);
    Assert(GameEditorPageResolver.Resolve(api4Adapter).Select(page => page.EditorId)
               .SequenceEqual(api4Adapter.Editors.Select(editor => editor.Id)),
        "Host API 4 module-owned page order or identity was not retained");

    if (args.Contains("--update-live", StringComparer.OrdinalIgnoreCase))
    {
        var liveUpdateRoot = Path.Combine(Path.GetTempPath(), $"GameValueEditor-LiveUpdate-{Guid.NewGuid():N}");
        var liveUpdateService = new ApplicationUpdateService(liveUpdateRoot, currentVersion: "0.4.0");
        var liveUpdate = await liveUpdateService.CheckAsync();
        Assert(liveUpdate.AssetName.StartsWith("GameValueEditor-v", StringComparison.OrdinalIgnoreCase) &&
               liveUpdate.AssetName.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase),
            $"Live update check selected a non-application release asset: {liveUpdate.AssetName}");
        Console.WriteLine($"Live application release passed: v{liveUpdate.Version}, {liveUpdate.AssetName}");
    }

    var connectedGame = new GameProfile { Name = "连接中", IsConnected = true, LastUsedUtc = DateTime.UtcNow.AddDays(-10) };
    var pinnedGame = new GameProfile { Name = "已置顶", IsPinned = true, LastUsedUtc = DateTime.UtcNow };
    var recentGame = new GameProfile { Name = "最近使用", LastUsedUtc = DateTime.UtcNow };
    var orderedGames = new[] { recentGame, pinnedGame, connectedGame }
        .OrderBy(gameItem => gameItem, new GameProfileConnectionComparer())
        .ToList();
    Assert(ReferenceEquals(orderedGames[0], connectedGame) && ReferenceEquals(orderedGames[1], pinnedGame),
        "Connected games were not sorted before pinned and recent games");
    var moduleGame = new GameProfile { Name = "模块游戏", IsModuleInstalled = true };
    var moduleSorted = new[] { recentGame, moduleGame }
        .OrderBy(gameItem => gameItem, new GameProfileConnectionComparer(GameLibrarySortMode.LocalModule))
        .ToList();
    Assert(ReferenceEquals(moduleSorted[0], moduleGame) && moduleGame.Badges.Contains("🧩", StringComparison.Ordinal),
        "Local-module sorting or badge failed");
    var nameSorted = new[] { new GameProfile { Name = "乙" }, new GameProfile { Name = "甲" } }
        .OrderBy(gameItem => gameItem, new GameProfileConnectionComparer(GameLibrarySortMode.Name))
        .ToList();
    Assert(nameSorted[0].Name == "甲", "Name sorting failed");

    var processService = new ProcessService();
    var logicalStart = DateTime.UtcNow.AddMinutes(-2);
    const string electronPath = @"C:\Games\Vespera\Vespera.exe";
    var electronProcesses = new[]
    {
        new ProcessItem { ProcessId = 100, ParentProcessId = 50, ProcessName = "Vespera", ExecutablePath = electronPath, StartTimeUtc = logicalStart, RuntimeKind = GameRuntimeKind.Electron, Role = GameProcessRole.Main, WorkingSetBytes = 80 },
        new ProcessItem { ProcessId = 101, ParentProcessId = 100, ProcessName = "Vespera", ExecutablePath = electronPath, StartTimeUtc = logicalStart.AddSeconds(1), RuntimeKind = GameRuntimeKind.Electron, Role = GameProcessRole.Gpu, WorkingSetBytes = 120 },
        new ProcessItem { ProcessId = 102, ParentProcessId = 100, ProcessName = "Vespera", ExecutablePath = electronPath, StartTimeUtc = logicalStart.AddSeconds(2), RuntimeKind = GameRuntimeKind.Electron, Role = GameProcessRole.Renderer, WorkingSetBytes = 500 },
        new ProcessItem { ProcessId = 103, ParentProcessId = 100, ProcessName = "Vespera", ExecutablePath = electronPath, StartTimeUtc = logicalStart.AddSeconds(3), RuntimeKind = GameRuntimeKind.Electron, Role = GameProcessRole.Network, WorkingSetBytes = 70 },
        new ProcessItem { ProcessId = 104, ParentProcessId = 100, ProcessName = "Vespera", ExecutablePath = electronPath, StartTimeUtc = logicalStart.AddSeconds(4), RuntimeKind = GameRuntimeKind.Electron, Role = GameProcessRole.Audio, WorkingSetBytes = 60 },
        new ProcessItem { ProcessId = 200, ParentProcessId = 50, ProcessName = "Vespera", ExecutablePath = electronPath, StartTimeUtc = logicalStart.AddMinutes(1), RuntimeKind = GameRuntimeKind.Electron, Role = GameProcessRole.Main, WorkingSetBytes = 90 },
        new ProcessItem { ProcessId = 201, ParentProcessId = 200, ProcessName = "Vespera", ExecutablePath = electronPath, StartTimeUtc = logicalStart.AddMinutes(1).AddSeconds(1), RuntimeKind = GameRuntimeKind.Electron, Role = GameProcessRole.Renderer, WorkingSetBytes = 600 }
    };
    Assert(electronProcesses.All(process =>
            !process.DisplayName.Contains("主进程", StringComparison.Ordinal) &&
            !process.DisplayName.Contains("游戏数据", StringComparison.Ordinal) &&
            !process.DisplayName.Contains("辅助", StringComparison.Ordinal)),
        "Process list leaked internal role labels into user-facing display text");
    foreach (var selected in electronProcesses.Take(5))
    {
        var logicalGame = processService.ResolveLogicalGame(selected, electronProcesses);
        Assert(logicalGame.RootProcess.ProcessId == 100, $"Selecting PID {selected.ProcessId} did not resolve the same logical game root");
        Assert(logicalGame.DataProcess.ProcessId == 102, $"Selecting PID {selected.ProcessId} did not route to the renderer data process");
        Assert(logicalGame.Members.Count == 5, $"Selecting PID {selected.ProcessId} merged another game instance or omitted a member");
    }
    var secondInstance = processService.ResolveLogicalGame(electronProcesses[6], electronProcesses);
    Assert(secondInstance.RootProcess.ProcessId == 200 && secondInstance.DataProcess.ProcessId == 201 && secondInstance.Members.Count == 2,
        "Two instances with the same executable path were incorrectly merged");

    const string unityPath = @"C:\Games\WorldApart\WorldApart.exe";
    var unityProcesses = new[]
    {
        new ProcessItem
        {
            ProcessId = 400,
            ParentProcessId = 50,
            ProcessName = "WorldApart",
            ExecutablePath = unityPath,
            StartTimeUtc = logicalStart,
            RuntimeKind = GameRuntimeKind.UnityIl2Cpp,
            Role = GameProcessRole.Main,
            WorkingSetBytes = 20 * 1024 * 1024
        },
        new ProcessItem
        {
            ProcessId = 401,
            ParentProcessId = 400,
            ProcessName = "WorldApart",
            WindowTitle = "WorldApart",
            ExecutablePath = unityPath,
            StartTimeUtc = logicalStart.AddSeconds(1),
            RuntimeKind = GameRuntimeKind.UnityIl2Cpp,
            Role = GameProcessRole.Main,
            WorkingSetBytes = 4L * 1024 * 1024 * 1024
        }
    };
    foreach (var selected in unityProcesses)
    {
        var logicalGame = processService.ResolveLogicalGame(selected, unityProcesses);
        Assert(logicalGame.RootProcess.ProcessId == 400,
            $"Selecting Unity PID {selected.ProcessId} did not resolve the same logical game root");
        Assert(logicalGame.DataProcess.ProcessId == 401,
            $"Selecting Unity PID {selected.ProcessId} did not route to the window-owning game process");
        Assert(logicalGame.Members.Count == 2,
            $"Selecting Unity PID {selected.ProcessId} omitted a same-instance member");
    }

    var singleNative = new ProcessItem
    {
        ProcessId = 300,
        ProcessName = "SingleGame",
        ExecutablePath = @"C:\Games\Single\SingleGame.exe",
        StartTimeUtc = logicalStart,
        RuntimeKind = GameRuntimeKind.Native,
        Role = GameProcessRole.Main
    };
    var singleGroup = processService.ResolveLogicalGame(singleNative, new[] { singleNative });
    Assert(singleGroup.RootProcess.ProcessId == 300 && singleGroup.DataProcess.ProcessId == 300 && singleGroup.Members.Count == 1,
        "Single-process game routing changed unexpectedly");

    var nativeSpeedTargetPath = Environment.GetEnvironmentVariable("GVE_NATIVE_SPEED_TARGET");
    var speedTargetStart = new ProcessStartInfo(
        string.IsNullOrWhiteSpace(nativeSpeedTargetPath) ? Environment.ProcessPath! : nativeSpeedTargetPath,
        string.IsNullOrWhiteSpace(nativeSpeedTargetPath) ? "--speed-target" : string.Empty)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        CreateNoWindow = true
    };
    using (var speedTarget = Process.Start(speedTargetStart) ?? throw new InvalidOperationException("Unable to start speed test target"))
    using (var speedService = new ProcessSpeedService())
    {
        var ready = await speedTarget.StandardOutput.ReadLineAsync();
        Assert(ready?.StartsWith("READY ", StringComparison.Ordinal) == true, $"Unexpected speed target handshake: {ready}");
        try
        {
            speedService.Accelerate(speedTarget.Id, 0.001);
            throw new InvalidOperationException("Speed multiplier below 0.01 unexpectedly succeeded");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("0.01", StringComparison.Ordinal))
        {
        }
        var hookResult = speedService.Accelerate(speedTarget.Id, 2.5);
        Assert(hookResult.PatchedImportCount > 0, "No clock imports were patched");
        var acceleratedLine = await speedTarget.StandardOutput.ReadLineAsync();
        Assert(long.TryParse(acceleratedLine, out var acceleratedElapsed),
            $"Speed target returned an invalid sample: {acceleratedLine} ms");
        if (!string.IsNullOrWhiteSpace(nativeSpeedTargetPath))
            Assert(acceleratedElapsed is >= 1500 and <= 3800,
                $"Native speed target did not apply 2.5x: {acceleratedLine} ms");
        speedService.DetachSafely();
        Assert(!speedService.HasHooks && speedService.Multiplier == 1,
            "Safe detach kept editor-owned speed state alive");
        var normalizedLine = await speedTarget.StandardOutput.ReadLineAsync();
        Assert(long.TryParse(normalizedLine, out var normalizedElapsed), $"Invalid normalized sample: {normalizedLine}");
        var normalizedDelta = normalizedElapsed - acceleratedElapsed;
        if (!string.IsNullOrWhiteSpace(nativeSpeedTargetPath))
            Assert(normalizedDelta is >= 500 and <= 1800,
                $"Closing-time safe detach did not preserve continuous normal speed: delta={normalizedDelta} ms");

        var reattached = speedService.Accelerate(speedTarget.Id, 0.5);
        Assert(reattached.PatchedImportCount > 0, "Could not reattach to persistent normal-speed wrappers");
        var reacceleratedLine = await speedTarget.StandardOutput.ReadLineAsync();
        Assert(long.TryParse(reacceleratedLine, out var reacceleratedElapsed),
            $"Invalid reaccelerated sample: {reacceleratedLine}");
        var reacceleratedDelta = reacceleratedElapsed - normalizedElapsed;
        if (!string.IsNullOrWhiteSpace(nativeSpeedTargetPath))
            Assert(reacceleratedDelta is >= 150 and <= 1100,
                $"Reattaching at 0.5x lost clock continuity: delta={reacceleratedDelta} ms");

        var changedMultiplier = speedService.Accelerate(speedTarget.Id, 0.75);
        Assert(changedMultiplier.PatchedImportCount > 0, "Changing an active multiplier did not reattach speed hooks");
        var changedMultiplierLine = await speedTarget.StandardOutput.ReadLineAsync();
        Assert(long.TryParse(changedMultiplierLine, out var changedMultiplierElapsed),
            $"Invalid changed-multiplier sample: {changedMultiplierLine}");
        var changedMultiplierDelta = changedMultiplierElapsed - reacceleratedElapsed;
        if (!string.IsNullOrWhiteSpace(nativeSpeedTargetPath))
            Assert(changedMultiplierDelta is >= 300 and <= 1400,
                $"Changing to 0.75x lost clock continuity: delta={changedMultiplierDelta} ms");
        await speedTarget.WaitForExitAsync();
        Assert(speedTarget.ExitCode == 0, $"Speed target exited with {speedTarget.ExitCode}");
    }

    var speedStressStart = new ProcessStartInfo(Environment.ProcessPath!, "--speed-stress-target")
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        CreateNoWindow = true
    };
    using (var speedStressTarget = Process.Start(speedStressStart)
                                   ?? throw new InvalidOperationException("Unable to start speed stress target"))
    using (var speedService = new ProcessSpeedService())
    {
        var ready = await speedStressTarget.StandardOutput.ReadLineAsync();
        Assert(ready?.StartsWith("READY ", StringComparison.Ordinal) == true,
            $"Unexpected speed stress target handshake: {ready}");
        speedService.Accelerate(speedStressTarget.Id, 5);
        var stressMultipliers = new[] { 0.01d, 0.5d, 0.75d, 2.5d, 20d, 100d, 1d };
        for (var transition = 0; transition < 21; transition++)
        {
            await Task.Delay(15);
            var multiplier = stressMultipliers[transition % stressMultipliers.Length];
            if (multiplier == 1d)
                speedService.Normalize();
            else
                speedService.Accelerate(speedStressTarget.Id, multiplier);
        }
        speedService.DetachSafely();
        await speedStressTarget.StandardInput.WriteLineAsync("STOP");
        var result = await speedStressTarget.StandardOutput.ReadLineAsync();
        await speedStressTarget.WaitForExitAsync();
        Assert(speedStressTarget.ExitCode == 0 && result == "OK",
            $"Atomic speed transition stress failed: exit={speedStressTarget.ExitCode}, result={result}");
    }

    var cooldownViewModel = new MainViewModel();
    try
    {
        await cooldownViewModel.AccelerateGameAsync();
        throw new InvalidOperationException("Speed control without an attached process unexpectedly succeeded");
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains("连接游戏进程", StringComparison.Ordinal))
    {
    }
    Assert(!cooldownViewModel.CanAccelerate && !cooldownViewModel.CanRestoreSpeed,
        "Speed buttons were not disabled during the two-second interaction cooldown");
    await Task.Delay(2200);
    Assert(!cooldownViewModel.CanAccelerate && !cooldownViewModel.CanRestoreSpeed,
        "Speed buttons became enabled without an attached game after the interaction cooldown");

    var livenessViewModel = new MainViewModel();
    using (var currentProcess = Process.GetCurrentProcess())
    {
        var staleProcess = new ProcessItem
        {
            ProcessId = currentProcess.Id,
            ProcessName = currentProcess.ProcessName,
            ExecutablePath = executable!,
            StartTimeUtc = currentProcess.StartTime.ToUniversalTime().AddSeconds(1)
        };
        livenessViewModel.Attach(staleProcess);
        Assert(livenessViewModel.AttachedProcess is not null &&
               livenessViewModel.ConnectionText.StartsWith("已连接", StringComparison.Ordinal),
            "Liveness test could not create an attached session");
        Assert(livenessViewModel.SynchronizeConnectionStates() == 1,
            "Exited or PID-reused game session was not detected");
        Assert(livenessViewModel.AttachedProcess is null && livenessViewModel.ConnectionText == "未连接",
            "Stale game session remained connected after liveness synchronization");
    }
    livenessViewModel.Shutdown();

    var profileTestRoot = Path.Combine(Path.GetTempPath(), $"GameValueEditor-Smoke-{Guid.NewGuid():N}");
    Directory.CreateDirectory(profileTestRoot);
    try
    {
        var store = new ProfileStore(profileTestRoot);
        var document = new LibraryDocument { SchemaVersion = 2, Theme = "Dark" };
        var game = new GameProfile { Name = "测试游戏", ExecutablePath = executable! };
        var version = new GameVersionProfile
        {
            DisplayName = "test-build",
            ExecutableSha256 = fingerprint.Sha256,
            BuildFingerprint = fingerprint.BuildSha256,
            PreferredSearchRoutineId = SearchRoutineIds.ScaledNumeric,
            PreferredScaleMultiplier = 3
        };
        version.Fields.Add(new SavedField
        {
            Name = "测试物品",
            LocatorKind = "GameAdapter",
            AdapterId = "game.test.inventory.v1",
            AdapterFieldKey = "item-key",
            IsValueLocked = true,
            LockedValue = "321"
        });
        game.Versions.Add(version);
        document.Games.Add(game);
        await store.SaveAsync(document);
        var restored = await store.LoadAsync();
        Assert(restored.Theme == "Dark", "Theme persistence failed");
        Assert(restored.Games.Single().Versions.Single().PreferredSearchRoutineId == SearchRoutineIds.ScaledNumeric,
            "Preferred routine persistence failed");
        Assert(restored.Games.Single().Versions.Single().Fields.Single().AdapterFieldKey == "item-key",
            "Adapter field persistence failed");
        Assert(restored.SchemaVersion == 7, "Library schema version was not upgraded");
        Assert(restored.Games.Single().Versions.Single().BuildFingerprint == fingerprint.BuildSha256,
            "Build fingerprint persistence failed");
        Assert(restored.Games.Single().Versions.Single().Fields.Single().LockedValue == "321",
            "Locked field target persistence failed");
    }
    finally
    {
        if (profileTestRoot.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            Directory.Delete(profileTestRoot, true);
    }

    var serviceTestRoot = Path.Combine(Path.GetTempPath(), $"GameValueEditor-ServiceSmoke-{Guid.NewGuid():N}");
    Directory.CreateDirectory(serviceTestRoot);
    try
    {
        var moduleCatalogJson = """
        {
          "schemaVersion": 1,
          "hostApiVersion": 2,
          "modules": [{
            "id": "game.test.multi-editor",
            "version": "1.1.0",
            "displayName": "测试专属模块",
            "gameDisplayName": "测试游戏",
            "hostApiVersion": 2,
            "processNames": ["MatchedGame"],
            "compatibleBuilds": [{"executableSha256": "EXE", "gameAssemblySha256": "ASM", "metadataSha256": "META"}],
            "editors": [{"id":"test.inventory"},{"id":"test.characters"}],
            "downloadUrl": "https://example.invalid/module.zip",
            "sha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
          }]
        }
        """;
        using var catalogClient = new HttpClient(new StaticResponseHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(moduleCatalogJson, Encoding.UTF8, "application/json")
            }));
        var catalogService = new GameModuleCatalogService(Path.Combine(serviceTestRoot, "modules-check"), catalogClient);
        var matchingGame = new GameProfile { ProcessName = "MatchedGame" };
        var matchingVersion = new GameVersionProfile
        {
            ExecutableSha256 = "EXE",
            GameAssemblySha256 = "ASM",
            MetadataSha256 = "META"
        };
        var available = await catalogService.CheckAsync(matchingGame, matchingVersion);
        Assert(available.Availability == GameModuleAvailability.Available,
            "Compatible game module was not offered");
        var wrongGame = new GameProfile { ProcessName = "Long Live The Emperor" };
        var unavailable = await catalogService.CheckAsync(wrongGame, matchingVersion);
        Assert(unavailable.Availability == GameModuleAvailability.NotAvailable,
            "A game-specific module leaked into another game");
        var transientFingerprint = new VersionFingerprint("test", "", "", "EXE", 1, "x64", "", "ASM", "META");
        var transientAvailable = await catalogService.CheckAsync("MatchedGame", transientFingerprint);
        Assert(transientAvailable.Availability == GameModuleAvailability.Available,
            "An unlisted connected game could not check for a compatible module");
        var changedBuild = new VersionFingerprint("test", "", "", "OTHER-EXE", 1, "x64", "", "OTHER-ASM", "OTHER-META");
        var changedBuildAvailable = await catalogService.CheckAsync("MatchedGame", changedBuild);
        Assert(changedBuildAvailable.Availability == GameModuleAvailability.Available &&
               !changedBuildAvailable.IsExactBuildMatch &&
               changedBuildAvailable.StatusText.Contains("本地安全验证", StringComparison.Ordinal),
            "Module discovery was incorrectly hidden by an unlisted game build");

        var steamApps = Path.Combine(serviceTestRoot, "Steam", "steamapps");
        var steamGameDirectory = Path.Combine(steamApps, "common", "A1");
        Directory.CreateDirectory(steamGameDirectory);
        var steamExecutable = Path.Combine(steamGameDirectory, "WorldApart.exe");
        await File.WriteAllBytesAsync(steamExecutable, [0x4D, 0x5A, 0, 0]);
        await File.WriteAllTextAsync(Path.Combine(steamApps, "appmanifest_4209920.acf"), """
        "AppState"
        {
            "appid" "4209920"
            "name" "不问凡尘"
            "installdir" "A1"
            "buildid" "25617557"
        }
        """);
        var platformMetadata = new GameVersionMetadataService().ReadPlatformMetadata(steamExecutable);
        Assert(platformMetadata.PlatformName == "Steam" && platformMetadata.AppId == "4209920" &&
               platformMetadata.BuildId == "25617557" && platformMetadata.DisplayName == "不问凡尘",
            "Steam game-declared build metadata was not resolved from the matching library manifest");

        var moduleAssemblyPath = Assembly.GetExecutingAssembly().Location;
        Assert(File.Exists(moduleAssemblyPath), "Smoke module assembly is unavailable");
        var moduleArchive = CreateModuleArchive("game.test.multi-editor", "1.1.0", moduleAssemblyPath);
        var moduleHash = Convert.ToHexString(SHA256.HashData(moduleArchive));
        using var moduleClient = new HttpClient(new StaticResponseHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(moduleArchive)
            }));
        var installService = new GameModuleCatalogService(Path.Combine(serviceTestRoot, "modules-install"), moduleClient);
        var remoteModule = available.RemoteModule!;
        remoteModule.Sha256 = moduleHash;
        var moduleProgress = new InlineProgress<DownloadProgressSnapshot>();
        await installService.InstallAsync(remoteModule, moduleProgress);
        Assert(moduleProgress.Values.Count > 0 && moduleProgress.Values[^1].Percentage == 100,
            "Module download did not report completion progress");
        Assert(installService.FindInstalled(remoteModule.Id)?.Version == "1.1.0",
            "Verified module installation was not persisted");
        var installedManifest = installService.GetInstalledManifest(remoteModule.Id);
        Assert(installedManifest?.GameDisplayName == "测试游戏" && installedManifest.Contributors.Count == 1,
            "Installed module identity or contributor metadata was not retained");
        using var installedRegistry = LoadAndVerifyInstalledModule(Path.Combine(serviceTestRoot, "modules-install"));
        var removedRecord = installService.Unregister(remoteModule.Id);
        Assert(removedRecord is not null && installService.FindInstalled(remoteModule.Id) is null,
            "Module unregister did not update installed.json");
        installService.RestoreRegistration(removedRecord!);
        Assert(installService.FindInstalled(remoteModule.Id) is not null,
            "Module registration rollback failed");
        _ = installService.Unregister(remoteModule.Id);
        var packageDeleted = await installService.DeletePackageAsync(remoteModule.Id);
        Assert(packageDeleted,
            "Formal module package remained locked while the loaded adapter was running from its shadow copy");
        Assert(!Directory.Exists(Path.Combine(serviceTestRoot, "modules-install", "packages", remoteModule.Id)),
            "Module package directory was not removed");
        installedRegistry.Reload();

        var lockedModuleId = "game.test.locked";
        var lockedModuleDirectory = Path.Combine(serviceTestRoot, "modules-install", "packages", lockedModuleId, "1.0.0");
        Directory.CreateDirectory(lockedModuleDirectory);
        var lockedModuleFile = Path.Combine(lockedModuleDirectory, "locked.dll");
        await File.WriteAllBytesAsync(lockedModuleFile, [1, 2, 3]);
        await using (var lockedStream = new FileStream(lockedModuleFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert(!await installService.DeletePackageAsync(lockedModuleId),
                "Locked module package unexpectedly reported a completed deletion");
            Assert(installService.HasPendingDeletion(lockedModuleId),
                "Failed module deletion was not recorded for startup cleanup");
        }
        _ = new GameModuleCatalogService(Path.Combine(serviceTestRoot, "modules-install"), moduleClient);
        Assert(!Directory.Exists(Path.Combine(serviceTestRoot, "modules-install", "packages", lockedModuleId)) &&
               !installService.HasPendingDeletion(lockedModuleId),
            "Pending module deletion was not completed after the lock was released");
        remoteModule.Id = "..\\escape";
        try
        {
            await installService.InstallAsync(remoteModule);
            throw new InvalidOperationException("Unsafe module ID unexpectedly installed");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("不安全", StringComparison.Ordinal))
        {
        }

        var releasesJson = """
        [
          {
            "tag_name": "module-fzzml-v1.0.0",
            "draft": false,
            "prerelease": false,
            "assets": [{
              "name": "GameValueEditor.Module.Fzzml-v1.0.0.zip",
              "browser_download_url": "https://example.invalid/module.zip",
              "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "size": 123
            }]
          },
          {
            "tag_name": "v9.0.0",
            "draft": false,
            "prerelease": true,
            "assets": [{
              "name": "GameValueEditor-v9.0.0-win-x64.zip",
              "browser_download_url": "https://example.invalid/prerelease.zip",
              "digest": "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
              "size": 999
            }]
          },
          {
            "tag_name": "v0.3.0",
            "draft": false,
            "prerelease": false,
            "assets": [{
              "name": "GameValueEditor-v0.3.0-complete-offline-win-x64.zip",
              "browser_download_url": "https://example.invalid/complete-offline.zip",
              "digest": "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
              "size": 999
            },{
              "name": "GameValueEditor-v0.3.0-win-x64.zip",
              "browser_download_url": "https://example.invalid/stable.zip",
              "digest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
              "size": 456
            }]
          },
          {
            "tag_name": "v0.2.0",
            "draft": false,
            "prerelease": false,
            "assets": [{
              "name": "GameValueEditor-v0.2.0-win-x64.zip",
              "browser_download_url": "https://example.invalid/stable.zip",
              "digest": "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
              "size": 789
            }]
          }
        ]
        """;
        using var releasesClient = new HttpClient(new StaticResponseHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(releasesJson, Encoding.UTF8, "application/json")
            }));
        var stableUpdateService = new ApplicationUpdateService(
            Path.Combine(serviceTestRoot, "stable-updates"), releasesClient, "0.2.0");
        var stableUpdate = await stableUpdateService.CheckAsync();
        Assert(stableUpdate.IsUpdateAvailable && stableUpdate.Version == "0.3.0",
            "Stable updater did not select the newest formal application release");
        Assert(stableUpdate.AssetName == "GameValueEditor-v0.3.0-win-x64.zip",
            "Application updater selected the complete offline bundle instead of the standard host package");
        var availableUpdateViewModel = new MainViewModel(stableUpdateService);
        await availableUpdateViewModel.CheckApplicationUpdateAsync();
        Assert(availableUpdateViewModel.HasApplicationUpdateAvailable && availableUpdateViewModel.CanUseApplicationUpdate,
            "Newly available update remained disabled by the check-button cooldown");
        availableUpdateViewModel.Shutdown();
        var currentUpdateService = new ApplicationUpdateService(
            Path.Combine(serviceTestRoot, "current-updates"), releasesClient, "0.3.0");
        var currentUpdate = await currentUpdateService.CheckAsync();
        Assert(!currentUpdate.IsUpdateAvailable && currentUpdate.Version == "0.3.0",
            "Current formal version unexpectedly selected a prerelease or module release");

        using var failingUpdateClient = new HttpClient(new StaticResponseHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var failingUpdateViewModel = new MainViewModel(new ApplicationUpdateService(
            Path.Combine(serviceTestRoot, "failing-updates"), failingUpdateClient, "0.3.0"));
        try
        {
            await failingUpdateViewModel.CheckApplicationUpdateAsync();
            throw new InvalidOperationException("Failed update check unexpectedly succeeded");
        }
        catch (HttpRequestException)
        {
        }
        Assert(failingUpdateViewModel.ApplicationUpdateStatusText == "　检查失败" &&
               failingUpdateViewModel.ApplicationUpdateActionText == "检查更新",
            "Failed application update check left the footer in its in-progress state");

        var updateArchive = Encoding.UTF8.GetBytes("verified update archive");
        var updateHash = Convert.ToHexString(SHA256.HashData(updateArchive));
        using var updateClient = new HttpClient(new StaticResponseHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(updateArchive)
            }));
        var updateService = new ApplicationUpdateService(Path.Combine(serviceTestRoot, "updates"), updateClient);
        var updateProgress = new InlineProgress<DownloadProgressSnapshot>();
        var pending = await updateService.DownloadAsync(new ApplicationUpdateCheckResult(
            true, "99.0.0", "GameValueEditor-v99.0.0-win-x64.zip",
            "https://example.invalid/update.zip", updateHash, updateArchive.Length), updateProgress);
        Assert(File.Exists(pending.ArchivePath) && File.Exists(updateService.PendingManifestPath),
            "Verified application update was not marked for next startup");
        Assert(updateProgress.Values.Count > 0 && updateProgress.Values[^1].Percentage == 100,
            "Application download did not report completion progress");

        var stalledDestination = Path.Combine(serviceTestRoot, "stalled.download");
        using var stalledClient = new HttpClient(new StaticResponseHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream())
            }));
        try
        {
            await HttpDownloadService.DownloadToFileAsync(stalledClient, "https://example.invalid/stalled",
                stalledDestination, 0, timeoutPolicy: new DownloadTimeoutPolicy(
                    TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(80)));
            throw new InvalidOperationException("Stalled download unexpectedly completed");
        }
        catch (TimeoutException exception) when (exception.Message.Contains("没有收到下载数据", StringComparison.Ordinal))
        {
        }
        Assert(!File.Exists(stalledDestination), "Timed-out download left a partial file behind");

        Directory.CreateDirectory(Path.GetDirectoryName(updateService.LastErrorNoticePath)!);
        await File.WriteAllTextAsync(updateService.LastErrorNoticePath,
            "{\"FailedAt\":\"2026-09-30T00:00:00+08:00\",\"Message\":\"文件仍被占用\",\"LogPath\":\"update-error.log\"}");
        Assert(updateService.TakeLastFailure()?.Message == "文件仍被占用" &&
               updateService.TakeLastFailure() is null,
            "Application update failure notice was not returned exactly once");

        var bootstrapRoot = Path.Combine(serviceTestRoot, "bootstrap");
        var bootstrapUpdates = Path.Combine(bootstrapRoot, "data", "updates");
        var bootstrapVersionDirectory = Path.Combine(bootstrapUpdates, "1.0.0");
        Directory.CreateDirectory(bootstrapVersionDirectory);
        var bootstrapArchive = Path.Combine(bootstrapVersionDirectory, "GameValueEditor-v1.0.0-win-x64.zip");
        var packagedUpdater = Encoding.UTF8.GetBytes("new-updater-from-verified-package");
        using (var archive = ZipFile.Open(bootstrapArchive, ZipArchiveMode.Create))
        {
            var updaterEntry = archive.CreateEntry("GameValueEditor.Updater.exe");
            await using (var updaterStream = updaterEntry.Open())
                await updaterStream.WriteAsync(packagedUpdater);
            var applicationEntry = archive.CreateEntry("GameValueEditor.exe");
            await using (var applicationStream = applicationEntry.Open())
                await applicationStream.WriteAsync(Encoding.UTF8.GetBytes("new-app"));
        }
        var bootstrapHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(bootstrapArchive)));
        await File.WriteAllTextAsync(Path.Combine(bootstrapUpdates, "pending-update.json"),
            $$"""{"Version":"1.0.0","ArchivePath":"{{bootstrapArchive.Replace("\\", "\\\\")}}","Sha256":"{{bootstrapHash}}","DownloadedUtc":"2026-10-05T00:00:00Z"}""");
        await File.WriteAllTextAsync(Path.Combine(bootstrapUpdates, "updater-stale.exe"), "old-broken-updater");
        await File.WriteAllTextAsync(Path.Combine(bootstrapUpdates, "failed-update-stale.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(bootstrapUpdates, "update-error.log"), "retain latest error");
        Directory.CreateDirectory(Path.Combine(bootstrapUpdates, "extract-stale"));
        var staleVersionDirectory = Path.Combine(bootstrapUpdates, "0.9.0");
        Directory.CreateDirectory(staleVersionDirectory);
        await File.WriteAllTextAsync(Path.Combine(staleVersionDirectory, "old.zip"), "old");

        var bootstrapService = new ApplicationUpdateService(bootstrapUpdates);
        Assert(File.Exists(bootstrapArchive) && File.Exists(Path.Combine(bootstrapUpdates, "update-error.log")),
            "Update cleanup removed the active package or latest diagnostic");
        Assert(!File.Exists(Path.Combine(bootstrapUpdates, "updater-stale.exe")) &&
               !File.Exists(Path.Combine(bootstrapUpdates, "failed-update-stale.json")) &&
               !Directory.Exists(Path.Combine(bootstrapUpdates, "extract-stale")) &&
               !Directory.Exists(staleVersionDirectory),
            "Update cleanup left stale runners, manifests, extraction directories, or old packages");
        var preparedRunner = bootstrapService.PrepareUpdaterRunner();
        Assert((await File.ReadAllBytesAsync(preparedRunner)).SequenceEqual(packagedUpdater),
            "Update bootstrap did not select the updater from the verified target package");
    }
    finally
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        if (serviceTestRoot.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
        {
            try { Directory.Delete(serviceTestRoot, true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Deferred temporary module cleanup: {exception.Message}");
            }
        }
    }

    Exception? dialogFailure = null;
    var dialogThread = new Thread(() =>
    {
        try
        {
            var application = new App();
            application.InitializeComponent();
            var dialog = new AdapterFieldDialog(["背包物品", "角色资源", "未分组"], "赤元丸", "赤元丸");
            dialog.ShowActivated = false;
            dialog.ShowInTaskbar = false;
            dialog.Opacity = 0;
            dialog.Left = -10_000;
            dialog.Top = -10_000;
            dialog.Show();
            dialog.ApplyTemplate();
            var comboBox = (ComboBox?)dialog.FindName("GroupComboBox")
                           ?? throw new InvalidOperationException("Group combo box was not created");
            comboBox.ApplyTemplate();
            var editor = comboBox.Template.FindName("PART_EditableTextBox", comboBox) as TextBox
                         ?? throw new InvalidOperationException("Editable combo box template part is missing");
            Assert(editor.Visibility == Visibility.Visible, "Editable group text box is not visible");
            Assert(editor.Text == "未分组", "Initial group text is not visible");

            editor.Text = "背包";
            var filteredGroups = comboBox.Items.Cast<string>().ToList();
            Assert(filteredGroups.SequenceEqual(["背包物品"]),
                $"Typing did not filter existing groups (text={editor.Text}, items={string.Join('|', filteredGroups)})");
            comboBox.SelectedItem = comboBox.Items[0];
            Assert(comboBox.Text == "背包物品",
                $"Selected group is not displayed (combo={comboBox.Text}, editor={editor.Text}, selected={comboBox.SelectedItem})");

            comboBox.SelectedItem = null;
            editor.Text = "全新分组";
            Assert(comboBox.Text == "全新分组", "Free-form group text was not retained");
            dialog.Close();

            var themeService = new ThemeService();
            var expectedThemes = new[]
            {
                ApplicationTheme.Light,
                ApplicationTheme.Dark,
                ApplicationTheme.EyeCareGreen,
                ApplicationTheme.WarmSand,
                ApplicationTheme.MistBlue
            };
            var windowColors = new HashSet<Color>();
            foreach (var theme in expectedThemes)
            {
                themeService.Apply(theme);
                var windowBrush = application.Resources["WindowBrush"] as SolidColorBrush
                                  ?? throw new InvalidOperationException($"{theme} did not provide WindowBrush");
                var textBrush = application.Resources["TextBrush"] as SolidColorBrush
                                ?? throw new InvalidOperationException($"{theme} did not provide TextBrush");
                Assert(windowBrush.Color != textBrush.Color, $"{theme} background and text colors are identical");
                windowColors.Add(windowBrush.Color);
            }
            Assert(windowColors.Count == expectedThemes.Length, "Theme window colors must be distinct");

            themeService.Apply(ApplicationTheme.Dark);
            var messageDialog = Activator.CreateInstance(
                                    typeof(MessageDialog),
                                    BindingFlags.Instance | BindingFlags.NonPublic,
                                    binder: null,
                                    args: new object[] { "操作未完成", "用于验证深色主题的弹框。", false },
                                    culture: null) as Window
                                ?? throw new InvalidOperationException("Message dialog could not be created for theme verification");
            Window[] themedDialogs =
            [
                new AdapterFieldDialog(),
                new GroupInputDialog([], "未分组"),
                messageDialog,
                new ModifyFieldDialog("测试字段", "未分组", "1", []),
                new ModuleContributorsDialog([
                    new GameModuleContributor
                    {
                        GithubId = 1,
                        GithubLogin = "octocat",
                        DisplayName = "The Octocat",
                        ProfileUrl = "https://github.com/octocat",
                        FirstContributionDate = new DateOnly(2026, 1, 1),
                        LatestContributionDate = new DateOnly(2026, 2, 1)
                    }
                ]),
                new SaveFieldDialog(),
                new TextInputDialog("输入", "请输入测试内容")
            ];
            var expectedWindowBrush = (SolidColorBrush)application.Resources["WindowBrush"];
            var expectedTextBrush = (SolidColorBrush)application.Resources["TextBrush"];
            var themedWindowStyle = (Style)application.Resources["ThemedWindowStyle"];
            foreach (var themedDialog in themedDialogs)
            {
                try
                {
                    themedDialog.ShowActivated = false;
                    themedDialog.ShowInTaskbar = false;
                    themedDialog.Left = -10_000;
                    themedDialog.Top = -10_000;
                    themedDialog.Show();
                    themedDialog.UpdateLayout();
                    Assert(ReferenceEquals(themedDialog.Style, themedWindowStyle),
                        $"{themedDialog.GetType().Name} does not use the shared themed window style");
                    Assert(themedDialog.Background is SolidColorBrush background && background.Color == expectedWindowBrush.Color,
                        $"{themedDialog.GetType().Name} does not use the dark theme background");
                    Assert(themedDialog.Foreground is SolidColorBrush foreground && foreground.Color == expectedTextBrush.Color,
                        $"{themedDialog.GetType().Name} does not use the dark theme text color");
                }
                finally
                {
                    themedDialog.Close();
                }
            }

            if (args.Contains("--render-ui", StringComparer.OrdinalIgnoreCase))
            {
                themeService.Apply(ApplicationTheme.Dark);
                var mainWindow = new MainWindow
                {
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Left = -10_000,
                    Top = -10_000,
                    Width = 1320,
                    Height = 820
                };
                mainWindow.Show();
                mainWindow.UpdateLayout();
                var topProcessConnectButton = (Button?)mainWindow.FindName("TopProcessConnectButton")
                                              ?? throw new InvalidOperationException("Top process connect button was not created");
                var editorGameConnectButton = (Button?)mainWindow.FindName("EditorGameConnectButton")
                                              ?? throw new InvalidOperationException("Editor game connect button was not created");
                var editorGameDisconnectButton = (Button?)mainWindow.FindName("EditorGameDisconnectButton")
                                                 ?? throw new InvalidOperationException("Editor game disconnect button was not created");
                Assert(BindingOperations.GetBinding(topProcessConnectButton, UIElement.IsEnabledProperty)?.Path.Path == nameof(MainViewModel.CanConnectProcess),
                    "Top connect button must follow the selected process");
                Assert(BindingOperations.GetBinding(editorGameConnectButton, UIElement.IsEnabledProperty)?.Path.Path == nameof(MainViewModel.CanConnectSelectedGame),
                    "Editor connect button must follow the selected library game");
                Assert(BindingOperations.GetBinding(editorGameDisconnectButton, UIElement.IsEnabledProperty)?.Path.Path == nameof(MainViewModel.CanDisconnectSelectedGame),
                    "Editor disconnect button must follow the selected library game");
                var editorModulesTabControl = (TabControl?)mainWindow.FindName("EditorModulesTabControl")
                                              ?? throw new InvalidOperationException("Editor modules tab control was not created");
                var mainTabs = (TabControl?)mainWindow.FindName("MainTabs")
                               ?? throw new InvalidOperationException("Main tab control was not created");
                var renderViewModel = (MainViewModel)mainWindow.DataContext;
                renderViewModel.AdapterEditorPages.Add(new AdapterInventoryEditorPageState(
                    new("test.inventory", "背包物品", GameEditorKind.Collection, 100, "测试集合页面"),
                    new("test.inventory", GameEditorPageRole.Inventory)));
                renderViewModel.AdapterEditorPages.Add(new AdapterCharacterEditorPageState(
                    new("test.characters", "人物属性", GameEditorKind.MasterDetail, 200, "测试人物页面"),
                    new("test.characters", GameEditorPageRole.CharacterAttributes),
                    true));
                renderViewModel.SelectedAdapterEditorPage = renderViewModel.AdapterEditorPages[0];
                mainTabs.SelectedIndex = 0;
                editorModulesTabControl.Visibility = Visibility.Visible;
                editorModulesTabControl.ApplyTemplate();
                var root = (FrameworkElement)mainWindow.Content;
                root.Measure(new Size(1320, 820));
                root.Arrange(new Rect(0, 0, 1320, 820));
                mainWindow.UpdateLayout();
                var inventoryEditorTab = (TabItem?)editorModulesTabControl.ItemContainerGenerator.ContainerFromIndex(0)
                                         ?? throw new InvalidOperationException("Dynamic inventory page tab was not created");
                var characterAttributesEditorTab = (TabItem?)editorModulesTabControl.ItemContainerGenerator.ContainerFromIndex(1)
                                                   ?? throw new InvalidOperationException("Dynamic character page tab was not created");
                var editorModuleHeaderPanel = (TabPanel?)editorModulesTabControl.Template.FindName(
                                                  "PART_EditorModuleHeaderPanel", editorModulesTabControl)
                                              ?? throw new InvalidOperationException("Vertical editor module header panel was not created");
                var editorModuleContentHost = (ContentPresenter?)editorModulesTabControl.Template.FindName(
                                                  "PART_SelectedContentHost", editorModulesTabControl)
                                              ?? throw new InvalidOperationException("Editor module content host was not created");
                var headerPosition = editorModuleHeaderPanel.TranslatePoint(new Point(), editorModulesTabControl);
                var contentPosition = editorModuleContentHost.TranslatePoint(new Point(), editorModulesTabControl);
                Assert(contentPosition.X > headerPosition.X + editorModuleHeaderPanel.ActualWidth,
                    $"Editor module content must render to the right of the vertical navigation " +
                    $"(header x={headerPosition.X}, width={editorModuleHeaderPanel.ActualWidth}; content x={contentPosition.X})");
                var inventoryTabPosition = inventoryEditorTab.TranslatePoint(new Point(), editorModuleHeaderPanel);
                var characterTabPosition = characterAttributesEditorTab.TranslatePoint(new Point(), editorModuleHeaderPanel);
                Assert(characterTabPosition.Y >= inventoryTabPosition.Y + inventoryEditorTab.ActualHeight,
                    "Editor module names must stack vertically in the left navigation");
                Assert(renderViewModel.Themes.Select(choice => choice.Display).SequenceEqual(
                        ["浅色", "深色", "护眼墨绿", "暖砂纸张", "雾蓝灰"]),
                    "Theme selector does not expose the five expected themes");
                root.Measure(new Size(1320, 820));
                root.Arrange(new Rect(0, 0, 1320, 820));
                root.UpdateLayout();
                var snapshotDirectory = Path.GetFullPath("artifacts");
                Directory.CreateDirectory(snapshotDirectory);
                string RenderMainWindow(string fileName)
                {
                    mainWindow.UpdateLayout();
                    root.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(1320, 820, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    var path = Path.Combine(snapshotDirectory, fileName);
                    using var snapshot = File.Create(path);
                    encoder.Save(snapshot);
                    return path;
                }

                var themeSnapshots = new Dictionary<ApplicationTheme, string>();
                foreach (var (theme, fileName) in new[]
                         {
                             (ApplicationTheme.Light, "ui-light-smoke.png"),
                             (ApplicationTheme.Dark, "ui-dark-smoke.png"),
                             (ApplicationTheme.EyeCareGreen, "ui-eye-care-green-smoke.png"),
                             (ApplicationTheme.WarmSand, "ui-warm-sand-smoke.png"),
                             (ApplicationTheme.MistBlue, "ui-mist-blue-smoke.png")
                         })
                {
                    themeService.Apply(theme);
                    themeSnapshots[theme] = RenderMainWindow(fileName);
                }

                themeService.Apply(ApplicationTheme.Dark);
                var renderedMessageDialog = Activator.CreateInstance(
                                                typeof(MessageDialog),
                                                BindingFlags.Instance | BindingFlags.NonPublic,
                                                binder: null,
                                                args: new object[] { "操作未完成", "用于验证深色主题下的错误提示弹框。", false },
                                                culture: null) as Window
                                            ?? throw new InvalidOperationException("Message dialog could not be created for rendering");
                renderedMessageDialog.ShowActivated = false;
                renderedMessageDialog.ShowInTaskbar = false;
                renderedMessageDialog.Left = -10_000;
                renderedMessageDialog.Top = -10_000;
                renderedMessageDialog.Show();
                renderedMessageDialog.UpdateLayout();
                var renderedDialogRoot = (FrameworkElement)renderedMessageDialog.Content;
                renderedDialogRoot.Measure(new Size(432, double.PositiveInfinity));
                var dialogWidth = 432;
                var dialogHeight = Math.Max(1, (int)Math.Ceiling(renderedDialogRoot.DesiredSize.Height));
                renderedDialogRoot.Arrange(new Rect(0, 0, dialogWidth, dialogHeight));
                renderedDialogRoot.UpdateLayout();
                var dialogBitmap = new RenderTargetBitmap(dialogWidth, dialogHeight, 96, 96, PixelFormats.Pbgra32);
                dialogBitmap.Render(renderedDialogRoot);
                var dialogEncoder = new PngBitmapEncoder();
                dialogEncoder.Frames.Add(BitmapFrame.Create(dialogBitmap));
                var dialogSnapshotPath = Path.Combine(snapshotDirectory, "ui-message-dialog-dark-smoke.png");
                using (var snapshot = File.Create(dialogSnapshotPath)) dialogEncoder.Save(snapshot);
                renderedMessageDialog.Close();

                var renderVersion = new GameVersionProfile
                {
                    DisplayName = "1.2.3",
                    FileVersion = "1.2.3.4",
                    ProductVersion = "1.2.3",
                    GameDeclaredVersion = "0.8.6",
                    GameDeclaredProductName = "不问凡尘",
                    GameDeclaredBuildGuid = "build-guid-smoke",
                    PlatformName = "Steam",
                    PlatformAppId = "4209920",
                    PlatformBuildId = "25617557",
                    PlatformDisplayName = "不问凡尘",
                    Architecture = "x64",
                    CollectedUtc = new DateTime(2026, 9, 27, 12, 30, 0, DateTimeKind.Local),
                    ExecutableSha256 = new string('a', 64),
                    LastVerifiedUtc = new DateTime(2026, 9, 27, 12, 30, 0, DateTimeKind.Local),
                    IsCurrentBuild = true
                };
                var renderGame = new GameProfile { Name = "排版验证游戏" };
                renderGame.Versions.Add(renderVersion);
                renderViewModel.Games.Add(renderGame);
                renderViewModel.SelectedGame = renderGame;
                renderViewModel.SelectedVersion = renderVersion;
                mainTabs.SelectedIndex = 3;
                mainWindow.UpdateLayout();
                root.UpdateLayout();
                var versionBitmap = new RenderTargetBitmap(1320, 820, 96, 96, PixelFormats.Pbgra32);
                versionBitmap.Render(root);
                var versionEncoder = new PngBitmapEncoder();
                versionEncoder.Frames.Add(BitmapFrame.Create(versionBitmap));
                var versionSnapshotPath = Path.GetFullPath(Path.Combine("artifacts", "ui-version-dark-smoke.png"));
                using (var snapshot = File.Create(versionSnapshotPath)) versionEncoder.Save(snapshot);
                mainWindow.Close();
                foreach (var (theme, path) in themeSnapshots)
                    Console.WriteLine($"Rendered {theme} UI: {path}");
                Console.WriteLine($"Rendered dark message dialog: {dialogSnapshotPath}");
                Console.WriteLine($"Rendered version UI: {versionSnapshotPath}");
            }
            application.Shutdown();
        }
        catch (Exception exception)
        {
            dialogFailure = exception;
        }
    });
    dialogThread.SetApartmentState(ApartmentState.STA);
    dialogThread.Start();
    dialogThread.Join();
    if (dialogFailure is not null) throw new InvalidOperationException("Editable group dialog smoke test failed", dialogFailure);

    foreach (var moduleArgument in args.Where(argument =>
                 argument.StartsWith("--verify-module-package=", StringComparison.OrdinalIgnoreCase)))
    {
        VerifyPackagedModule(moduleArgument[(moduleArgument.IndexOf('=') + 1)..]);
    }

    Console.WriteLine("Smoke tests passed: codec, scaled routine, scanner, writer, fingerprint.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

static byte[] CreateModuleArchive(string id, string version, string assemblyPath)
{
    using var stream = new MemoryStream();
    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
    {
        var manifest = archive.CreateEntry("module.json");
        using (var writer = new StreamWriter(manifest.Open(), Encoding.UTF8, leaveOpen: false))
            writer.Write($$"""
            {"id":"{{id}}","version":"{{version}}","displayName":"测试多编辑器游戏模块","gameDisplayName":"测试游戏","description":"测试","assemblyFile":"{{Path.GetFileName(assemblyPath)}}","hostApiVersion":2,"processNames":["MatchedGame"],"compatibleBuilds":[{"executableSha256":"EXE","gameAssemblySha256":"ASM","metadataSha256":"META"}],"contributors":[{"githubId":1,"githubLogin":"tester","displayName":"测试贡献者","profileUrl":"https://github.com/tester","firstContributionDate":"2026-01-01","latestContributionDate":"2026-01-02"}],"editors":[{"id":"test.inventory","displayName":"背包物品","kind":"collection","order":100,"sessionOnly":false},{"id":"test.characters","displayName":"人物属性","kind":"master-detail","order":200,"sessionOnly":true}]}
            """);
        var assembly = archive.CreateEntry(Path.GetFileName(assemblyPath));
        using var assemblyStream = assembly.Open();
        using var source = File.OpenRead(assemblyPath);
        source.CopyTo(assemblyStream);
    }
    return stream.ToArray();
}

[MethodImpl(MethodImplOptions.NoInlining)]
static GameAdapterRegistry LoadAndVerifyInstalledModule(string modulesDirectory)
{
    var installedRegistry = new GameAdapterRegistry(modulesDirectory);
    var loaded = installedRegistry.FindById("game.test.multi-editor");
    Assert(loaded is IInventoryGameAdapter && loaded is ICharacterAttributesGameAdapter && loaded.Editors.Count == 2,
        "Installed multi-editor module was not dynamically loaded through the module Host API");
    return installedRegistry;
}

static void VerifyPackagedModule(string archivePath)
{
    var resolvedArchive = Path.GetFullPath(archivePath);
    Assert(File.Exists(resolvedArchive), $"Module package does not exist: {resolvedArchive}");
    var verificationRoot = Path.Combine(Path.GetTempPath(), $"gve-module-contract-{Guid.NewGuid():N}");
    Directory.CreateDirectory(verificationRoot);
    try
    {
        string moduleId;
        string version;
        int hostApiVersion;
        string[] editorIds;
        int contributorCount;
        using (var archive = ZipFile.OpenRead(resolvedArchive))
        {
            var manifestEntry = archive.GetEntry("module.json")
                                ?? throw new InvalidOperationException("Module package is missing module.json.");
            using var manifestStream = manifestEntry.Open();
            using var document = JsonDocument.Parse(manifestStream);
            var root = document.RootElement;
            moduleId = root.GetProperty("id").GetString() ?? string.Empty;
            version = root.GetProperty("version").GetString() ?? string.Empty;
            hostApiVersion = root.GetProperty("hostApiVersion").GetInt32();
            editorIds = root.GetProperty("editors").EnumerateArray()
                .Select(editor => editor.GetProperty("id").GetString() ?? string.Empty).ToArray();
            contributorCount = root.TryGetProperty("contributors", out var contributors)
                ? contributors.GetArrayLength()
                : 0;
        }

        Assert(!string.IsNullOrWhiteSpace(moduleId) && !string.IsNullOrWhiteSpace(version),
            "Module package identity is incomplete.");
        var packageDirectory = Path.Combine(verificationRoot, "packages", moduleId, version);
        Directory.CreateDirectory(packageDirectory);
        using (var archive = ZipFile.OpenRead(resolvedArchive))
        {
            var packageRoot = Path.GetFullPath(packageDirectory) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                var destination = Path.GetFullPath(Path.Combine(packageDirectory, entry.FullName));
                Assert(destination.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase),
                    "Module package contains an unsafe path.");
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, true);
            }
        }

        var installed = new InstalledModuleDocument
        {
            Modules = [new InstalledModuleRecord(moduleId, version, DateTime.UtcNow)]
        };
        File.WriteAllText(Path.Combine(verificationRoot, "installed.json"), JsonSerializer.Serialize(installed));
        LoadAndVerifyPackagedModule(verificationRoot, moduleId, hostApiVersion, editorIds);
        Assert(contributorCount > 0, "Official packaged module has no contributor metadata.");
        Console.WriteLine($"Verified packaged module: {moduleId} v{version}, {editorIds.Length} pages.");
    }
    finally
    {
        for (var attempt = 0; attempt < 4 && Directory.Exists(verificationRoot); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { Directory.Delete(verificationRoot, true); }
            catch (UnauthorizedAccessException) when (attempt < 3) { Thread.Sleep(50); }
            catch (IOException) when (attempt < 3) { Thread.Sleep(50); }
        }
        Assert(!Directory.Exists(verificationRoot), "Module compatibility test left a locked temporary package.");
    }
}

[MethodImpl(MethodImplOptions.NoInlining)]
static void LoadAndVerifyPackagedModule(
    string modulesDirectory,
    string moduleId,
    int hostApiVersion,
    IReadOnlyCollection<string> editorIds)
{
    using var registry = new GameAdapterRegistry(modulesDirectory);
    Assert(registry.LoadErrors.Count == 0,
        $"Packaged module failed to load: {string.Join(" | ", registry.LoadErrors)}");
    IGameAdapter? adapter = registry.FindById(moduleId)
                            ?? throw new InvalidOperationException($"Packaged module did not export {moduleId}.");
    Assert(adapter.Editors.Select(editor => editor.Id).ToHashSet(StringComparer.Ordinal)
            .SetEquals(editorIds),
        "Packaged module editor identities changed after loading.");
    if (hostApiVersion >= 4)
    {
        IGameEditorPageProvider? pageProvider = adapter as IGameEditorPageProvider
                                                   ?? throw new InvalidOperationException(
                                                       "Host API 4 packaged module does not provide module-owned pages.");
        Assert(pageProvider.EditorPages.Select(page => page.EditorId).ToHashSet(StringComparer.Ordinal)
                .SetEquals(editorIds),
            "Packaged module page registrations do not match its editors.");
        pageProvider = null;
    }
    adapter = null;
}

public sealed class SmokeTestModuleAdapter :
    IInventoryGameAdapter,
    ICharacterAttributesGameAdapter,
    IGameEditorPageProvider
{
    public string Id => "game.test.multi-editor";
    public string DisplayName => "测试多编辑器游戏模块";
    public string Description => "仅用于宿主接口冒烟测试。";
    public IReadOnlyList<GameEditorDescriptor> Editors =>
    [
        new("test.inventory", "背包物品", GameEditorKind.Collection, 100, "测试集合编辑器"),
        new("test.characters", "人物属性", GameEditorKind.MasterDetail, 200, "测试主从编辑器", true)
    ];
    public IReadOnlyList<GameEditorPageRegistration> EditorPages { get; } =
    [
        new("test.inventory", GameEditorPageRole.Inventory),
        new("test.characters", GameEditorPageRole.CharacterAttributes)
    ];
    public bool Supports(GameProcessContext process, GameBuildIdentity fingerprint) => true;
    public AdapterFieldValue ReadField(GameProcessContext process, string fieldKey) => new(fieldKey, "1", "测试");
    public AdapterFieldValue WriteField(GameProcessContext process, string fieldKey, string displayValue) => new(fieldKey, displayValue, "测试");
    public IReadOnlyList<AdapterInventoryItem> ReadInventory(GameProcessContext process) => [new("test", "测试物品", 1)];
    public bool SupportsCharacterAttributes(GameProcessContext process) => true;
    public IReadOnlyList<AdapterCharacterItem> ReadCharacters(GameProcessContext process) =>
        [new("test", "测试人物", 1, [new("strength", "力道", 1, 1, 1f)])];
    public AdapterCharacterItem WriteCharacterAttribute(GameProcessContext process, string characterId, string attributeKey, int targetValue) =>
        new(characterId, "测试人物", 1, [new(attributeKey, "力道", targetValue, targetValue, 1f)]);
}

internal static class SpeedStressTarget
{
    private static int _stop;
    private static string? _failure;

    public static async Task<int> RunAsync()
    {
        _stop = 0;
        _failure = null;
        var workers = Enumerable.Range(0, Math.Max(4, Environment.ProcessorCount))
            .Select(_ => Task.Run(PollClocks))
            .ToArray();
        Console.WriteLine($"READY {Environment.ProcessId}");
        _ = await Console.In.ReadLineAsync();
        Volatile.Write(ref _stop, 1);
        await Task.WhenAll(workers);
        if (_failure is not null)
        {
            Console.WriteLine(_failure);
            return 5;
        }
        Console.WriteLine("OK");
        return 0;
    }

    private static void PollClocks()
    {
        var lastQpc = Stopwatch.GetTimestamp();
        var lastTick64 = Environment.TickCount64;
        while (Volatile.Read(ref _stop) == 0)
        {
            var qpc = Stopwatch.GetTimestamp();
            var tick64 = Environment.TickCount64;
            if (qpc < lastQpc || tick64 < lastTick64)
            {
                Interlocked.CompareExchange(ref _failure,
                    $"Clock moved backwards: qpc={lastQpc}->{qpc}, tick64={lastTick64}->{tick64}", null);
                Volatile.Write(ref _stop, 1);
                return;
            }
            lastQpc = qpc;
            lastTick64 = tick64;
            Thread.SpinWait(64);
        }
    }
}

internal sealed class StaticResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromResult(responseFactory(request));
}

internal sealed class InlineProgress<T> : IProgress<T>
{
    public List<T> Values { get; } = [];
    public void Report(T value) => Values.Add(value);
}

internal sealed class StallingStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
}
