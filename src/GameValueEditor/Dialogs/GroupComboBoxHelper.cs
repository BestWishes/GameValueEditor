using System.Collections;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace GameValueEditor.Dialogs;

internal static class GroupComboBoxHelper
{
    public static void Configure(ComboBox comboBox, IEnumerable<string>? groups, string initialText)
    {
        var values = (groups ?? [])
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => group, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (!values.Contains("未分组", StringComparer.CurrentCultureIgnoreCase)) values.Add("未分组");

        var view = new ListCollectionView((IList)values);
        comboBox.ItemsSource = view;
        comboBox.IsEditable = true;
        comboBox.IsTextSearchEnabled = false;
        comboBox.StaysOpenOnEdit = true;
        comboBox.Text = initialText;

        var suppressFilter = false;
        TextBox? editableTextBox = null;

        comboBox.Loaded += (_, _) =>
        {
            comboBox.ApplyTemplate();
            if (comboBox.Template.FindName("PART_EditableTextBox", comboBox) is not TextBox editor) return;
            editableTextBox = editor;
            editor.TextChanged += (_, _) =>
            {
                if (suppressFilter) return;
                var query = editor.Text.Trim();
                if (comboBox.SelectedItem is string selected &&
                    !string.Equals(selected, query, StringComparison.CurrentCultureIgnoreCase))
                {
                    suppressFilter = true;
                    comboBox.SelectedIndex = -1;
                    comboBox.Text = query;
                    suppressFilter = false;
                }
                view.Filter = item => string.IsNullOrWhiteSpace(query) ||
                                      item is string group && group.Contains(query, StringComparison.CurrentCultureIgnoreCase);
                view.Refresh();
                if (!editor.IsKeyboardFocusWithin) return;
                comboBox.IsDropDownOpen = view.Count > 0;
                editor.CaretIndex = editor.Text.Length;
            };
            editor.GotKeyboardFocus += (_, _) => editor.SelectAll();
            editor.PreviewKeyDown += (_, args) =>
            {
                if (args.Key == Key.Escape) comboBox.IsDropDownOpen = false;
            };
        };

        comboBox.SelectionChanged += (_, _) =>
        {
            if (comboBox.SelectedItem is not string selected) return;
            suppressFilter = true;
            comboBox.Text = selected;
            if (editableTextBox is not null)
            {
                editableTextBox.Text = selected;
                editableTextBox.CaretIndex = selected.Length;
            }
            comboBox.IsDropDownOpen = false;
            view.Filter = null;
            view.Refresh();
            comboBox.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => suppressFilter = false);
        };
    }
}
