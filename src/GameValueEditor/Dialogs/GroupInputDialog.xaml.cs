using System.Windows;

namespace GameValueEditor.Dialogs;

public partial class GroupInputDialog : Window
{
    public GroupInputDialog(IEnumerable<string> groups, string currentGroup)
    {
        InitializeComponent();
        GroupComboBoxHelper.Configure(GroupComboBox, groups, currentGroup);
        Loaded += (_, _) => GroupComboBox.Focus();
    }

    public string GroupName => string.IsNullOrWhiteSpace(GroupComboBox.Text) ? "未分组" : GroupComboBox.Text.Trim();

    private void Save_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
