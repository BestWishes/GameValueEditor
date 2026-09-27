using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
            MemoryValueType.Int32,
            BitConverter.GetBytes(marker),
            writableOnly: true,
            alignedOnly: false,
            progress: null,
            CancellationToken.None);
        Assert(scanResult.Candidates.Any(candidate => candidate.Address == expectedAddress), "Pinned marker was not found by memory scan");
        var markerCandidate = scanResult.Candidates.First(candidate => candidate.Address == expectedAddress);
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

    var executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
    Assert(!string.IsNullOrWhiteSpace(executable), "Unable to resolve test executable path");
    var fingerprint = await new VersionFingerprintService().CreateAsync(executable!);
    Assert(fingerprint.Sha256.Length == 64, "SHA-256 fingerprint is invalid");
    Assert(fingerprint.BuildSha256.Length == 64, "Build fingerprint is invalid");
    Assert(fingerprint.FileSize > 0, "Executable size is invalid");
    Assert(Path.GetFullPath(ProfileStore.DefaultRoot).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase),
        "Default profile directory must stay beside the application");

    Assert(SemanticVersion.TryParse("0.3.0-preview.1", out var previewVersion), "Preview version parse failed");
    Assert(SemanticVersion.TryParse("0.3.0", out var formalVersion), "Formal version parse failed");
    Assert(formalVersion.CompareTo(previewVersion) > 0, "Formal release must supersede its preview");
    Assert(SemanticVersion.TryParse("1.2.10", out var higherPatch) &&
           SemanticVersion.TryParse("1.2.9", out var lowerPatch) && higherPatch.CompareTo(lowerPatch) > 0,
        "Semantic patch comparison failed");

    if (args.Contains("--update-live", StringComparer.OrdinalIgnoreCase))
    {
        var liveUpdateRoot = Path.Combine(Path.GetTempPath(), $"GameValueEditor-LiveUpdate-{Guid.NewGuid():N}");
        var liveUpdateService = new ApplicationUpdateService(liveUpdateRoot, currentVersion: "0.3.0-preview.8");
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
        Assert(restored.SchemaVersion == 5, "Library schema version was not upgraded");
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
            "hostApiVersion": 2,
            "processNames": ["MatchedGame"],
            "compatibleBuilds": [{"executableSha256": "EXE", "gameAssemblySha256": "ASM", "metadataSha256": "META"}],
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
        await installService.InstallAsync(remoteModule);
        Assert(installService.FindInstalled(remoteModule.Id)?.Version == "1.1.0",
            "Verified module installation was not persisted");
        using (var installedRegistry = new GameAdapterRegistry(Path.Combine(serviceTestRoot, "modules-install")))
        {
            var loaded = installedRegistry.FindById("game.test.multi-editor");
            Assert(loaded is IInventoryGameAdapter && loaded is ICharacterAttributesGameAdapter && loaded.Editors.Count == 2,
                "Installed multi-editor module was not dynamically loaded through Host API v2");
        }
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
            "tag_name": "v0.3.0-preview.2",
            "draft": false,
            "prerelease": true,
            "assets": [{
              "name": "GameValueEditor-v0.3.0-preview.2-win-x64.zip",
              "browser_download_url": "https://example.invalid/preview.zip",
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
        var previewUpdateService = new ApplicationUpdateService(
            Path.Combine(serviceTestRoot, "preview-updates"), releasesClient, "0.3.0-preview.1");
        var previewUpdate = await previewUpdateService.CheckAsync();
        Assert(previewUpdate.IsUpdateAvailable && previewUpdate.Version == "0.3.0-preview.2",
            "Preview channel did not select the newest application preview after ignoring module releases");
        var availableUpdateViewModel = new MainViewModel(previewUpdateService);
        await availableUpdateViewModel.CheckApplicationUpdateAsync();
        Assert(availableUpdateViewModel.HasApplicationUpdateAvailable && availableUpdateViewModel.CanUseApplicationUpdate,
            "Newly available update remained disabled by the check-button cooldown");
        availableUpdateViewModel.Shutdown();
        var stableUpdateService = new ApplicationUpdateService(
            Path.Combine(serviceTestRoot, "stable-updates"), releasesClient, "0.2.0");
        var stableUpdate = await stableUpdateService.CheckAsync();
        Assert(!stableUpdate.IsUpdateAvailable && stableUpdate.Version == "0.2.0",
            "Stable channel unexpectedly selected an application preview or a module release");

        using var failingUpdateClient = new HttpClient(new StaticResponseHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var failingUpdateViewModel = new MainViewModel(new ApplicationUpdateService(
            Path.Combine(serviceTestRoot, "failing-updates"), failingUpdateClient, "0.3.0-preview.2"));
        try
        {
            await failingUpdateViewModel.CheckApplicationUpdateAsync();
            throw new InvalidOperationException("Failed update check unexpectedly succeeded");
        }
        catch (HttpRequestException)
        {
        }
        Assert(failingUpdateViewModel.ApplicationUpdateStatusPrefix == "检查失败 " &&
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
        var pending = await updateService.DownloadAsync(new ApplicationUpdateCheckResult(
            true, "99.0.0", "GameValueEditor-v99.0.0-win-x64.zip",
            "https://example.invalid/update.zip", updateHash, updateArchive.Length));
        Assert(File.Exists(pending.ArchivePath) && File.Exists(updateService.PendingManifestPath),
            "Verified application update was not marked for next startup");
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
                var renderViewModel = (MainViewModel)mainWindow.DataContext;
                Assert(renderViewModel.Themes.Select(choice => choice.Display).SequenceEqual(
                        ["浅色", "深色", "护眼墨绿", "暖砂纸张", "雾蓝灰"]),
                    "Theme selector does not expose the five expected themes");
                var root = (FrameworkElement)mainWindow.Content;
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

                var mainTabs = (TabControl?)mainWindow.FindName("MainTabs")
                               ?? throw new InvalidOperationException("Main tab control was not created");
                var renderVersion = new GameVersionProfile
                {
                    DisplayName = "1.2.3",
                    FileVersion = "1.2.3.4",
                    ProductVersion = "1.2.3",
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

    if (args.Contains("--fzzml-live", StringComparer.OrdinalIgnoreCase))
    {
        using var liveProcess = Process.GetProcessesByName("fzzml").FirstOrDefault()
                                ?? throw new InvalidOperationException("fzzml is not running");
        var path = liveProcess.MainModule?.FileName ?? throw new InvalidOperationException("Unable to resolve fzzml path");
        var liveItem = new ProcessItem
        {
            ProcessId = liveProcess.Id,
            ProcessName = liveProcess.ProcessName,
            ExecutablePath = path,
            StartTimeUtc = liveProcess.StartTime.ToUniversalTime()
        };
        var liveFingerprint = await new VersionFingerprintService().CreateAsync(path);
        var liveModulesDirectory = Environment.GetEnvironmentVariable("GVE_MODULES_DIRECTORY");
        using var liveRegistry = new GameAdapterRegistry(liveModulesDirectory);
        var installedAdapter = liveRegistry.FindById("game.fzzml")
                               ?? throw new InvalidOperationException(
                                   $"Installed fzzml module could not be loaded: {string.Join(" | ", liveRegistry.LoadErrors)}");
        var adapter = liveRegistry.Resolve(liveItem, liveFingerprint)
                      ?? throw new InvalidOperationException(
                          $"Installed fzzml module rejected the current build: exe={liveFingerprint.Sha256}, " +
                          $"assembly={liveFingerprint.GameAssemblySha256}, metadata={liveFingerprint.MetadataSha256}, " +
                          $"supports={installedAdapter.Supports(liveItem, liveFingerprint)}");
        var liveValue = adapter.ReadField(liveItem, "赤阳花");
        Assert(long.TryParse(liveValue.DisplayValue, out var liveCount) && liveCount >= 0,
            $"Unexpected live 赤阳花 count: {liveValue.DisplayValue}");
        Assert(adapter is IInventoryGameAdapter, "fzzml adapter does not expose inventory enumeration");
        var inventory = ((IInventoryGameAdapter)adapter).ReadInventory(liveItem);
        var liveInventoryItem = inventory.Single(item => item.FieldKey == "赤阳花");
        Assert(liveInventoryItem.Count == liveCount, "Inventory enumeration and keyed read disagree");
        Console.WriteLine($"Live adapter passed: {adapter.DisplayName}, 赤阳花={liveValue.DisplayValue}, {liveValue.Status}.");
        var characterAdapter = adapter as ICharacterAttributesGameAdapter;
        Assert(characterAdapter is not null && characterAdapter.SupportsCharacterAttributes(liveItem),
            "fzzml adapter does not expose the character-attributes editor for this build");
        var characters = characterAdapter!.ReadCharacters(liveItem);
        Assert(characters.Count > 0 && characters.All(character => character.Attributes.Count == 5),
            "Character editor did not enumerate five writable dimensions per character");
        var firstCharacter = characters[0];
        var firstAttribute = firstCharacter.Attributes[0];
        var sameValue = characterAdapter!.WriteCharacterAttribute(
            liveItem, firstCharacter.CharacterId, firstAttribute.Key, firstAttribute.RawValue);
        Assert(sameValue.Attributes.Single(attribute => attribute.Key == firstAttribute.Key).RawValue == firstAttribute.RawValue,
            "Character editor same-value main-thread validation failed");
        Console.WriteLine($"Live character editor passed: {characters.Count} characters, same-value write {firstCharacter.DisplayName}/{firstAttribute.DisplayName}={firstAttribute.RawValue}.");
        var setArgument = args.FirstOrDefault(argument => argument.StartsWith("--fzzml-set=", StringComparison.OrdinalIgnoreCase));
        if (setArgument is not null)
        {
            var requested = setArgument[(setArgument.IndexOf('=') + 1)..];
            Assert(int.TryParse(requested, out var requestedValue) && requestedValue is >= 0 and <= 1000,
                "Live fzzml smoke writes are intentionally limited to 0-1000 to avoid disrupting the player's save during automated validation");
            var written = adapter.WriteField(liveItem, "赤阳花", requested);
            Assert(written.DisplayValue == requested, $"Live adapter write mismatch: {written.DisplayValue}");
            Console.WriteLine($"Live adapter write passed: 赤阳花={written.DisplayValue}, {written.Status}.");
        }
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
            {"id":"{{id}}","version":"{{version}}","displayName":"测试专属模块","assemblyFile":"{{Path.GetFileName(assemblyPath)}}","hostApiVersion":2,"editors":[{"id":"test.inventory","displayName":"背包物品","kind":"collection","order":100},{"id":"test.characters","displayName":"人物属性","kind":"master-detail","order":200}]}
            """);
        var assembly = archive.CreateEntry(Path.GetFileName(assemblyPath));
        using var assemblyStream = assembly.Open();
        using var source = File.OpenRead(assemblyPath);
        source.CopyTo(assemblyStream);
    }
    return stream.ToArray();
}

public sealed class SmokeTestModuleAdapter : IInventoryGameAdapter, ICharacterAttributesGameAdapter
{
    public string Id => "game.test.multi-editor";
    public string DisplayName => "测试多编辑器游戏模块";
    public string Description => "仅用于宿主接口冒烟测试。";
    public IReadOnlyList<GameEditorDescriptor> Editors =>
    [
        new("test.inventory", "背包物品", GameEditorKind.Collection, 100, "测试集合编辑器"),
        new("test.characters", "人物属性", GameEditorKind.MasterDetail, 200, "测试主从编辑器", true)
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
