using System.Windows;
using GameValueEditor.Dialogs;
using GameValueEditor.ModuleSdk;
using GameValueEditor.ViewModels;

namespace GameValueEditor.Services.Adapters;

internal sealed class WpfGameEditorHostServices(Window owner, MainViewModel viewModel) : IGameEditorHostServices
{
    public async Task<string?> PromptValueAsync(GameEditorTextPrompt prompt)
    {
        return await owner.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new TextInputDialog(prompt.Title, prompt.Message, prompt.InitialValue) { Owner = owner };
            return dialog.ShowDialog() == true ? dialog.Value : null;
        });
    }

    public async Task SaveFieldAsync(GameEditorSavedFieldRequest request)
    {
        var selection = await owner.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new AdapterFieldDialog(
                viewModel.GetAvailableGroups(),
                request.FieldKey,
                request.SuggestedDisplayName) { Owner = owner };
            return dialog.ShowDialog() == true
                ? new SavedFieldSelection(dialog.DisplayName, dialog.GroupName)
                : null;
        });
        if (selection is null) return;
        await viewModel.AddAdapterFieldAsync(request.FieldKey, selection.DisplayName, selection.GroupName);
    }

    public void ReportStatus(string message) =>
        owner.Dispatcher.Invoke(() => viewModel.ReportModulePageStatus(message));

    public void ShowError(string title, string message) =>
        owner.Dispatcher.Invoke(() => MessageDialog.ShowInfo(owner, title, message));

    private sealed record SavedFieldSelection(string DisplayName, string GroupName);
}
