using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Services.Adapters;

internal static class GameEditorPageResolver
{
    public static IReadOnlyList<GameEditorPageRegistration> Resolve(IGameAdapter adapter)
    {
        if (adapter is IGameEditorPageProvider provider)
            return provider.EditorPages;

        // Compatibility path for Host API 2/3 packages. API 4 modules must register
        // every page explicitly and never depend on descriptor order or ID suffixes.
        var registrations = new List<GameEditorPageRegistration>();
        var remaining = adapter.Editors.OrderBy(editor => editor.Order).ToList();

        if (adapter is IInventoryGameAdapter)
        {
            var inventory = remaining.FirstOrDefault(editor => editor.Kind == GameEditorKind.Collection);
            if (inventory is not null)
            {
                registrations.Add(new(inventory.Id, GameEditorPageRole.Inventory));
                remaining.Remove(inventory);
            }
        }

        if (adapter is ICharacterAttributesGameAdapter)
        {
            var characters = remaining.FirstOrDefault(editor => editor.Kind == GameEditorKind.MasterDetail);
            if (characters is not null)
            {
                registrations.Add(new(characters.Id, GameEditorPageRole.CharacterAttributes));
                remaining.Remove(characters);
            }
        }

        if (adapter is IEntityEditorsGameAdapter)
            registrations.AddRange(remaining.Select(editor =>
                new GameEditorPageRegistration(editor.Id, GameEditorPageRole.Entity)));

        return registrations;
    }

    public static void ValidateApi4Provider(IGameAdapter adapter)
    {
        if (adapter is not IGameEditorPageProvider provider)
            throw new InvalidOperationException("Host API 4 游戏包必须实现 IGameEditorPageProvider。");

        var duplicate = provider.EditorPages.GroupBy(page => page.EditorId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"游戏模块页面注册 ID 重复：{duplicate.Key}。");

        var editorIds = adapter.Editors.Select(editor => editor.Id).ToHashSet(StringComparer.Ordinal);
        var pageIds = provider.EditorPages.Select(page => page.EditorId).ToHashSet(StringComparer.Ordinal);
        if (!editorIds.SetEquals(pageIds))
            throw new InvalidOperationException("Host API 4 游戏包必须为每个编辑模块注册且仅注册一个页面。");

        if (provider.EditorPages.Count(page => page.Role == GameEditorPageRole.Inventory) > 1 ||
            provider.EditorPages.Count(page => page.Role == GameEditorPageRole.CharacterAttributes) > 1)
            throw new InvalidOperationException("每个游戏模块最多注册一个背包页面和一个人物属性页面。");

        foreach (var page in provider.EditorPages)
        {
            var supported = page.Role switch
            {
                GameEditorPageRole.Inventory => adapter is IInventoryGameAdapter,
                GameEditorPageRole.CharacterAttributes => adapter is ICharacterAttributesGameAdapter,
                GameEditorPageRole.Entity => adapter is IEntityEditorsGameAdapter,
                _ => false
            };
            if (!supported)
                throw new InvalidOperationException($"页面 {page.EditorId} 注册的角色与模块实现能力不一致。");
        }
    }
}
