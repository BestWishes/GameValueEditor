using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GameValueEditor;
using GameValueEditor.Models;
using GameValueEditor.Services;
using GameValueEditor.Services.Adapters;
using GameValueEditor.ViewModels;

internal static class LayoutRegressionTests
{
    private static ApplicationReleaseTarget Target(string version, byte[] payload) => new(
        version, $"GameValueEditor-v{version}-win-x64.zip", $"https://example.invalid/{version}.zip",
        payload.Length, Convert.ToHexString(SHA256.HashData(payload)), new string('a', 40), 2, 7, 5);

    internal static async Task CheckUpdateStatesAsync(string root)
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var target = Target("0.5.0", payload);
        var checkGate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadGate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsoluteUri switch
        {
            ApplicationUpdateService.ReleaseIndexUrl => checkGate.Task,
            "https://example.invalid/0.5.0.zip" => downloadGate.Task,
            _ => Task.FromResult(Json(new[] { new { tag_name = "v0.5.0", draft = false, prerelease = false,
                assets = new[] { new { name = target.AssetName, browser_download_url = target.DownloadUrl,
                    size = target.SizeBytes, digest = "sha256:" + target.Sha256 } } } }))
        }));
        var folder = System.IO.Path.Combine(root, "layout-update-states");
        var updater = new ApplicationUpdateService(System.IO.Path.Combine(folder, "updates"), client, "0.4.9", applicationDirectory: folder);
        using var registry = new GameAdapterRegistry(System.IO.Path.Combine(folder, "modules"));
        var vm = ModuleLifecycleRegressionTests.CreateViewModel(folder,
            new GameModuleCatalogService(System.IO.Path.Combine(folder, "modules"), client), registry, updater);
        try
        {
            Assert(vm.CanCheckApplicationUpdate && !vm.CanUseApplicationUpdate, "Initial update button must be disabled, check enabled.");
            var check = vm.CheckApplicationUpdateAsync();
            Assert(!vm.CanCheckApplicationUpdate && !vm.CanUseApplicationUpdate, "Both actions must be disabled while checking.");
            checkGate.SetResult(Json(new ApplicationReleaseIndex { Releases = [target] }));
            await check;
            Assert(!vm.CanCheckApplicationUpdate && vm.CanUseApplicationUpdate, "Check cooldown must not block an available update.");
            var download = vm.DownloadApplicationUpdateAsync();
            Assert(!vm.CanCheckApplicationUpdate && !vm.CanUseApplicationUpdate && !vm.CanUseApplicationRollback,
                "Download must disable all update operations.");
            downloadGate.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
            Assert(await download && vm.IsApplicationUpdateDownloaded && !vm.CanUseApplicationUpdate && !vm.CanCheckApplicationUpdate,
                "Downloaded pending update must not be downloaded or checked again.");
            Assert(File.Exists(updater.PendingManifestPath), "Successful download did not preserve the pending update.");
        }
        finally { vm.Shutdown(); }
        foreach (var scenario in new[] { "check-failure", "latest", "download-retry" })
        {
            var scenarioFolder = System.IO.Path.Combine(root, "layout-" + scenario);
            var release = scenario == "latest" ? Target("0.4.9", payload) : target;
            var downloadAttempts = 0;
            using var scenarioClient = new HttpClient(new Handler(request => Task.FromResult(
                request.RequestUri!.AbsoluteUri == ApplicationUpdateService.ReleaseIndexUrl
                    ? scenario == "check-failure" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        : Json(new ApplicationReleaseIndex { Releases = [release] })
                    : request.RequestUri.AbsoluteUri == release.DownloadUrl
                        ? ++downloadAttempts == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }
                        : Json(new[] { new { tag_name = "v" + release.Version, draft = false, prerelease = false,
                            assets = new[] { new { name = release.AssetName, browser_download_url = release.DownloadUrl,
                                size = release.SizeBytes, digest = "sha256:" + release.Sha256 } } } }))));
            using var scenarioRegistry = new GameAdapterRegistry(System.IO.Path.Combine(scenarioFolder, "modules"));
            var scenarioUpdater = new ApplicationUpdateService(System.IO.Path.Combine(scenarioFolder, "updates"), scenarioClient, "0.4.9",
                applicationDirectory: scenarioFolder);
            var scenarioVm = ModuleLifecycleRegressionTests.CreateViewModel(scenarioFolder,
                new GameModuleCatalogService(System.IO.Path.Combine(scenarioFolder, "modules"), scenarioClient), scenarioRegistry, scenarioUpdater);
            try
            {
                if (scenario == "check-failure")
                {
                    try { await scenarioVm.CheckApplicationUpdateAsync(); throw new InvalidOperationException("Expected failed check."); }
                    catch (HttpRequestException) { }
                    Assert(!scenarioVm.CanUseApplicationUpdate && !scenarioVm.CanCheckApplicationUpdate, "Failed check exposed an update or bypassed cooldown.");
                    await Task.Delay(TimeSpan.FromSeconds(10.1));
                    Assert(scenarioVm.CanCheckApplicationUpdate && !scenarioVm.CanUseApplicationUpdate, "Check did not recover after cooldown.");
                }
                else
                {
                    await scenarioVm.CheckApplicationUpdateAsync();
                    if (scenario == "latest") Assert(!scenarioVm.CanUseApplicationUpdate, "Current version incorrectly enabled update.");
                    else
                    {
                        try { await scenarioVm.DownloadApplicationUpdateAsync(); throw new InvalidOperationException("Expected failed download."); }
                        catch (HttpRequestException) { }
                        Assert(scenarioVm.CanUseApplicationUpdate && !scenarioVm.IsApplicationUpdateDownloaded && !File.Exists(scenarioUpdater.PendingManifestPath),
                            "Failed download did not restore retry or created a pending operation.");
                        Assert(await scenarioVm.DownloadApplicationUpdateAsync() && downloadAttempts == 2 && !scenarioVm.CanUseApplicationUpdate,
                            "Retry did not complete and disable the update action.");
                    }
                }
            }
            finally { scenarioVm.Shutdown(); }
        }
    }

    internal static void CheckLayout(string[] args)
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gve-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("UI layout must not access the network.")));
        using var registry = new GameAdapterRegistry(System.IO.Path.Combine(folder, "modules"));
        var vm = ModuleLifecycleRegressionTests.CreateViewModel(folder,
            new GameModuleCatalogService(System.IO.Path.Combine(folder, "modules"), client), registry);
        var window = new MainWindow(vm);
        try
        {
            var game = new GameProfile { Name = "布局示例", IsModuleInstalled = true, IsModuleLoaded = true,
                IsLocked = true, IsPinned = true, IsConnected = true, IconSource = window.Icon,
                Versions = [new() { Fields = [new() { Name = "保留的字段" }] }] };
            vm.Games.Add(game); vm.SelectedGame = game; vm.SelectedVersion = game.Versions[0];
            vm.Games.Add(new GameProfile { Name = "这是一个很长的游戏名称，悬停可看全文", IconSource = window.Icon, IsModuleLoaded = true });
            vm.Games.Add(new GameProfile { Name = "普通游戏", IconSource = window.Icon });
            var root = (FrameworkElement)window.Content;
            var moduleButtons = ((StackPanel)window.FindName("ModuleActionButtons")).Children.OfType<Button>().ToArray();
            var appButtons = ((StackPanel)window.FindName("ApplicationActionButtons")).Children.OfType<Button>().ToArray();
            Arrange(root, 1320);
            Assert(moduleButtons.Select(button => button.Name).SequenceEqual(new[] { "ModuleRollbackButton", "ModuleCheckButton", "ModuleInstallButton",
                "ModuleUninstallButton", "ModuleCompatibilityDiagnosticsButton", "ModuleContributorsButton" }), "Module action order changed.");
            Assert(appButtons.Select(button => (string)button.Content).SequenceEqual(new[] { "回退", "查新", "更新", "官网" }), "Footer labels or order changed.");
            Assert(moduleButtons.Select(button => (string)button.Content).SequenceEqual(new[] { "回退", "查新", "更新", "卸载", "诊断", "献者" }), "Module labels changed.");
            Assert(BindingOperations.GetBinding(appButtons[1], UIElement.IsEnabledProperty)?.Path.Path == nameof(MainViewModel.CanCheckApplicationUpdate) &&
                BindingOperations.GetBinding(appButtons[2], UIElement.IsEnabledProperty)?.Path.Path == nameof(MainViewModel.CanUseApplicationUpdate),
                "Check and update availability bindings were mixed.");
            Assert(!appButtons[2].IsEnabled && appButtons[2].Visibility == Visibility.Visible, "Unavailable update must remain visible and disabled.");
            appButtons[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // Must not fall back to a network check.
            var themes = new ThemeService();
            foreach (var width in new[] { 1080, 1320, 1640 })
            {
                Arrange(root, width);
                var baselineModule = Positions(moduleButtons, root);
                var baselineApp = Positions(appButtons, root);
                var cancel = (Button)window.FindName("DownloadCancelButton");
                Assert(cancel.Width == 64 && cancel.Visibility == Visibility.Hidden, "Idle cancel slot must be fixed and hidden, not collapsed.");
                Assert(cancel.ActualHeight == appButtons[0].ActualHeight, "Cancel must use the same footer action height.");
                foreach (var visibility in new[] { Visibility.Visible, Visibility.Hidden })
                {
                    cancel.Visibility = visibility;
                    Arrange(root, width);
                    Assert(Positions(appButtons, root).SequenceEqual(baselineApp), "Cancel visibility moved the existing footer buttons.");
                }
                CheckSpacing(moduleButtons, root, 64, 8);
                CheckSpacing(appButtons, root, 52, 12);
                foreach (var state in new[] { "initial", "checking", "available", "latest", "failed", "downloading", "pending" })
                {
                    SetField(vm, "_applicationUpdateResult", state == "available"
                        ? new ApplicationUpdateCheckResult("0.4.9", Target("12.3.4", [1]), Target("0.4.7", [1])) : null);
                    SetField(vm, "_isApplicationUpdateBusy", state is "checking" or "downloading");
                    SetField(vm, "_applicationUpdateDownloaded", state == "pending");
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.ApplicationUpdateStatusText))!.SetValue(vm,
                        state + new string('长', 100));
                    typeof(MainViewModel).GetMethod("NotifyApplicationUpdateState", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null);
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.StatusText))!.SetValue(vm, new string('状', 200));
                    Arrange(root, width);
                    Assert(Positions(moduleButtons, root).SequenceEqual(baselineModule) && Positions(appButtons, root).SequenceEqual(baselineApp),
                        $"Action buttons moved at width {width}, state {state}.");
                    Assert(appButtons.All(button => button.Visibility == Visibility.Visible), "A state hid a footer action.");
                }
                foreach (var availability in new[] { GameModuleAvailability.Available, GameModuleAvailability.UpdateAvailable })
                {
                    SetField(vm, "_moduleCheckResult", new GameModuleCheckResult(availability, null, null, "", IsExactBuildMatch: false));
                    typeof(MainViewModel).GetMethod("NotifyModuleControls", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null);
                    Arrange(root, width);
                    Assert((string)moduleButtons[2].Content == (availability == GameModuleAvailability.Available ? "下载" : "更新") &&
                        vm.ModuleInstallToolTip.Contains("后验证", StringComparison.Ordinal), "Download or post-validation hint was lost.");
                    Assert(Positions(moduleButtons, root).SequenceEqual(baselineModule), "Download/update text changed button positions.");
                }
                SetField(vm, "_applicationUpdateResult", new ApplicationUpdateCheckResult("0.4.9", null, Target("0.4.8", [1])));
                SetField(vm, "_isApplicationUpdateBusy", false);
                SetField(vm, "_applicationUpdateDownloaded", false);
                typeof(MainViewModel).GetProperty(nameof(MainViewModel.ApplicationUpdateStatusText))!.SetValue(vm, "已最新 · 可回退 v0.4.8");
                typeof(MainViewModel).GetProperty(nameof(MainViewModel.StatusText))!.SetValue(vm, "布局预览：单行游戏库，四个固定状态图标，页脚间距 12 DIP。");
                typeof(MainViewModel).GetMethod("NotifyApplicationUpdateState", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null);
                foreach (var theme in Enum.GetValues<ApplicationTheme>())
                {
                    themes.Apply(theme); Arrange(root, width);
                    CheckLibraryEntry(root, game, width, theme);
                    if (args.Contains("--render-layout", StringComparer.OrdinalIgnoreCase))
                    {
                        Render(root, theme, width);
                        cancel.Visibility = Visibility.Visible; cancel.IsEnabled = true; Arrange(root, width);
                        Assert(Positions(appButtons, root).SequenceEqual(baselineApp), "Visible themed cancel moved the footer actions.");
                        Render(root, theme, width, "-cancel");
                        cancel.Visibility = Visibility.Hidden; Arrange(root, width);
                    }
                }
            }
            foreach (var (file, hash) in new[] {
                ("Locked", "4419E2269EB02C99FDB0737AA9BFCA4EB1771D1E0FA2571EEA819D14807A517E"),
                ("Topmost", "B7754C2A88454B9FD9906943645F64678C92C1C5D2B8299F338420F30968C44F"),
                ("Connected", "78B18183AF72FE9CF1F8BCC10C048DCDDE73BE66AAC67A63488393ACC482D51F"),
                ("DedicatedModuleLoaded", "F837EFD14BD7EF93951A24AA9615FE858D078066343531B22009211718E14FA3") })
            {
                var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/GameValueEditor;component/Assets/GameValueEditor{file}.png"))!;
                using (resource.Stream) Assert(Convert.ToHexString(SHA256.HashData(resource.Stream)) == hash, $"Embedded {file} differs from the final user image.");
            }
            using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(game));
            Assert(!serialized.RootElement.TryGetProperty(nameof(GameProfile.IsModuleLoaded), out _) &&
                serialized.RootElement.GetProperty(nameof(GameProfile.Versions))[0].GetProperty(nameof(GameVersionProfile.Fields)).GetArrayLength() == 1,
                "Loaded state was persisted or library version/field data was removed.");
            Console.WriteLine("Layout regressions passed: single-line library, 16 status combinations, four final themed icons, 8/12 DIP action gaps and stable commands.");
        }
        finally
        {
            window.Close();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private static void CheckLibraryEntry(FrameworkElement root, GameProfile game, int width, ApplicationTheme theme)
    {
        var entry = Descendants(root).OfType<Grid>().Single(item => item.Name == "LibraryEntryGrid" && ReferenceEquals(item.DataContext, game));
        var avatar = entry.Children.OfType<Border>().Single();
        var name = entry.Children.OfType<TextBlock>().Single();
        var icons = entry.Children.OfType<Rectangle>().OrderBy(Grid.GetColumn).ToArray();
        var library = Descendants(root).OfType<ListBox>().Single(item => item.Name == "GameLibraryList");
        Assert(ScrollViewer.GetHorizontalScrollBarVisibility(library) == ScrollBarVisibility.Disabled && entry.ActualWidth <= library.ActualWidth,
            "Library entry escaped its bounded sidebar viewport.");
        Assert(entry.RowDefinitions.Count == 0 && entry.ColumnDefinitions.Count == 6 &&
            entry.ColumnDefinitions.Skip(2).Select(column => column.ActualWidth).SequenceEqual(new double[] { 24, 24, 24, 32 }),
            "Library is not one row with four fixed status slots.");
        Assert(icons.Select(icon => icon.Name).SequenceEqual(new[] { "TopmostStatusIcon", "LockedStatusIcon", "DedicatedModuleLoadedIcon", "ConnectedStatusIcon" }),
            "Library status order changed.");
        Assert(avatar.ActualWidth == 38 && avatar.ActualHeight == 38 && avatar.ToolTip is null &&
            ((Image)avatar.Child).ToolTip is null && ((Image)avatar.Child).Source is not null && entry.ToolTip is null,
            "Avatar was removed, resized or received a tooltip.");
        Assert(name.Text == game.Name && name.TextTrimming == TextTrimming.CharacterEllipsis && name.TextWrapping == TextWrapping.NoWrap &&
            (string)name.ToolTip == game.Name && name.ActualWidth > 0, "Name lost its single-line full-name tooltip.");
        var tips = new[] { "已置顶", "已锁定", "已加载本地模块", "已连接到游戏" };
        var sizes = new[] { new Size(14, 16), new Size(13, 15), new Size(14, 16), new Size(24, 28) };
        var statusBits = new[] { 2, 1, 4, 8 };
        for (var index = 0; index < icons.Length; index++)
        {
            var icon = icons[index];
            Assert(icon.ActualWidth == sizes[index].Width && icon.ActualHeight == sizes[index].Height &&
                icon.Fill is SolidColorBrush fill && fill.Color == ((SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color &&
                (string)icon.ToolTip == tips[index] && AutomationProperties.GetName(icon) == tips[index] && !icon.Focusable,
                $"Icon size, theme, tooltip or accessibility changed: {theme}/{icon.Name}.");
            Assert(icon.OpacityMask is ImageBrush mask && mask.ViewboxUnits == BrushMappingMode.RelativeToBoundingBox && mask.Viewbox.Width < 0.6,
                "Icon mask retained oversized transparent padding.");
            var point = icon.TranslatePoint(new Point(), entry);
            Assert(Math.Abs(point.Y + icon.ActualHeight / 2 - entry.ActualHeight / 2) < 0.01 && point.X + icon.ActualWidth <= entry.ActualWidth,
                "Status icon is misaligned or exceeds its entry.");
        }
        var baseline = icons.Select(icon => icon.TranslatePoint(new Point(), root)).ToArray();
        var originalName = game.Name;
        foreach (var text in new[] { originalName, new string('长', 80) })
        {
            game.Name = text;
            for (var bits = 0; bits < 16; bits++)
            {
                game.IsLocked = (bits & 1) != 0; game.IsPinned = (bits & 2) != 0;
                game.IsModuleLoaded = (bits & 4) != 0; game.IsConnected = (bits & 8) != 0;
                Arrange(root, width);
                var current = icons.Select(icon => icon.TranslatePoint(new Point(), root)).ToArray();
                Assert(current.SequenceEqual(baseline), $"Status or long name moved a library icon: width={width}, theme={theme}, bits={bits}, nameLength={text.Length}, baseline={string.Join(';', baseline)}, current={string.Join(';', current)}.");
                for (var index = 0; index < icons.Length; index++)
                    Assert(icons[index].Visibility == ((bits & statusBits[index]) != 0 ? Visibility.Visible : Visibility.Hidden),
                        "Missing status did not leave its fixed hidden slot.");
                Assert(name.Text == text && (string)name.ToolTip == text && name.TranslatePoint(new Point(name.ActualWidth, 0), entry).X <=
                    icons[0].TranslatePoint(new Point(), entry).X, "Long name collided with status icons.");
            }
        }
        game.Name = originalName;
        game.IsLocked = game.IsPinned = game.IsModuleLoaded = game.IsConnected = true;
        Arrange(root, width);
    }

    private static void Arrange(FrameworkElement root, int width)
    {
        root.Measure(new Size(width, 780)); root.Arrange(new Rect(0, 0, width, 780)); root.UpdateLayout();
    }
    private static Point[] Positions(Button[] buttons, FrameworkElement root) => buttons.Select(button => button.TranslatePoint(new Point(), root)).ToArray();
    private static void CheckSpacing(Button[] buttons, FrameworkElement root, int width, int gap)
    {
        var positions = Positions(buttons, root);
        for (var index = 0; index < buttons.Length; index++)
        {
            Assert(buttons[index].ActualWidth == width && positions[index].X >= 0 && positions[index].X + width <= root.ActualWidth,
                "Button width changed or exceeded the viewport.");
            if (index > 0) Assert(Math.Abs(positions[index].X - positions[index - 1].X - width - gap) < 0.01, $"Button edge spacing is not {gap} DIP.");
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void SetField(MainViewModel vm, string name, object? value) =>
        typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Render(FrameworkElement root, ApplicationTheme theme, int width, string suffix = "")
    {
        var bitmap = new RenderTargetBitmap(width, 780, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var folder = System.IO.Path.GetFullPath("artifacts/layout-review"); Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, $"layout-{theme}-{width}{suffix}.png");
        using var file = File.Create(path); encoder.Save(file);
        Console.WriteLine($"Rendered layout: {path}");
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }
}
