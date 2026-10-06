using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using GameValueEditor.Models;
using GameValueEditor.Services;
using GameValueEditor.Services.Adapters;
using GameValueEditor.ViewModels;

internal static class DownloadFeedbackRegressionTests
{
    internal static async Task RunAsync(string root)
    {
        var folder = Path.Combine(root, "download-feedback");
        Directory.CreateDirectory(folder);
        var samples = new List<DownloadProgressSnapshot>();
        using (var client = new HttpClient(new Handler(async (_, token) =>
               {
                   await Task.Delay(1200, token);
                   return new(HttpStatusCode.OK) { Content = new StreamContent(new SlowStream(false)) };
               })))
        {
            await HttpDownloadService.DownloadToFileAsync(client, "https://example.invalid/slow", Path.Combine(folder, "slow.bin"), 4,
                new InlineProgress(samples.Add));
        }
        Assert(samples.Any(sample => sample.Phase == DownloadPhase.Connecting && sample.WaitingSeconds >= 1), "Connecting heartbeat missing.");
        Assert(samples.Any(sample => sample.Phase == DownloadPhase.Waiting && sample.WaitingSeconds >= 1), "Waiting heartbeat missing.");
        Assert(samples.Any(sample => sample.BytesPerSecond is > 0 && sample.DisplayText.Contains("KB/s")), "Transfer speed missing.");
        Assert(samples[^1].Percentage == 100 && samples[^1].Phase == DownloadPhase.Verifying, "Transfer did not close at verification.");

        using (var client = new HttpClient(new Handler(async (_, token) =>
               {
                   await Task.Delay(Timeout.Infinite, token);
                   return new(HttpStatusCode.OK);
               })))
        using (var cancellation = new CancellationTokenSource(100))
        {
            var path = Path.Combine(folder, "headers.download");
            try { await HttpDownloadService.DownloadToFileAsync(client, "https://example.invalid/headers", path, 4, cancellationToken: cancellation.Token); throw new Exception("Header cancellation ignored."); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Assert(!File.Exists(path), "Header cancellation created a partial file.");
        }

        foreach (var command in new[] { "application-update", "application-rollback", "module-install", "module-rollback" })
        {
            var scenario = Path.Combine(folder, command);
            using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new SlowStream(true)) })));
            var catalog = new GameModuleCatalogService(Path.Combine(scenario, "modules"), client);
            var updater = new ApplicationUpdateService(Path.Combine(scenario, "updates"), client, "0.4.9", applicationDirectory: scenario);
            using var registry = new GameAdapterRegistry(Path.Combine(scenario, "modules"));
            var vm = ModuleLifecycleRegressionTests.CreateViewModel(scenario, catalog, registry, updater);
            string? installedBefore = null;
            try
            {
                var targetVersion = command == "application-rollback" ? "0.4.8" : "0.5.0";
                var target = new ApplicationReleaseTarget(targetVersion, $"GameValueEditor-v{targetVersion}-win-x64.zip",
                    "https://example.invalid/app.zip", 4, Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])), new string('a', 40), 2, 7, 5);
                SetField(vm, "_applicationUpdateResult", new ApplicationUpdateCheckResult("0.4.9", target, target));
                if (command.StartsWith("module", StringComparison.Ordinal))
                {
                    var game = new GameProfile { Name = "cancel fixture", ModuleId = "game.cancel", ProcessName = "fixture", Versions = [new() { ExecutableSha256 = "A" }] };
                    vm.Games.Add(game); vm.SelectedGame = game; vm.SelectedVersion = game.Versions[0];
                    SetField(vm, "_moduleCheckContext", typeof(MainViewModel).GetMethod("CaptureModuleContext", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null));
                    var module = new GameModuleCatalogEntry { Id = "game.cancel", Version = command == "module-rollback" ? "1.2.2" : "1.2.4", DisplayName = "cancel fixture",
                        HostApiVersion = 7, MinimumHostVersion = "0.4.4", DownloadUrl = "https://example.invalid/module.zip",
                        SizeBytes = 4, Sha256 = target.Sha256 };
                    SetField(vm, "_moduleCheckResult", new GameModuleCheckResult(GameModuleAvailability.UpdateAvailable, module, null, "", true, module));
                    Directory.CreateDirectory(Path.Combine(scenario, "modules"));
                    installedBefore = JsonSerializer.Serialize(new InstalledModuleDocument { Modules = [new("game.cancel", "1.2.3", DateTime.UtcNow)] });
                    File.WriteAllText(Path.Combine(scenario, "modules", "installed.json"), installedBefore);
                }
                Task<bool> operation = command switch
                {
                    "application-update" => vm.DownloadApplicationUpdateAsync(),
                    "application-rollback" => vm.DownloadApplicationRollbackAsync(),
                    "module-install" => vm.InstallAvailableGameModuleAsync(),
                    _ => vm.RollbackCurrentGameModuleAsync()
                };
                Assert(vm.IsDownloadActive && vm.CanCancelDownload && vm.DownloadCancelVisibility == Visibility.Visible, "Download did not expose cancellation.");
                Assert(!vm.CanUseApplicationUpdate && !vm.CanUseApplicationRollback && !vm.CanInstallGameModule && !vm.CanRollbackGameModule,
                    "Concurrent downloads remained enabled.");
                await Task.Delay(100);
                vm.CancelDownload();
                Assert(!await operation && !vm.IsDownloadActive && !vm.CanCancelDownload && vm.DownloadCancelVisibility == Visibility.Hidden,
                    "Cancellation did not finish cleanly or still requested restart.");
                Assert(!vm.IsApplicationUpdateDownloaded && !File.Exists(updater.PendingManifestPath), "Cancellation created a pending update.");
                Assert(!vm.ModuleRestartRequired, "Canceled module operation requested a restart.");
                Assert(!Directory.Exists(Path.Combine(scenario, "updates", targetVersion)) || Directory.GetFiles(Path.Combine(scenario, "updates", targetVersion)).Length == 0,
                    "Canceled application transfer left a partial archive.");
                Assert(!Directory.Exists(Path.Combine(scenario, "modules", "downloads")) || Directory.GetFiles(Path.Combine(scenario, "modules", "downloads")).Length == 0,
                    "Canceled module transfer left a partial archive.");
                if (installedBefore is not null) Assert(File.ReadAllText(Path.Combine(scenario, "modules", "installed.json")) == installedBefore,
                    "Cancellation changed the installed module record.");
                var finalStatus = vm.StatusText;
                await Task.Delay(20);
                Assert(vm.StatusText == finalStatus && vm.StatusText.Contains("已取消"), "Late progress replaced cancellation status.");
            }
            finally { vm.Shutdown(); }
        }

        using (var client = new HttpClient(new Handler((_, _) => throw new Exception("Gate test must not access the network."))))
        using (var registry = new GameAdapterRegistry(Path.Combine(folder, "gate-modules")))
        {
            var vm = ModuleLifecycleRegressionTests.CreateViewModel(Path.Combine(folder, "gate"), new GameModuleCatalogService(Path.Combine(folder, "gate-modules"), client), registry);
            var begin = typeof(MainViewModel).GetMethod("BeginDownload", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var end = typeof(MainViewModel).GetMethod("EndDownload", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var callbackCount = 0;
            var operation = begin.Invoke(vm, [new Action<DownloadProgressSnapshot>(_ => callbackCount++)])!;
            ((IProgress<DownloadProgressSnapshot>)operation).Report(new(4, 4) { Phase = DownloadPhase.Verifying });
            Assert(!vm.CanCancelDownload, "Cancellation gate closed asynchronously instead of before verification.");
            vm.CancelDownload();
            Assert(!(bool)operation.GetType().GetProperty("IsCanceled")!.GetValue(operation)!, "UI canceled an installation-stage operation.");
            end.Invoke(vm, [operation]);
            await Task.Delay(20);
            Assert(callbackCount == 0, "Queued progress escaped the ended operation reference guard.");
            var canceled = begin.Invoke(vm, [new Action<DownloadProgressSnapshot>(_ => { })])!;
            vm.CancelDownload();
            try { ((IProgress<DownloadProgressSnapshot>)canceled).Report(new(4, 4) { Phase = DownloadPhase.Verifying }); throw new Exception("A canceled transfer entered verification."); }
            catch (OperationCanceledException) { }
            end.Invoke(vm, [canceled]);
            vm.Shutdown();
        }
        Console.WriteLine("Download feedback regressions passed: waiting/speed, header/body cancellation, all four commands, unchanged records and synchronous install gate.");
    }

    private static void SetField(object target, string name, object? value) => typeof(MainViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class InlineProgress(Action<DownloadProgressSnapshot> action) : IProgress<DownloadProgressSnapshot>
    { public void Report(DownloadProgressSnapshot value) => action(value); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken); }
    private sealed class SlowStream(bool stall) : MemoryStream(new byte[] { 1, 2, 3, 4 })
    {
        private bool _read;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stall && _read) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (!stall && !_read) await Task.Delay(1200, cancellationToken);
            _read = true;
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
