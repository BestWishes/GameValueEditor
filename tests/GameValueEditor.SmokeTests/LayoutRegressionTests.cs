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
        var target = Target("0.4.9", payload);
        var checkGate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadGate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsoluteUri switch
        {
            ApplicationUpdateService.ReleaseIndexUrl => checkGate.Task,
            "https://example.invalid/0.4.9.zip" => downloadGate.Task,
            _ => Task.FromResult(Json(new[] { new { tag_name = "v0.4.9", draft = false, prerelease = false,
                assets = new[] { new { name = target.AssetName, browser_download_url = target.DownloadUrl,
                    size = target.SizeBytes, digest = "sha256:" + target.Sha256 } } } }))
        }));
        var folder = System.IO.Path.Combine(root, "layout-update-states");
        var updater = new ApplicationUpdateService(System.IO.Path.Combine(folder, "updates"), client, "0.4.8", applicationDirectory: folder);
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
            var release = scenario == "latest" ? Target("0.4.8", payload) : target;
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
            var scenarioUpdater = new ApplicationUpdateService(System.IO.Path.Combine(scenarioFolder, "updates"), scenarioClient, "0.4.8",
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
            var game = new GameProfile { Name = "布局测试 · 已安装模块", IsModuleInstalled = true, Versions = [new()] };
            vm.Games.Add(game); vm.SelectedGame = game; vm.SelectedVersion = game.Versions[0];
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
                CheckSpacing(moduleButtons, root, 64);
                CheckSpacing(appButtons, root, 52);
                foreach (var state in new[] { "initial", "checking", "available", "latest", "failed", "downloading", "pending" })
                {
                    SetField(vm, "_applicationUpdateResult", state == "available"
                        ? new ApplicationUpdateCheckResult("0.4.8", Target("12.3.4", [1]), Target("0.4.6", [1])) : null);
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
                SetField(vm, "_applicationUpdateResult", new ApplicationUpdateCheckResult("0.4.8", null, Target("0.4.7", [1])));
                SetField(vm, "_isApplicationUpdateBusy", false);
                SetField(vm, "_applicationUpdateDownloaded", false);
                typeof(MainViewModel).GetProperty(nameof(MainViewModel.ApplicationUpdateStatusText))!.SetValue(vm, "已最新 · 可回退 v0.4.7");
                typeof(MainViewModel).GetProperty(nameof(MainViewModel.StatusText))!.SetValue(vm, "布局预览：模块图标随主题变色，操作按钮位置固定。");
                typeof(MainViewModel).GetMethod("NotifyApplicationUpdateState", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null);
                foreach (var theme in Enum.GetValues<ApplicationTheme>())
                {
                    themes.Apply(theme); Arrange(root, width);
                    var icon = Descendants(root).OfType<Rectangle>().Single(item => item.Name == "DedicatedModuleInstalledIcon");
                    Assert(icon.Visibility == Visibility.Visible && icon.ActualWidth == 20 && icon.ActualHeight == 16 &&
                        icon.Fill is SolidColorBrush fill && fill.Color == ((SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color,
                        $"Installed icon does not follow {theme}.");
                    Assert(icon.OpacityMask is ImageBrush mask && mask.ViewboxUnits == BrushMappingMode.RelativeToBoundingBox && mask.Viewbox.Width < 0.5,
                        "Icon mask did not exclude oversized transparent padding.");
                    Assert(AutomationProperties.GetName(icon) == "已装专属模块", "Icon lost its accessible name.");
                    var summary = (TextBlock)((StackPanel)icon.Parent).Children[0];
                    Assert(Math.Abs(icon.TranslatePoint(new Point(), root).X - summary.TranslatePoint(new Point(), root).X - summary.ActualWidth - 16) < 0.01,
                        "Installed icon did not retain the requested 16 DIP rightward spacing.");
                    game.IsModuleInstalled = false; Arrange(root, width);
                    Assert(icon.Visibility == Visibility.Collapsed, "Uninstalled game shows an installed-module icon.");
                    game.IsModuleInstalled = true; Arrange(root, width);
                    if (args.Contains("--render-layout", StringComparer.OrdinalIgnoreCase)) Render(root, theme, width);
                }
            }
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/GameValueEditor;component/Assets/GameValueEditorDedicatedModuleLoaded.png"))!;
            using (resource.Stream)
                Assert(Convert.ToHexString(SHA256.HashData(resource.Stream)) == "B9AF9ED8EA56547B69A342BF98B46585CA7AEE30F5DCF8CDB70743F30A981B2C",
                    "Embedded icon differs from the original user image.");
            Console.WriteLine("Layout regressions passed: fixed actions, 8 DIP gaps, five themed icon masks and separate update commands.");
        }
        finally
        {
            window.Close();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private static void Arrange(FrameworkElement root, int width)
    {
        root.Measure(new Size(width, 780)); root.Arrange(new Rect(0, 0, width, 780)); root.UpdateLayout();
    }
    private static Point[] Positions(Button[] buttons, FrameworkElement root) => buttons.Select(button => button.TranslatePoint(new Point(), root)).ToArray();
    private static void CheckSpacing(Button[] buttons, FrameworkElement root, int width)
    {
        var positions = Positions(buttons, root);
        for (var index = 0; index < buttons.Length; index++)
        {
            Assert(buttons[index].ActualWidth == width && positions[index].X >= 0 && positions[index].X + width <= root.ActualWidth,
                "Button width changed or exceeded the viewport.");
            if (index > 0) Assert(Math.Abs(positions[index].X - positions[index - 1].X - width - 8) < 0.01, "Button edge spacing is not 8 DIP.");
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
    private static void Render(FrameworkElement root, ApplicationTheme theme, int width)
    {
        var bitmap = new RenderTargetBitmap(width, 780, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var folder = System.IO.Path.GetFullPath("artifacts/layout-review"); Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, $"layout-{theme}-{width}.png");
        using var file = File.Create(path); encoder.Save(file);
        Console.WriteLine($"Rendered layout: {path}");
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }
}
