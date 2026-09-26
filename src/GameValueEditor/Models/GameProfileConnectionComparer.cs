namespace GameValueEditor.Models;

public sealed class GameProfileConnectionComparer : System.Collections.IComparer, IComparer<GameProfile>
{
    public int Compare(object? x, object? y) => Compare(x as GameProfile, y as GameProfile);

    public int Compare(GameProfile? left, GameProfile? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return 1;
        if (right is null) return -1;
        var connected = right.IsConnected.CompareTo(left.IsConnected);
        if (connected != 0) return connected;
        var pinned = right.IsPinned.CompareTo(left.IsPinned);
        if (pinned != 0) return pinned;
        var used = right.LastUsedUtc.CompareTo(left.LastUsedUtc);
        return used != 0 ? used : string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
    }
}
