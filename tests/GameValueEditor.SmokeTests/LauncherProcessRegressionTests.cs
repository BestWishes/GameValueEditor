using System.IO;
using System.Text.Json;
using GameValueEditor.Models;
using GameValueEditor.Services;

internal static class LauncherProcessRegressionTests
{
    internal static void Run()
    {
        var checks = 0;
        void Check(bool result, string message) { checks++; if (!result) throw new InvalidOperationException(message); }
        var service = new ProcessService();
        var start = DateTime.UtcNow.AddMinutes(-2);
        var source = Item(10, 1, @"C:\download\Adventure\Start.exe", GameRuntimeKind.Native, GameProcessRole.Main);
        var path = Path.Combine(Path.GetTempPath(), "gve-extracted-fixture", "app", "AdventureRuntime.exe");
        var runtime = Item(20, 10, path, GameRuntimeKind.Electron, GameProcessRole.Main, window: "Game");
        var renderer = Item(21, 20, path, GameRuntimeKind.Electron, GameProcessRole.Renderer);
        var gpu = Item(22, 20, path, GameRuntimeKind.Electron, GameProcessRole.Gpu);
        var network = Item(23, 20, path, GameRuntimeKind.Electron, GameProcessRole.Network);
        var audio = Item(24, 20, path, GameRuntimeKind.Electron, GameProcessRole.Audio);
        var other = Item(30, 2, path, GameRuntimeKind.Electron, GameProcessRole.Main);
        var otherRenderer = Item(31, 30, path, GameRuntimeKind.Electron, GameProcessRole.Renderer);
        var processes = new[] { source, runtime, renderer, gpu, network, audio, other, otherRenderer };
        foreach (var seed in processes.Take(6))
        {
            var group = service.ResolveLogicalGame(seed, processes);
            Check(group.RootProcess == runtime, "Launcher/child did not resolve the runtime lifetime.");
            Check(group.DataProcess == renderer, "Launcher/child did not route to the renderer.");
            Check(group.Members.Count == 5 && !group.Members.Contains(source) && !group.Members.Contains(other), "Launcher/other instance leaked into data members.");
            Check(group.IsSameInstance(service.ResolveLogicalGame(renderer, processes)), "Selecting the launcher changed session identity.");
            Check(group.SeedProcess == seed && group.RuntimeKind == GameRuntimeKind.Electron, "Selected seed/runtime metadata was lost.");
        }
        var game = new GameProfile { ExecutablePath = source.ExecutablePath, ProcessName = source.ProcessName };
        var candidates = service.FindGameCandidates(game, processes);
        Check(candidates.Count == 1 && candidates[0] == renderer, "Library launcher discovery failed to deduplicate the real data process.");
        var second = Item(40, 10, Path.Combine(Path.GetTempPath(), "another-game", "Runtime.exe"), GameRuntimeKind.Electron, GameProcessRole.Main, window: "Other game");
        var secondRenderer = Item(41, 40, second.ExecutablePath, GameRuntimeKind.Electron, GameProcessRole.Renderer);
        var ambiguous = new[] { source, runtime, renderer, second, secondRenderer };
        try { service.ResolveLogicalGame(source, ambiguous); throw new Exception("Ambiguous runtime accepted."); }
        catch (InvalidOperationException error) { Check(error.Message.Contains("多个游戏实例"), "Ambiguity had no actionable explanation."); }
        Check(service.ResolveLogicalGame(renderer, ambiguous).RootProcess == runtime, "Explicit runtime selection rejected another instance.");
        game.Identity = new() { InstallationExecutablePath = source.ExecutablePath };
        Check(service.FindGameCandidates(game, ambiguous).Count == 2, "Ambiguous launcher prevented discovery of its two actual runtimes.");
        foreach (var kind in new[] { GameRuntimeKind.Unity, GameRuntimeKind.UnityMono, GameRuntimeKind.UnityIl2Cpp })
        {
            var known = Item(10, 1, source.ExecutablePath, kind, GameProcessRole.Main);
            Check(service.ResolveLogicalGame(known, [known, runtime, renderer]).DataProcess == known, "Known engine redirected to a web helper: " + kind);
        }
        var windowedNative = Item(10, 1, source.ExecutablePath, GameRuntimeKind.Native, GameProcessRole.Main, window: "Actual game");
        Check(service.ResolveLogicalGame(windowedNative, [windowedNative, runtime, renderer]).DataProcess == windowedNative, "Windowed native game redirected to a web helper.");
        Check(service.ResolveLogicalGame(source, [source, runtime]).RootProcess == source, "A windowed web helper with no renderer was treated as a game runtime.");
        var headlessWeb = Item(20, 10, path, GameRuntimeKind.Electron, GameProcessRole.Main);
        Check(service.ResolveLogicalGame(source, [source, headlessWeb, renderer]).RootProcess == source, "A headless web helper was claimed by a launcher.");
        foreach (var name in new[] { "steam", "explorer", "pwsh", "dotnet", "node", "chrome", "GameValueEditor" })
        {
            var common = Item(10, 1, @"C:\shared\" + name + ".exe", GameRuntimeKind.Native, GameProcessRole.Main);
            Check(service.ResolveLogicalGame(common, [common, runtime, renderer]).DataProcess == common, "A shared launcher claimed its child game: " + name);
        }
        foreach (var invalid in new[]
        {
            Item(20, 10, path, GameRuntimeKind.Electron, GameProcessRole.Main, start.AddSeconds(5)),
            Item(20, 10, path, GameRuntimeKind.Electron, GameProcessRole.Main, default(DateTime)),
            Item(20, 999, path, GameRuntimeKind.Electron, GameProcessRole.Main),
            Item(20, 10, path, GameRuntimeKind.Native, GameProcessRole.Main),
            Item(20, 10, path, GameRuntimeKind.Electron, GameProcessRole.Utility),
            Item(20, 10, @"C:\download\Other\Runtime.exe", GameRuntimeKind.Electron, GameProcessRole.Main)
        })
            Check(service.ResolveLogicalGame(source, [source, invalid]).RootProcess == source, "Unproved child was selected.");
        var missingSourceTime = Item(10, 1, source.ExecutablePath, GameRuntimeKind.Native, GameProcessRole.Main, default(DateTime));
        Check(service.ResolveLogicalGame(missingSourceTime, [missingSourceTime, runtime]).RootProcess == missingSourceTime, "Missing source start time was trusted.");
        var reused = Item(10, 1, source.ExecutablePath, GameRuntimeKind.Native, GameProcessRole.Main, start.AddSeconds(25));
        Check(service.ResolveLogicalGame(reused, [reused, runtime]).RootProcess == reused, "Reused parent PID was trusted.");
        var intermediary = Item(15, 10, Path.Combine(Path.GetTempPath(), "wrapper", "Extract.exe"), GameRuntimeKind.Native, GameProcessRole.Main);
        var chained = Item(20, 15, path, GameRuntimeKind.Electron, GameProcessRole.Main, window: "Game");
        Check(service.ResolveLogicalGame(source, [source, intermediary, chained, renderer]).DataProcess == renderer, "Bounded extraction ancestry was not followed.");
        var cycle = Item(15, 20, intermediary.ExecutablePath, GameRuntimeKind.Native, GameProcessRole.Main);
        Check(service.ResolveLogicalGame(source, [source, cycle, chained]).RootProcess == source, "Cyclic ancestry was accepted.");
        var deepParents = Enumerable.Range(11, 9).Select(id => Item(id, id - 1,
            Path.Combine(Path.GetTempPath(), "wrapper", $"Extract{id}.exe"), GameRuntimeKind.Native, GameProcessRole.Main)).ToArray();
        var deepRuntime = Item(20, 19, path, GameRuntimeKind.Electron, GameProcessRole.Main);
        Check(service.ResolveLogicalGame(source, new[] { source, deepRuntime }.Concat(deepParents).ToArray()).RootProcess == source,
            "An over-depth extraction chain was accepted.");
        var nw = Item(20, 10, path, GameRuntimeKind.NwJs, GameProcessRole.Main, window: "Game");
        var nwRenderer = Item(21, 20, path, GameRuntimeKind.NwJs, GameProcessRole.Renderer);
        Check(service.ResolveLogicalGame(source, [source, nw, nwRenderer]).DataProcess == nwRenderer, "Extracted NW.js stopped routing to its renderer.");
        Check(service.ResolveLogicalGame(source, [source]).DataProcess == source, "A single native process changed behavior.");
        var nativeChild = Item(20, 10, source.ExecutablePath, GameRuntimeKind.Native, GameProcessRole.Main, window: "Game");
        Check(service.ResolveLogicalGame(source, [source, nativeChild]).DataProcess == nativeChild, "Same-EXE native window routing regressed.");
        Check(service.ResolveLogicalGame(otherRenderer, processes).RootProcess == other, "Different launch ancestry merged instances.");
        Console.WriteLine($"Launcher routing: {checks} checks passed; pure process snapshots, no game writes.");

        ProcessItem Item(int id, int parent, string exe, GameRuntimeKind kind, GameProcessRole role, DateTime? time = null, string window = "") => new()
        {
            ProcessId = id, ParentProcessId = parent, ExecutablePath = exe, ProcessName = Path.GetFileNameWithoutExtension(exe),
            RuntimeKind = kind, Role = role, StartTimeUtc = time ?? start.AddSeconds(id), WindowTitle = window, WorkingSetBytes = id
        };
    }

    internal static void InspectLive(int sourceId)
    {
        var service = new ProcessService(); var processes = service.GetProcesses();
        var seed = processes.Single(item => item.ProcessId == sourceId);
        var group = service.ResolveLogicalGame(seed, processes);
        if (group.RootProcess == seed || group.DataProcess.Role != GameProcessRole.Renderer || group.Members.Contains(seed))
            throw new InvalidOperationException("The selected launcher did not resolve a separate real game renderer.");
        Console.WriteLine(JsonSerializer.Serialize(new { verification = "launcher-runtime-route-readonly", sourceId,
            rootId = group.RootProcess.ProcessId, dataId = group.DataProcess.ProcessId, members = group.Members.Count,
            runtime = group.RuntimeKind.ToString(), sameInstance = group.IsSameInstance(service.ResolveLogicalGame(group.DataProcess, processes)),
            gameWriteCalled = false }));
    }
}
