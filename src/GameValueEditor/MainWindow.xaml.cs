using System.Diagnostics;
using System.Windows;
using GameValueEditor.Dialogs;
using GameValueEditor.ViewModels;

namespace GameValueEditor;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += async (_, _) => await RunGuardedAsync(_viewModel.InitializeAsync);
        Closed += (_, _) => _viewModel.CancelScan();
    }

    private void RefreshProcesses_OnClick(object sender, RoutedEventArgs e) => RunGuarded(_viewModel.RefreshProcesses);

    private async void AttachProcess_OnClick(object sender, RoutedEventArgs e) => await RunGuardedAsync(async () =>
    {
        var process = _viewModel.SelectedProcess ?? throw new InvalidOperationException("请先从顶部选择一个进程。");
        _viewModel.Attach(process);
        await _viewModel.MatchAttachedVersionAsync();
    });

    private async void AddGame_OnClick(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            var process = _viewModel.AttachedProcess;
            if (process is null)
            {
                process = _viewModel.SelectedProcess ?? throw new InvalidOperationException("请先选择并连接一个游戏进程。");
                _viewModel.Attach(process);
            }

            var defaultName = _viewModel.SelectedGame?.Name;
            if (string.IsNullOrWhiteSpace(defaultName))
            {
                try { defaultName = FileVersionInfo.GetVersionInfo(process.ExecutablePath).ProductName; }
                catch { defaultName = null; }
            }
            defaultName = string.IsNullOrWhiteSpace(defaultName) ? process.ProcessName : defaultName;

            var dialog = new TextInputDialog("保存游戏", "游戏在左侧列表中显示的名称：", defaultName) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await _viewModel.AddCurrentProcessToLibraryAsync(dialog.Value);
        });
    }

    private async void PinGame_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(_viewModel.TogglePinnedAsync);

    private async void LockGame_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(_viewModel.ToggleLockedAsync);

    private async void RenameGame_OnClick(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            var game = _viewModel.SelectedGame ?? throw new InvalidOperationException("请先选择游戏条目。");
            var dialog = new TextInputDialog("重命名游戏", "新的游戏名称：", game.Name) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await _viewModel.RenameSelectedGameAsync(dialog.Value);
        });
    }

    private async void AttachSelectedGame_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(_viewModel.AttachSelectedGameAsync);

    private async void DeleteGame_OnClick(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            var game = _viewModel.SelectedGame ?? throw new InvalidOperationException("请先选择游戏条目。");
            if (game.IsPinned) throw new InvalidOperationException("置顶游戏不能删除，请先取消置顶。");
            if (game.IsLocked) throw new InvalidOperationException("锁定游戏不能删除，请先解锁。");
            var answer = MessageBox.Show(
                this,
                $"确定删除“{game.Name}”及其全部版本和字段配置吗？\n这个操作不会修改游戏文件。",
                "删除游戏条目",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes) await _viewModel.DeleteSelectedGameAsync();
        });
    }

    private async void NewScan_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(() => _viewModel.RunScanAsync(true));

    private async void NextScan_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(() => _viewModel.RunScanAsync(false));

    private void CancelScan_OnClick(object sender, RoutedEventArgs e) => _viewModel.CancelScan();
    private void ResetScan_OnClick(object sender, RoutedEventArgs e) => _viewModel.ResetScan();

    private async void SaveField_OnClick(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            if (_viewModel.SelectedScanResult is null) throw new InvalidOperationException("请先选择一个扫描结果。");
            if (_viewModel.SelectedGame is null || _viewModel.SelectedVersion is null)
                throw new InvalidOperationException("请先点击顶部的“保存到游戏库”，再保存字段。");
            var dialog = new SaveFieldDialog { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await _viewModel.SaveSelectedCandidateAsync(dialog.FieldName, dialog.GroupName, dialog.Note);
        });
    }

    private async void RelocateField_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(_viewModel.RelocateSelectedFieldAsync);

    private void RefreshFields_OnClick(object sender, RoutedEventArgs e) => RunGuarded(_viewModel.RefreshSavedValues);

    private async void WriteField_OnClick(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            var field = _viewModel.SelectedSavedField ?? throw new InvalidOperationException("请先选择一个已保存字段。");
            var initial = field.CurrentValue == "—" ? string.Empty : field.CurrentValue;
            var dialog = new TextInputDialog("修改数值", $"输入“{field.Name}”的新值（{field.TypeDisplay}）：", initial) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await _viewModel.WriteSelectedFieldAsync(dialog.Value);
        });
    }

    private async void DeleteField_OnClick(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            var field = _viewModel.SelectedSavedField ?? throw new InvalidOperationException("请先选择字段。");
            var answer = MessageBox.Show(this, $"确定删除字段“{field.Name}”吗？", "删除字段", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes) await _viewModel.DeleteSelectedFieldAsync();
        });
    }

    private void OpenLibraryFolder_OnClick(object sender, RoutedEventArgs e) => RunGuarded(() =>
    {
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        info.ArgumentList.Add("/select,");
        info.ArgumentList.Add(_viewModel.LibraryPath);
        Process.Start(info);
    });

    private void RunGuarded(Action action)
    {
        try { action(); }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task RunGuardedAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { ShowError(exception); }
    }

    private void ShowError(Exception exception) => MessageBox.Show(
        this,
        exception.Message,
        "操作未完成",
        MessageBoxButton.OK,
        MessageBoxImage.Information);
}
