using System.Windows;

namespace GameValueEditor.Dialogs;

public partial class AdapterFieldDialog : Window
{
    public AdapterFieldDialog(IEnumerable<string>? groups = null, string fieldKey = "", string displayName = "")
    {
        InitializeComponent();
        GroupComboBoxHelper.Configure(GroupComboBox, groups, "未分组");
        FieldKeyTextBox.Text = fieldKey;
        DisplayNameTextBox.Text = displayName;
        FieldKeyTextBox.IsReadOnly = !string.IsNullOrWhiteSpace(fieldKey);
        Loaded += (_, _) => FieldKeyTextBox.Focus();
    }

    public string FieldKey => FieldKeyTextBox.Text.Trim();
    public string DisplayName => DisplayNameTextBox.Text.Trim();
    public string GroupName => string.IsNullOrWhiteSpace(GroupComboBox.Text) ? "未分组" : GroupComboBox.Text.Trim();

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FieldKey))
        {
            ErrorText.Text = "请填写游戏内字段键。";
            FieldKeyTextBox.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            ErrorText.Text = "请手动填写备注名称。";
            DisplayNameTextBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
