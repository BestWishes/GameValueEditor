using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameValueEditor;
using GameValueEditor.Dialogs;
using GameValueEditor.Models;
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
        var hookResult = speedService.Accelerate(speedTarget.Id, 4);
        Assert(hookResult.PatchedImportCount > 0, "No clock imports were patched");
        var acceleratedLine = await speedTarget.StandardOutput.ReadLineAsync();
        Assert(long.TryParse(acceleratedLine, out var acceleratedElapsed),
            $"Speed target returned an invalid sample: {acceleratedLine} ms");
        if (!string.IsNullOrWhiteSpace(nativeSpeedTargetPath))
            Assert(acceleratedElapsed >= 2500, $"Native speed target did not accelerate: {acceleratedLine} ms");
        speedService.DetachSafely();
        Assert(!speedService.HasHooks && speedService.Multiplier == 1,
            "Safe detach kept editor-owned speed state alive");
        var normalizedLine = await speedTarget.StandardOutput.ReadLineAsync();
        Assert(long.TryParse(normalizedLine, out var normalizedElapsed), $"Invalid normalized sample: {normalizedLine}");
        var normalizedDelta = normalizedElapsed - acceleratedElapsed;
        if (!string.IsNullOrWhiteSpace(nativeSpeedTargetPath))
            Assert(normalizedDelta is >= 500 and <= 1800,
                $"Closing-time safe detach did not preserve continuous normal speed: delta={normalizedDelta} ms");

        var reattached = speedService.Accelerate(speedTarget.Id, 3);
        Assert(reattached.PatchedImportCount > 0, "Could not reattach to persistent normal-speed wrappers");
        var reacceleratedLine = await speedTarget.StandardOutput.ReadLineAsync();
        Assert(long.TryParse(reacceleratedLine, out var reacceleratedElapsed),
            $"Invalid reaccelerated sample: {reacceleratedLine}");
        var reacceleratedDelta = reacceleratedElapsed - normalizedElapsed;
        if (!string.IsNullOrWhiteSpace(nativeSpeedTargetPath))
            Assert(reacceleratedDelta is >= 1800 and <= 4200,
                $"Reattaching after editor close lost clock continuity: delta={reacceleratedDelta} ms");

        var changedMultiplier = speedService.Accelerate(speedTarget.Id, 2);
        Assert(changedMultiplier.PatchedImportCount > 0, "Changing an active multiplier did not reattach speed hooks");
        var changedMultiplierLine = await speedTarget.StandardOutput.ReadLineAsync();
        Assert(long.TryParse(changedMultiplierLine, out var changedMultiplierElapsed),
            $"Invalid changed-multiplier sample: {changedMultiplierLine}");
        var changedMultiplierDelta = changedMultiplierElapsed - reacceleratedElapsed;
        if (!string.IsNullOrWhiteSpace(nativeSpeedTargetPath))
            Assert(changedMultiplierDelta is >= 1200 and <= 3200,
                $"Changing an active multiplier lost clock continuity: delta={changedMultiplierDelta} ms");
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
        for (var transition = 0; transition < 12; transition++)
        {
            await Task.Delay(15);
            if (transition % 3 == 2)
                speedService.Normalize();
            else
                speedService.Accelerate(speedStressTarget.Id, transition % 2 == 0 ? 2 : 5);
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
    Assert(cooldownViewModel.CanAccelerate && cooldownViewModel.CanRestoreSpeed,
        "Speed buttons did not recover after the interaction cooldown");

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
        Assert(restored.SchemaVersion == 4, "Library schema version was not upgraded");
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

            if (args.Contains("--render-ui", StringComparer.OrdinalIgnoreCase))
            {
                new ThemeService().Apply(ApplicationTheme.Dark);
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
                var root = (FrameworkElement)mainWindow.Content;
                root.Measure(new Size(1320, 820));
                root.Arrange(new Rect(0, 0, 1320, 820));
                root.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1320, 820, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var snapshotPath = Path.GetFullPath(Path.Combine("artifacts", "ui-dark-smoke.png"));
                Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
                using (var snapshot = File.Create(snapshotPath)) encoder.Save(snapshot);
                mainWindow.Close();
                Console.WriteLine($"Rendered dark UI: {snapshotPath}");
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
        var adapter = new GameAdapterRegistry().Resolve(liveItem, liveFingerprint)
                      ?? throw new InvalidOperationException("Supported fzzml adapter was not detected");
        var liveValue = adapter.ReadField(liveItem, "赤阳花");
        Assert(long.TryParse(liveValue.DisplayValue, out var liveCount) && liveCount >= 0,
            $"Unexpected live 赤阳花 count: {liveValue.DisplayValue}");
        Assert(adapter is IInventoryGameAdapter, "fzzml adapter does not expose inventory enumeration");
        var inventory = ((IInventoryGameAdapter)adapter).ReadInventory(liveItem);
        var liveInventoryItem = inventory.Single(item => item.FieldKey == "赤阳花");
        Assert(liveInventoryItem.Count == liveCount, "Inventory enumeration and keyed read disagree");
        Console.WriteLine($"Live adapter passed: {adapter.DisplayName}, 赤阳花={liveValue.DisplayValue}, {liveValue.Status}.");
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
