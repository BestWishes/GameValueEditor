using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using GameValueEditor.Infrastructure;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.ViewModels;

public sealed class AdapterEntityEditorState : AdapterEditorPageState
{
    private AdapterEditorEntity? _selectedEntity;
    private AdapterEditorField? _selectedField;
    private ICollectionView? _fieldsView;
    private string _filterText = string.Empty;
    private string _selectedGroup = "全部类别";

    public AdapterEntityEditorState(
        GameEditorDescriptor descriptor,
        GameEditorPageRegistration registration,
        bool isSupported) : base(descriptor, registration, isSupported)
    {
        EntitiesView = CollectionViewSource.GetDefaultView(Entities);
        EntitiesView.Filter = item =>
            item is AdapterEditorEntity entity &&
            (SelectedGroup == "全部类别" || string.Equals(entity.Summary, SelectedGroup, StringComparison.CurrentCulture)) &&
            (string.IsNullOrWhiteSpace(FilterText) ||
             entity.DisplayName.Contains(FilterText.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
             entity.Fields.Any(field =>
                 field.DisplayName.Contains(FilterText.Trim(), StringComparison.CurrentCultureIgnoreCase)));
    }

    public ObservableCollection<AdapterEditorEntity> Entities { get; } = [];
    public ObservableCollection<string> GroupOptions { get; } = ["全部类别"];
    public ICollectionView EntitiesView { get; }
    public ICollectionView? FieldsView => _fieldsView;
    public bool IsEmpty => Entities.Count == 0;
    public bool HasGroups => GroupOptions.Count > 2;
    public string EmptyText => string.IsNullOrWhiteSpace(Registration.EmptyMessage)
        ? "当前没有可显示的项目。"
        : Registration.EmptyMessage;

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!SetProperty(ref _filterText, value)) return;
            EntitiesView.Refresh();
            _fieldsView?.Refresh();
        }
    }

    public string SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!SetProperty(ref _selectedGroup, value)) return;
            EntitiesView.Refresh();
        }
    }

    public AdapterEditorEntity? SelectedEntity
    {
        get => _selectedEntity;
        set
        {
            if (!SetProperty(ref _selectedEntity, value)) return;
            var selected = value;
            _fieldsView = selected is null ? null : CollectionViewSource.GetDefaultView(selected.Fields);
            if (_fieldsView is not null)
                _fieldsView.Filter = item =>
                    item is AdapterEditorField field &&
                    (string.IsNullOrWhiteSpace(FilterText) ||
                     field.DisplayName.Contains(FilterText.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
                     selected!.DisplayName.Contains(FilterText.Trim(), StringComparison.CurrentCultureIgnoreCase));
            OnPropertyChanged(nameof(FieldsView));
            SelectedField = value?.Fields.FirstOrDefault(field => field.CanWrite);
        }
    }

    public AdapterEditorField? SelectedField
    {
        get => _selectedField;
        set => SetProperty(ref _selectedField, value);
    }

    public void ReplaceEntities(IEnumerable<AdapterEditorEntity> entities, string? selectedEntityId, string? selectedFieldKey)
    {
        Entities.Clear();
        foreach (var entity in entities) Entities.Add(entity);
        var previousGroup = SelectedGroup;
        GroupOptions.Clear();
        GroupOptions.Add("全部类别");
        foreach (var group in Entities.Select(item => item.Summary)
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.CurrentCulture)
                     .OrderBy(value => value, StringComparer.CurrentCulture))
            GroupOptions.Add(group);
        SelectedGroup = GroupOptions.Contains(previousGroup) ? previousGroup : "全部类别";
        EntitiesView.Refresh();
        SelectedEntity = Entities.FirstOrDefault(item =>
            string.Equals(item.EntityId, selectedEntityId, StringComparison.Ordinal)) ?? Entities.FirstOrDefault();
        if (SelectedEntity is not null && !string.IsNullOrWhiteSpace(selectedFieldKey))
            SelectedField = SelectedEntity.Fields.FirstOrDefault(item =>
                string.Equals(item.Key, selectedFieldKey, StringComparison.Ordinal)) ??
                SelectedEntity.Fields.FirstOrDefault(item => item.CanWrite);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(HasGroups));
    }
}
