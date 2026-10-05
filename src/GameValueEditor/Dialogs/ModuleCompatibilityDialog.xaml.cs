using System.Windows;
using GameValueEditor.Services.Adapters;

namespace GameValueEditor.Dialogs;

public partial class ModuleCompatibilityDialog : Window
{
    private readonly string _reportText;

    internal ModuleCompatibilityDialog(ModuleCompatibilityReport report)
    {
        InitializeComponent();
        DiagnosticsGrid.ItemsSource = report.Items;
        _reportText = report.Text;
    }

    private void CopyReport_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_reportText);
            CopyStatusText.Text = "已复制脱敏诊断报告。";
        }
        catch (Exception exception)
        {
            CopyStatusText.Text = $"复制失败：{exception.Message}";
        }
    }
}
