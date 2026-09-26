using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using GameValueEditor.Dialogs;
using GameValueEditor.Services;

namespace GameValueEditor;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            if (ApplicationUpdateService.TryLaunchPendingAtStartup())
            {
                Shutdown();
                return;
            }
        }
        catch (Exception exception)
        {
            // A damaged pending update must not prevent the installed version from starting.
            WriteCrashLog(exception);
        }
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ThemeService.ApplyWindowChrome((Window)sender)));
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.SetObserved();
        };
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = WriteCrashLog(e.Exception);
        if (MainWindow is { } owner)
            MessageDialog.ShowInfo(owner, "程序发生错误", $"操作没有完成，错误已经记录到：\n{logPath}\n\n{e.Exception.Message}");
        e.Handled = true;
    }

    private static string WriteCrashLog(Exception exception)
    {
        string directory;
        try
        {
            directory = ProfileStore.DefaultRoot;
            Directory.CreateDirectory(directory);
        }
        catch
        {
            directory = Path.Combine(Path.GetTempPath(), "GameValueEditor");
            Directory.CreateDirectory(directory);
        }

        var path = Path.Combine(directory, "crash.log");
        var text = new StringBuilder()
            .AppendLine($"[{DateTimeOffset.Now:O}]")
            .AppendLine(exception.ToString())
            .AppendLine(new string('-', 80))
            .ToString();
        File.AppendAllText(path, text, Encoding.UTF8);
        return path;
    }
}

