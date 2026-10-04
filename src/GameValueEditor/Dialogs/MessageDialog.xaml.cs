using System.Windows;
using System.Windows.Controls;

namespace GameValueEditor.Dialogs;

public partial class MessageDialog : Window
{
    private MessageDialog(string title, string message, bool showCancel)
        : this(title, message, showCancel, null)
    {
    }

    private MessageDialog(
        string title,
        string message,
        bool showCancel,
        IReadOnlyList<MessageDialogAction>? actions = null)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        foreach (var action in actions ?? [])
        {
            var button = new Button { Content = action.Text, MinWidth = 96 };
            button.Click += (_, _) =>
            {
                try { action.Callback(); }
                catch (Exception exception) { ShowInfo(this, "操作未完成", exception.Message); }
            };
            ActionButtonsPanel.Children.Add(button);
        }
    }

    public static bool Confirm(Window owner, string title, string message) =>
        new MessageDialog(title, message, true) { Owner = owner }.ShowDialog() == true;

    public static void ShowInfo(
        Window owner,
        string title,
        string message,
        params MessageDialogAction[] actions) =>
        new MessageDialog(title, message, false, actions) { Owner = owner }.ShowDialog();

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}

public sealed record MessageDialogAction(string Text, Action Callback);
