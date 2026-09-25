using System.Windows;

namespace GameValueEditor.Dialogs;

public partial class SaveFieldDialog : Window
{
    public SaveFieldDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => NameTextBox.Focus();
    }

    public string FieldName => NameTextBox.Text.Trim();
    public string GroupName => GroupTextBox.Text.Trim();
    public string Note => NoteTextBox.Text.Trim();

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FieldName))
        {
            ErrorText.Text = "请手动输入字段名称。";
            NameTextBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
