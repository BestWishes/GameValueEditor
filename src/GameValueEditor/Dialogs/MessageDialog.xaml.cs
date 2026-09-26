using System.Windows;

namespace GameValueEditor.Dialogs;

public partial class MessageDialog : Window
{
    private MessageDialog(string title, string message, bool showCancel)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
    }

    public static bool Confirm(Window owner, string title, string message) =>
        new MessageDialog(title, message, true) { Owner = owner }.ShowDialog() == true;

    public static void ShowInfo(Window owner, string title, string message) =>
        new MessageDialog(title, message, false) { Owner = owner }.ShowDialog();

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
