namespace GameValueEditor.Services;

// Internal seam for read-only fault tests; not part of the module contract.
internal interface IScanMemoryReader : IDisposable
{
    int ProcessId { get; }
    DateTime StartTimeUtc { get; }
    int RegionQueryCount { get; }
    int RegionQueryError { get; }
    IReadOnlyList<MemoryRegion> EnumerateReadableRegions(bool writableOnly);
    bool TryRead(ulong address, int length, out byte[] data);
}
