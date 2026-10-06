using System.IO;
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
    private ApplicationInstanceLease? _instanceLease;
    private static readonly CrashLogService CrashLogger = new(ProfileStore.DefaultRoot);

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            if (!ApplicationInstanceLease.TryAcquire(ProfileStore.DefaultRoot, out _instanceLease, out var owner))
            {
                if (!ApplicationInstanceLease.TryActivate(owner))
                    MessageBox.Show("同一数据目录的肝肾大圣已经在运行或正在启动，请使用原窗口。", "肝肾大圣",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("无法取得数据目录的独占访问权限，已停止启动以保护资料。\n\n" + exception.Message,
                "肝肾大圣 · 启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }
        try
        {
            if (ApplicationUpdateService.TryLaunchPendingAtStartup())
            {
                Shutdown();
                return;
            }
        }
        catch (ApplicationRecoveryRequiredException exception)
        {
            WriteCrashLog(exception);
            MessageBox.Show(exception.Message + "\n\n不要删除恢复记录绕过检查。关闭占用文件的程序后，用记录中的更新器执行手动恢复。",
                "肝肾大圣 · 安装需要恢复", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
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

    protected override void OnExit(ExitEventArgs e)
    {
        try { base.OnExit(e); }
        finally { _instanceLease?.Dispose(); }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var logPath = WriteCrashLog(e.Exception);
        if (MainWindow is { } owner)
            MessageDialog.ShowInfo(owner, "程序发生错误", FormatCrashMessage(e.Exception, logPath));
    }

    internal static string FormatCrashMessage(Exception exception, string? logPath)
    {
        var logDescription = logPath is null ? "日志无法写入；原错误如下：" : $"错误已经记录到：\n{logPath}";
        return $"操作没有完成，{logDescription}\n\n{exception.Message}";
    }

    private static string? WriteCrashLog(Exception exception) => CrashLogger.TryWrite(exception);
}

