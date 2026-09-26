using System.Windows;

namespace GameValueEditor.Dialogs;

public partial class ModifyFieldDialog : Window
{
    public ModifyFieldDialog(string name, string group, string currentValue, IEnumerable<string> groups)
    {
        InitializeComponent();
        NameTextBox.Text = name;
        GroupComboBoxHelper.Configure(GroupComboBox, groups, group);
        ValueTextBox.Text = currentValue == "—" ? string.Empty : currentValue;
        Loaded += (_, _) => NameTextBox.Focus();
    }

    public string FieldName => NameTextBox.Text.Trim();
    public string GroupName => string.IsNullOrWhiteSpace(GroupComboBox.Text) ? "未分组" : GroupComboBox.Text.Trim();
    public string FieldValue => ValueTextBox.Text.Trim();

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FieldName))
        {
            ErrorText.Text = "备注名称不能为空。";
            NameTextBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
