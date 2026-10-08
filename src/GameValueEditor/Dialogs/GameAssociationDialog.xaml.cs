using System.Windows;
using System.Windows.Controls;
using GameValueEditor.Models;
using GameValueEditor.Services;

namespace GameValueEditor.Dialogs;

public partial class GameAssociationDialog : Window
{
    private readonly GameAssociationRequest _request;
    private bool _ready;
    private bool _selectionResolved;

    internal GameAssociationDialog(GameAssociationRequest request)
    {
        _request = request;
        InitializeComponent();
        PromptText.Text = $"暂时无法自动确认“{request.GameName}”的运行进程。请选择实际游戏进程，并核对下面的信息。\n" +
            "关联后保留原条目和历史字段。如果游戏已更新，会单独记录新版本，不直接使用旧内存地址，也不会绕过专属模块兼容检查。";
        _ready = true;
        RefreshChoices();
    }

    public ProcessItem? SelectedProcess => ProcessComboBox.SelectedItem as ProcessItem;

    private void Search_OnChanged(object sender, TextChangedEventArgs e) { if (_ready) RefreshChoices(); }

    private void RefreshChoices()
    {
        var filter = SearchTextBox.Text.Trim();
        var choices = _request.Processes.Where(process => process.DisplayName.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(process => _request.SuggestedProcesses.Any(suggested => suggested.ProcessId == process.ProcessId && suggested.StartTimeUtc == process.StartTimeUtc))
            .ThenByDescending(process => !string.IsNullOrWhiteSpace(process.WindowTitle)).ThenBy(process => process.ProcessName).ToArray();
        ProcessComboBox.ItemsSource = choices;
        // Recommendation is not permission. Always require an explicit selection and checkbox.
        ProcessComboBox.SelectedIndex = -1;
        DetailsText.Text = choices.Length == 0 ? "没有匹配的可访问进程，请取消后刷新进程或调整搜索。" : "请选择实际游戏进程。";
    }

    private void Process_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ConfirmationCheckBox.IsChecked = false;
        _selectionResolved = false;
        ConfirmButton.IsEnabled = false;
        if (SelectedProcess is not { } process) { ConfirmButton.IsEnabled = false; return; }
        LogicalGameProcessGroup group;
        try { group = new ProcessService().ResolveLogicalGame(process, _request.Processes); }
        catch (AmbiguousGameProcessException exception)
        {
            DetailsText.Text = exception.Message;
            return;
        }
        var identity = new GameIdentityEvidenceService().Read(group, _request.Processes);
        DetailsText.Text = $"关联到：{_request.GameName}\n进程：{process.DisplayName}\n实际程序：{group.DataProcess.ExecutablePath}\n" +
            $"启动来源：{(identity.InstallationExecutablePath.Length > 0 ? identity.InstallationExecutablePath : "未能确认固定来源")}\n" +
            $"游戏标识：{(identity.PackageId.Length > 0 ? identity.PackageId : identity.ProductName.Length > 0 ? identity.ProductName : "未提供")}";
        _selectionResolved = true;
    }

    private void Confirmation_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_ready) ConfirmButton.IsEnabled = _selectionResolved && SelectedProcess is not null && ConfirmationCheckBox.IsChecked == true;
    }

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectionResolved && SelectedProcess is not null && ConfirmationCheckBox.IsChecked == true) DialogResult = true;
    }
}
