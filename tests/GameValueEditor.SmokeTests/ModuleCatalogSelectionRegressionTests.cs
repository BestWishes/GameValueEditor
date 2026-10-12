using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using GameValueEditor.Models;
using GameValueEditor.Services.Adapters;

internal static class ModuleCatalogSelectionRegressionTests
{
    internal static async Task RunAsync(string root)
    {
        var checks = 0;
        void Check(bool valid, string message) { checks++; if (!valid) throw new InvalidOperationException(message); }
        GameModuleReleaseEntry Release(string version, bool dynamic = true, string exe = "NEW", string minimum = "0.5.2") => new()
        {
            Version = version, HostApiVersion = 8, MinimumHostVersion = minimum,
            SupportsUnlistedBuildValidation = dynamic,
            CompatibleBuilds = [new() { ExecutableSha256 = exe }],
            DownloadUrl = "https://example.invalid/fixture.zip", Sha256 = new('A', 64), SizeBytes = 1
        };
        var entry = new GameModuleCatalogEntry
        {
            Id = "game.selection-fixture", Version = "0.1.2", ProcessNames = ["SelectionGame"],
            Releases = [Release("0.1.3", minimum: "0.5.4"), Release("0.1.2"),
                Release("0.0.7", exe: "OLD"), Release("0.0.6", false, "OLD")]
        };
        using var client = new HttpClient(new Handler(() => JsonSerializer.Serialize(
            new GameModuleCatalog { SchemaVersion = 5, Modules = [entry] })));
        var service = new GameModuleCatalogService(root, client, currentHostVersion: "0.5.3");
        VersionFingerprint Build(string exe) => new("fixture", "", "", exe, 1, "x64", "", "", "");
        var result = await service.CheckAsync("SelectionGame", Build("OLD"));
        Check(result.RemoteModule?.Version == "0.1.2" && !result.IsExactBuildMatch,
            "An exact historical package hid the newest host-compatible runtime-resolving package.");
        Check(result.InstalledModule is null && result.Availability == GameModuleAvailability.Available,
            "Checking an uninstalled module changed registration.");
        foreach (var localVersion in new[] { "0.0.6", "0.0.7", "0.0.8", "0.0.9", "0.1.0", "0.1.1" })
        {
            service.RestoreRegistration(new(entry.Id, localVersion, DateTime.UtcNow));
            foreach (var exe in new[] { "OLD", "SMALL-UPDATE" })
            {
                result = await service.CheckAsync("SelectionGame", Build(exe));
                Check(result.Availability == GameModuleAvailability.UpdateAvailable && result.RemoteModule?.Version == "0.1.2",
                    $"Local delivery {localVersion} did not receive the newer formal package on {exe}.");
                Check(service.FindInstalled(entry.Id)?.Version == localVersion,
                    "A check automatically installed or rolled back a module.");
            }
        }
        service.RestoreRegistration(new(entry.Id, "0.1.2", DateTime.UtcNow));
        result = await service.CheckAsync("SelectionGame", Build("SMALL-UPDATE"));
        Check(result.Availability == GameModuleAvailability.Current && result.RollbackModule?.Version == "0.0.7" && !result.IsExactBuildMatch,
            "An unlisted small update lost its runtime-resolving manual rollback candidate.");
        result = await service.CheckAsync("SelectionGame", Build("NEW"));
        Check(result.IsExactBuildMatch && result.RollbackModule?.Version == "0.0.7",
            "The selected package lost its exact-match metadata or compatible rollback.");
        entry.Releases.RemoveAt(2); // Only the strict old package remains below the installed version.
        result = await service.CheckAsync("SelectionGame", Build("SMALL-UPDATE"));
        Check(result.RollbackModule is null, "An unknown strict package became a rollback candidate.");
        result = await service.CheckAsync("SelectionGame", Build("OLD"));
        Check(result.RollbackModule?.Version == "0.0.6", "A proven strict old package was excluded from manual rollback.");
        entry.Releases[1].SupportsUnlistedBuildValidation = false;
        result = await service.CheckAsync("SelectionGame", Build("SMALL-UPDATE"));
        Check(result.Availability == GameModuleAvailability.NotAvailable, "Unknown builds bypassed every module capability declaration.");
        result = await service.CheckAsync("AnotherGame", Build("NEW"));
        Check(result.Availability == GameModuleAvailability.NotAvailable, "Candidates leaked across game names.");
        Console.WriteLine($"Module catalog selection: {checks} checks passed; isolated HTTP/registration fixtures, no download or game operation.");
    }

    private sealed class Handler(Func<string> json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json()) });
    }
}
