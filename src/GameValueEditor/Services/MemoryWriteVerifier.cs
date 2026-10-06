namespace GameValueEditor.Services;

internal interface IMemoryWriteAccess : IDisposable
{
    void EnsureInstance(int processId, DateTime startTimeUtc);
    bool TryGetModuleBase(string moduleName, out ulong baseAddress);
    bool TryRead(ulong address, int length, out byte[] data);
    bool TryWrite(ulong address, byte[] data, out string error);
}

internal enum MemoryWriteState { WriteFailed, ReadbackFailed, ReadbackMismatch, ReadbackConfirmed }

internal sealed record MemoryWriteResult(MemoryWriteState State, byte[] CurrentBytes, string Error = "")
{
    internal bool HasReadback => State is MemoryWriteState.ReadbackConfirmed or MemoryWriteState.ReadbackMismatch;
    internal string Description => State switch
    {
        MemoryWriteState.WriteFailed => $"写入失败：{Error}",
        MemoryWriteState.ReadbackFailed => "已写入，但回读失败，当前值未知",
        MemoryWriteState.ReadbackMismatch => "已写入，但回读不一致，显示实际读取值",
        _ => "已写入，回读一致；实际效果请在游戏内确认"
    };
}

internal static class MemoryWriteVerifier
{
    internal static MemoryWriteResult Write(IMemoryWriteAccess memory, ulong address, byte[] expected)
    {
        if (!memory.TryWrite(address, expected, out var error))
            return new(MemoryWriteState.WriteFailed, [], error);
        if (!memory.TryRead(address, expected.Length, out var current) || current.Length != expected.Length)
            return new(MemoryWriteState.ReadbackFailed, []);
        return new(current.AsSpan().SequenceEqual(expected)
            ? MemoryWriteState.ReadbackConfirmed : MemoryWriteState.ReadbackMismatch, current.ToArray());
    }
}
