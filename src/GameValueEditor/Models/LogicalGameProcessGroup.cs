namespace GameValueEditor.Models;

/// <summary>
/// A logical game instance. Multi-process engines may expose many selectable processes,
/// but memory editing is always routed to the authoritative data process.
/// </summary>
public sealed class LogicalGameProcessGroup
{
    public required ProcessItem SeedProcess { get; init; }
    public required ProcessItem RootProcess { get; init; }
    public required ProcessItem DataProcess { get; init; }
    public required IReadOnlyList<ProcessItem> Members { get; init; }
    public required GameRuntimeKind RuntimeKind { get; init; }

    public bool Contains(ProcessItem process) => Members.Any(member =>
        member.ProcessId == process.ProcessId && member.StartTimeUtc == process.StartTimeUtc);

    public bool IsSameInstance(LogicalGameProcessGroup other) =>
        RootProcess.ProcessId == other.RootProcess.ProcessId &&
        RootProcess.StartTimeUtc == other.RootProcess.StartTimeUtc;
}
