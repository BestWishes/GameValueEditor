using System.Diagnostics;
using GameValueEditor.Models;

namespace GameValueEditor.Services;

public sealed class ProcessService
{
    public IReadOnlyList<ProcessItem> GetProcesses()
    {
        var currentId = Environment.ProcessId;
        var result = new List<ProcessItem>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == currentId || process.HasExited) continue;
                var path = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(path)) continue;

                result.Add(new ProcessItem
                {
                    ProcessId = process.Id,
                    ProcessName = process.ProcessName,
                    WindowTitle = process.MainWindowTitle,
                    ExecutablePath = path,
                    StartTimeUtc = process.StartTime.ToUniversalTime()
                });
            }
            catch
            {
                // Protected system processes are intentionally omitted.
            }
            finally
            {
                process.Dispose();
            }
        }

        return result
            .OrderByDescending(item => !string.IsNullOrWhiteSpace(item.WindowTitle))
            .ThenBy(item => item.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public ProcessItem? FindRunningGame(GameProfile game) => GetProcesses().FirstOrDefault(item =>
        string.Equals(item.ExecutablePath, game.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(item.ProcessName, game.ProcessName, StringComparison.OrdinalIgnoreCase));
}
