namespace GameValueEditor.Models;

public enum GameLibrarySortMode { Connection, Name, LocalModule }

public sealed class GameProfileConnectionComparer(GameLibrarySortMode mode = GameLibrarySortMode.Connection) : System.Collections.IComparer, IComparer<GameProfile>
{
    public int Compare(object? x, object? y) => Compare(x as GameProfile, y as GameProfile);

    public int Compare(GameProfile? left, GameProfile? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return 1;
        if (right is null) return -1;
        if (mode == GameLibrarySortMode.Connection)
        {
            var connected = right.IsConnected.CompareTo(left.IsConnected);
            if (connected != 0) return connected;
        }
        if (mode == GameLibrarySortMode.LocalModule)
        {
            var module = right.IsModuleInstalled.CompareTo(left.IsModuleInstalled);
            if (module != 0) return module;
        }
        var pinned = right.IsPinned.CompareTo(left.IsPinned);
        if (pinned != 0) return pinned;
        if (mode is GameLibrarySortMode.Name or GameLibrarySortMode.LocalModule)
            return string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
        var used = right.LastUsedUtc.CompareTo(left.LastUsedUtc);
        return used != 0 ? used : string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
    }
}
