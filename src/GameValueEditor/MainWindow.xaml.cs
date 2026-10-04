using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameValueEditor.Dialogs;
using GameValueEditor.Models;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Services.Adapters;
using GameValueEditor.ViewModels;

namespace GameValueEditor;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly DispatcherTimer _connectionMonitorTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _connectionMonitorTimer.Tick += ConnectionMonitorTimer_OnTick;
        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(_viewModel.InitializeAsync);
        _connectionMonitorTimer.Start();
        var updateFailure = _viewModel.TakeLastApplicationUpdateFailure();
        if (updateFailure is not null)
        {
            var updateDirectory = Path.GetDirectoryName(updateFailure.LogPath) ?? AppContext.BaseDirectory;
            MessageDialog.ShowInfo(
                this,
                "更新未完成",
                $"已继续使用当前版本。\n\n{updateFailure.Message}\n\n详细日志：\n{updateFailure.LogPath}",
                new MessageDialogAction("打开更新目录", () =>
                    Process.Start(new ProcessStartInfo(updateDirectory) { UseShellExecute = true })),
                new MessageDialogAction("打开下载页", () =>
                    Process.Start(new ProcessStartInfo("https://github.com/BestWishes/GameValueEditor/releases/latest")
                        { UseShellExecute = true })),
                new MessageDialogAction("复制日志位置", () => Clipboard.SetText(updateFailure.LogPath)));
        }
    }

    private void ConnectionMonitorTimer_OnTick(object? sender, EventArgs e) =>
        _viewModel.SynchronizeConnectionStates();

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        _connectionMonitorTimer.Stop();
        try
        {
            _viewModel.Shutdown();
        }
        catch (Exception exception)
        {
            e.Cancel = true;
            ShowError(exception);
        }
    }

    private async void RefreshProcesses_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(_viewModel.RefreshProcessesWithCooldownAsync);

    private async void AttachProcess_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(_viewModel.AttachSelectedProcessAsync);

    private async void AddGame_OnClick(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            var process = _viewModel.AttachedProcess
                          ?? throw new InvalidOperationException("请先连接一个游戏进程。");

            var defaultName = _viewModel.SelectedGame?.Name;
            if (string.IsNullOrWhiteSpace(defaultName))
            {
                try { defaultName = FileVersionInfo.GetVersionInfo(process.ExecutablePath).ProductName; }
                catch { defaultName = null; }
            }
            defaultName = string.IsNullOrWhiteSpace(defaultName) ? process.ProcessName : defaultName;

            var dialog = new TextInputDialog("保存入库", "备注名称：", defaultName) { Owner = this };
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

    private async void DisconnectSelectedGame_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(_viewModel.DisconnectSelectedGameAsync);

    private async void DeleteGame_OnClick(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            var game = _viewModel.SelectedGame ?? throw new InvalidOperationException("请先选择游戏条目。");
            if (game.IsPinned) throw new InvalidOperationException("置顶游戏不能从库移出，请先取消置顶。");
            if (game.IsLocked) throw new InvalidOperationException("锁定游戏不能从库移出，请先解锁。");
            var moduleMessage = game.IsModuleInstalled
                ? "\n本地安装的该游戏专属模块也会一并卸载。"
                : string.Empty;
            if (MessageDialog.Confirm(
                    this,
                    "从游戏库移出",
                    $"确定将“{game.Name}”及其全部版本和字段配置从游戏库移出吗？{moduleMessage}\n这个操作不会修改游戏文件；如果游戏正在连接，当前连接会继续保留。"))
                await _viewModel.DeleteSelectedGameAsync();
        });
    }

    private async void NewScan_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(() => _viewModel.RunScanAsync(true));

    private async void NextScan_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(() => _viewModel.RunScanAsync(false));

    private void CancelScan_OnClick(object sender, RoutedEventArgs e) => _viewModel.CancelScan();
    private void ResetScan_OnClick(object sender, RoutedEventArgs e) => _viewModel.ResetScan();
    private void UndoScan_OnClick(object sender, RoutedEventArgs e) => _viewModel.UndoScan();
    private void ClearGameSearch_OnClick(object sender, RoutedEventArgs e) => _viewModel.ClearSearch();

    private async void AccelerateGame_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(_viewModel.AccelerateGameAsync);

    private async void RestoreGameSpeed_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(_viewModel.RestoreGameSpeedAsync);

    private async void CheckGameModules_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(_viewModel.CheckGameModuleUpdatesAsync);

    private async void InstallGameModule_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(_viewModel.InstallAvailableGameModuleAsync);

    private async void UninstallGameModule_OnClick(object sender, RoutedEventArgs e)
    {
        if (!MessageDialog.Confirm(this, "卸载专属模块", "确定卸载当前游戏的本地专属模块吗？\n游戏库、游戏版本和快捷入口会保留。")) return;
        await RunGuardedAsync(_viewModel.UninstallCurrentGameModuleAsync);
    }

    private void ShowModuleContributors_OnClick(object sender, RoutedEventArgs e) => RunGuarded(() =>
    {
        var contributors = _viewModel.GetModuleContributors();
        if (contributors.Count == 0) throw new InvalidOperationException("当前模块尚无可显示的贡献者信息。");
        new ModuleContributorsDialog(contributors) { Owner = this }.ShowDialog();
    });

    private async void ApplicationUpdate_OnClick(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            if (!_viewModel.HasApplicationUpdateAvailable)
            {
                await _viewModel.CheckApplicationUpdateAsync();
                return;
            }

            if (!await _viewModel.DownloadApplicationUpdateAsync()) return;
            var restartNow = MessageDialog.Confirm(
                this,
                "更新已准备完成",
                "新版本已下载并校验完成。\n\n是否现在关闭肝肾大圣并安装更新？\n选择“取消”将继续使用，下次启动时自动更新。");
            if (!restartNow) return;
            if (_viewModel.LaunchPendingApplicationUpdate()) Application.Current.Shutdown();
        });
    }

    private void OpenOfficialWebsite_OnClick(object sender, RoutedEventArgs e) =>
        RunGuarded(_viewModel.OpenOfficialWebsite);

    private async void SaveField_OnClick(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            var selected = ScanResultsGrid.SelectedItems.Cast<ScanCandidate>().ToList();
            if (selected.Count != 1) throw new InvalidOperationException("保存字段只支持单选，请只选择一个已经验证有效的扫描结果。");
            _viewModel.SelectedScanResult = selected[0];
            if (_viewModel.SelectedGame is null || _viewModel.SelectedVersion is null)
                throw new InvalidOperationException("请先点击游戏名称后的“保存入库”，再保存字段。");
            var dialog = new SaveFieldDialog(_viewModel.GetAvailableGroups()) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await _viewModel.SaveSelectedCandidateAsync(dialog.FieldName, dialog.GroupName);
        });
    }

    private async void RefreshFields_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(_viewModel.RefreshSavedValuesAsync);

    private async void AddAdapterField_OnClick(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            if (_viewModel.SelectedGame is null || _viewModel.SelectedVersion is null)
                throw new InvalidOperationException("请先点击游戏名称后的“保存入库”，再添加专属字段。");
            var dialog = new AdapterFieldDialog(_viewModel.GetAvailableGroups()) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await _viewModel.AddAdapterFieldAsync(dialog.FieldKey, dialog.DisplayName, dialog.GroupName);
        });
    }

    private async void Theme_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _viewModel.SelectedTheme is null) return;
        await RunGuardedAsync(() => _viewModel.ChangeThemeAsync(_viewModel.SelectedTheme));
    }

    private async void WriteField_OnClick(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            var field = _viewModel.SelectedSavedField ?? throw new InvalidOperationException("请先选择一个已保存字段。");
            var dialog = new ModifyFieldDialog(field.Name, field.Group, field.CurrentValue, _viewModel.GetAvailableGroups()) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await _viewModel.UpdateSelectedFieldAsync(dialog.FieldName, dialog.GroupName, dialog.FieldValue);
        });
    }

    private async void RefreshAdapterInventory_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(_viewModel.RefreshAdapterInventoryAsync);

    private async void WriteAdapterItem_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DataGrid grid)
            await EditSelectedAdapterItemsAsync(grid);
    }

    private async void AdapterInventoryGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is null) return;
        if (sender is DataGrid grid) await EditSelectedAdapterItemsAsync(grid);
    }

    private async Task EditSelectedAdapterItemsAsync(DataGrid grid)
    {
        await RunGuardedAsync(async () =>
        {
            var items = grid.SelectedItems.Cast<AdapterInventoryItem>().ToList();
            if (items.Count == 0) throw new InvalidOperationException("请至少选择一个背包物品。");
            var initialValue = items.Count == 1 ? items[0].CountDisplay : string.Empty;
            var prompt = items.Count == 1
                ? $"输入“{items[0].DisplayName}”的新物品总数（不限制为 9999）："
                : $"把选中的 {items.Count:N0} 种物品修改为同一个物品总数（不限制为 9999）：";
            var dialog = new TextInputDialog(
                items.Count == 1 ? "修改背包物品数量" : "批量修改背包物品数量",
                prompt,
                initialValue) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await _viewModel.WriteAdapterItemsAsync(items, dialog.Value);
        });
    }

    private async void SaveAdapterItem_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DataGrid grid) return;
        await RunGuardedAsync(async () =>
        {
            var selected = grid.SelectedItems.Cast<AdapterInventoryItem>().ToList();
            if (selected.Count != 1) throw new InvalidOperationException("添加到已保存字段只支持单选，请只选择一种物品。");
            var item = selected[0];
            _viewModel.SelectedAdapterItem = item;
            if (_viewModel.SelectedGame is null || _viewModel.SelectedVersion is null)
                throw new InvalidOperationException("请先点击游戏名称后的“保存入库”，再保存字段。");
            var dialog = new AdapterFieldDialog(
                _viewModel.GetAvailableGroups(), item.FieldKey, item.DisplayName) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await _viewModel.AddAdapterFieldAsync(dialog.FieldKey, dialog.DisplayName, dialog.GroupName);
        });
    }

    private async void ScanResultsGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is null) return;
        await EditSelectedScanResultsAsync();
    }

    private async void WriteScanResults_OnClick(object sender, RoutedEventArgs e) =>
        await EditSelectedScanResultsAsync();

    private async Task EditSelectedScanResultsAsync()
    {
        await RunGuardedAsync(async () =>
        {
            var candidates = ScanResultsGrid.SelectedItems.Cast<ScanCandidate>().ToList();
            if (candidates.Count == 0) throw new InvalidOperationException("请至少选择一个扫描结果。");
            var candidate = candidates[0];
            var prompt = candidates.Count == 1
                ? $"输入 {candidate.AddressDisplay} 的新界面值（{candidate.RoutineDisplay}）："
                : $"把选中的 {candidates.Count:N0} 个候选地址修改为同一个界面值：";
            var dialog = new TextInputDialog(
                candidates.Count == 1 ? "临时修改候选地址" : "批量修改候选地址",
                prompt,
                candidates.Count == 1 ? candidate.CurrentDisplay : string.Empty) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await _viewModel.WriteCandidatesAsync(candidates, dialog.Value);
        });
    }

    private void ScanResultsGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var count = ScanResultsGrid.SelectedItems.Count;
        if (WriteScanResultsButton is not null) WriteScanResultsButton.IsEnabled = count > 0;
        if (SaveScanFieldButton is not null) SaveScanFieldButton.IsEnabled = count == 1;
    }

    private async void RefreshAdapterCharacters_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(_viewModel.RefreshAdapterCharactersAsync);

    private async void WriteCharacterAttribute_OnClick(object sender, RoutedEventArgs e) =>
        await EditSelectedCharacterAttributeAsync();

    private async void AdapterCharacterAttributesGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is null) return;
        await EditSelectedCharacterAttributeAsync();
    }

    private async Task EditSelectedCharacterAttributeAsync()
    {
        await RunGuardedAsync(async () =>
        {
            var character = _viewModel.SelectedAdapterCharacter
                            ?? throw new InvalidOperationException("请先选择一个人物。");
            var attribute = _viewModel.SelectedCharacterAttribute
                            ?? throw new InvalidOperationException("请先选择一个人物属性。");
            var lifetimeNote = _viewModel.IsSelectedCharacterFieldSessionOnly ? "（仅本次游戏运行有效）" : string.Empty;
            var dialog = new TextInputDialog(
                "修改人物属性",
                $"输入“{character.DisplayName}”的{attribute.DisplayName}目标值{lifetimeNote}：",
                attribute.RawValueDisplay) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await _viewModel.WriteSelectedCharacterAttributeAsync(dialog.Value);
        });
    }

    private async void SaveCharacterAttribute_OnClick(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            var character = _viewModel.SelectedAdapterCharacter
                            ?? throw new InvalidOperationException("请先选择一个人物。");
            var attribute = _viewModel.SelectedCharacterAttribute
                            ?? throw new InvalidOperationException("请先选择一个人物属性。");
            if (_viewModel.SelectedGame is null || _viewModel.SelectedVersion is null)
                throw new InvalidOperationException("请先点击游戏名称后的“保存入库”，再保存字段。");
            var fieldKey = ModuleFieldKey.Create(_viewModel.ActiveCharacterEditorId, character.CharacterId, attribute.Key);
            var dialog = new AdapterFieldDialog(
                _viewModel.GetAvailableGroups(), fieldKey, $"{character.DisplayName} {attribute.DisplayName}") { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await _viewModel.AddCharacterAttributeFieldAsync(dialog.DisplayName, dialog.GroupName);
        });
    }

    private async void RefreshEntityEditor_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AdapterEntityEditorState editor) return;
        await RunGuardedAsync(() => _viewModel.RefreshEntityEditorAsync(editor));
    }

    private async void WriteEntityEditorField_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AdapterEntityEditorState editor) return;
        await EditEntityEditorFieldAsync(editor);
    }

    private async void EntityEditorFieldsGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is null ||
            (sender as FrameworkElement)?.DataContext is not AdapterEntityEditorState editor)
            return;
        await EditEntityEditorFieldAsync(editor);
    }

    private async Task EditEntityEditorFieldAsync(AdapterEntityEditorState editor)
    {
        await RunGuardedAsync(async () =>
        {
            var entity = editor.SelectedEntity ?? throw new InvalidOperationException("请先选择一个修改项目。");
            var field = editor.SelectedField ?? throw new InvalidOperationException("请先选择一个可修改字段。");
            if (!field.CanWrite) throw new InvalidOperationException("该字段当前不可修改。");
            var dialog = new TextInputDialog(
                $"修改{editor.Descriptor.DisplayName}",
                $"输入“{entity.DisplayName}”的{field.DisplayName}目标值（{field.RangeDisplay}）：",
                field.ValueDisplay) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await _viewModel.WriteEntityEditorFieldAsync(editor, dialog.Value);
        });
    }

    private async void SaveEntityEditorField_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AdapterEntityEditorState editor) return;
        await RunGuardedAsync(async () =>
        {
            var entity = editor.SelectedEntity ?? throw new InvalidOperationException("请先选择一个修改项目。");
            var field = editor.SelectedField ?? throw new InvalidOperationException("请先选择一个可修改字段。");
            if (_viewModel.SelectedGame is null || _viewModel.SelectedVersion is null)
                throw new InvalidOperationException("请先点击游戏名称后的“保存入库”，再保存字段。");
            var fieldKey = ModuleFieldKey.Create(editor.Descriptor.Id, entity.EntityId, field.Key);
            var dialog = new AdapterFieldDialog(
                _viewModel.GetAvailableGroups(), fieldKey, $"{entity.DisplayName} {field.DisplayName}") { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await _viewModel.AddEntityEditorFieldAsync(editor, dialog.DisplayName, dialog.GroupName);
        });
    }

    private async void SavedFieldsGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell?.DataContext is not Models.SavedField field) return;
        _viewModel.SelectedSavedField = field;
        var header = cell.Column.Header?.ToString();
        if (header == "锁定状态") return;

        await RunGuardedAsync(async () =>
        {
            switch (header)
            {
                case "备注名称":
                {
                    var dialog = new TextInputDialog("修改备注名称", "新的备注名称：", field.Name) { Owner = this };
                    if (dialog.ShowDialog() == true) await _viewModel.RenameSelectedFieldAsync(dialog.Value);
                    break;
                }
                case "分组":
                {
                    var dialog = new GroupInputDialog(_viewModel.GetAvailableGroups(), field.Group) { Owner = this };
                    if (dialog.ShowDialog() == true) await _viewModel.ChangeSelectedFieldGroupAsync(dialog.GroupName);
                    break;
                }
                case "当前值":
                {
                    var initial = field.CurrentValue == "—" ? string.Empty : field.CurrentValue;
                    var dialog = new TextInputDialog("修改字段值", $"输入“{field.Name}”的新值：", initial) { Owner = this };
                    if (dialog.ShowDialog() == true) await _viewModel.WriteSelectedFieldAsync(dialog.Value);
                    break;
                }
            }
        });
    }

    private async void SavedFieldsGrid_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell?.Column.Header?.ToString() != "锁定状态" || cell.DataContext is not Models.SavedField field) return;
        e.Handled = true;
        _viewModel.SelectedSavedField = field;
        await RunGuardedAsync(_viewModel.ToggleSelectedFieldValueLockAsync);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T result) return result;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private async void DeleteField_OnClick(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            var field = _viewModel.SelectedSavedField ?? throw new InvalidOperationException("请先选择字段。");
            if (MessageDialog.Confirm(this, "删除字段", $"确定删除字段“{field.Name}”吗？"))
                await _viewModel.DeleteSelectedFieldAsync();
        });
    }

    private void OpenLibraryFolder_OnClick(object sender, RoutedEventArgs e) => RunGuarded(() =>
    {
        var directory = Path.GetDirectoryName(_viewModel.LibraryPath)
                        ?? throw new InvalidOperationException("无法确定本地数据目录。");
        Directory.CreateDirectory(directory);
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        info.ArgumentList.Add(directory);
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

    private void ShowError(Exception exception) =>
        MessageDialog.ShowInfo(this, "操作未完成", exception.Message);
}
