using System.Collections.ObjectModel;
using System.Reflection;
using GameValueEditor.Models;

namespace GameValueEditor.Services;

// A connection can enrich old metadata and migrate semantic fields before its
// durable save. Keep the original objects, not a deserialized replacement library.
internal sealed class GameConnectionProfileCheckpoint
{
    private static readonly PropertyInfo[] VersionProperties = typeof(GameVersionProfile).GetProperties()
        .Where(property => property.CanRead && property.CanWrite && property.Name != nameof(GameVersionProfile.Fields))
        .ToArray();
    private readonly GameProfile _game;
    private readonly GameIdentityEvidence? _identity;
    private readonly string _path;
    private readonly string _processName;
    private readonly DateTime _lastUsed;
    private readonly ObservableCollection<GameVersionProfile> _collection;
    private readonly VersionState[] _versions;

    internal GameConnectionProfileCheckpoint(GameProfile game)
    {
        _game = game; _identity = game.Identity; _path = game.ExecutablePath;
        _processName = game.ProcessName; _lastUsed = game.LastUsedUtc; _collection = game.Versions;
        _versions = game.Versions.Select(version => new VersionState(version,
            VersionProperties.Select(property => property.GetValue(version)).ToArray(), version.Fields,
            version.Fields.ToArray())).ToArray();
    }

    internal void Restore()
    {
        _game.Identity = _identity; _game.ExecutablePath = _path; _game.ProcessName = _processName; _game.LastUsedUtc = _lastUsed;
        _game.Versions = _collection;
        RestoreMembers(_collection, _versions.Select(state => state.Version).ToArray());
        foreach (var state in _versions)
        {
            for (var index = 0; index < VersionProperties.Length; index++)
                VersionProperties[index].SetValue(state.Version, state.Values[index]);
            state.Version.Fields = state.Fields;
            RestoreMembers(state.Fields, state.Members);
            state.Version.NotifyChoiceChanged();
        }
        _game.NotifySummaryChanged();
    }

    private static void RestoreMembers<T>(ObservableCollection<T> collection, T[] members) where T : class
    {
        foreach (var added in collection.Where(item => !members.Contains(item)).ToArray()) collection.Remove(added);
        for (var index = 0; index < members.Length; index++)
        {
            var position = collection.IndexOf(members[index]);
            if (position < 0) collection.Insert(index, members[index]);
            else if (position != index) collection.Move(position, index);
        }
    }

    private sealed record VersionState(GameVersionProfile Version, object?[] Values,
        ObservableCollection<SavedField> Fields, SavedField[] Members);
}
