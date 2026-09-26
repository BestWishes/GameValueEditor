using GameValueEditor.Models;

namespace GameValueEditor.Services;

public sealed class MemoryScanService
{
    private const int ChunkSize = 1024 * 1024;
    private const int ResultLimit = 250_000;

    public Task<ScanRunResult> InitialExactScanAsync(
        int processId,
        MemoryValueType valueType,
        byte[] target,
        bool writableOnly,
        bool alignedOnly,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var memory = new ProcessMemoryAccessor(processId);
        var regions = memory.EnumerateReadableRegions(writableOnly);
        var total = regions.Aggregate<MemoryRegion, ulong>(0, (current, region) => current + region.RegionSize);
        ulong completed = 0;
        var results = new List<ScanCandidate>();
        var size = valueType.Size();
        var alignment = alignedOnly ? Math.Min(size, 4) : 1;

        foreach (var region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong position = 0;
            while (position < region.RegionSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payloadLength = (int)Math.Min((ulong)ChunkSize, region.RegionSize - position);
                var readLength = (int)Math.Min((ulong)(payloadLength + size - 1), region.RegionSize - position);
                var chunkAddress = region.BaseAddress + position;

                if (memory.TryRead(chunkAddress, readLength, out var buffer))
                {
                    var firstOffset = alignedOnly
                        ? (int)((ulong)alignment - (chunkAddress % (ulong)alignment)) % alignment
                        : 0;
                    var maxOffset = Math.Min(payloadLength - 1, buffer.Length - size);
                    for (var offset = firstOffset; offset <= maxOffset; offset += alignment)
                    {
                        if (!buffer.AsSpan(offset, size).SequenceEqual(target)) continue;
                        results.Add(new ScanCandidate
                        {
                            Address = chunkAddress + (ulong)offset,
                            FirstBytes = target.ToArray(),
                            PreviousBytes = target.ToArray(),
                            CurrentBytes = target.ToArray(),
                            ValueType = valueType
                        });
                        if (results.Count >= ResultLimit)
                        {
                            return new ScanRunResult(results, true, completed + position);
                        }
                    }
                }

                position += (ulong)payloadLength;
                if ((position & 0x7FFFFF) == 0)
                {
                    progress?.Report(new ScanProgress(completed + position, total, results.Count));
                }
            }
            completed += region.RegionSize;
            progress?.Report(new ScanProgress(completed, total, results.Count));
        }

        return new ScanRunResult(results, false, completed);
    }, cancellationToken);

    public Task<ScanRunResult> NextScanAsync(
        int processId,
        IReadOnlyCollection<ScanCandidate> candidates,
        MemoryValueType valueType,
        ScanComparison comparison,
        byte[]? exactTarget,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken) => NextScanAsync(
            processId,
            candidates,
            comparison,
            candidate => candidate.ValueType == valueType ? exactTarget : null,
            progress,
            cancellationToken);

    public Task<ScanRunResult> NextScanAsync(
        int processId,
        IReadOnlyCollection<ScanCandidate> candidates,
        ScanComparison comparison,
        Func<ScanCandidate, byte[]?> exactTargetFactory,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var memory = new ProcessMemoryAccessor(processId);
        var results = new List<ScanCandidate>();
        var groups = candidates.GroupBy(candidate => candidate.Address & ~0xFFFUL).ToList();
        var processed = 0;

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            memory.TryRead(group.Key, 4096, out var page);

            foreach (var candidate in group)
            {
                var offset = (int)(candidate.Address - group.Key);
                var valueType = candidate.ValueType;
                byte[] current;
                if (page.Length >= offset + valueType.Size())
                {
                    current = page.AsSpan(offset, valueType.Size()).ToArray();
                }
                else if (!memory.TryRead(candidate.Address, valueType.Size(), out current))
                {
                    continue;
                }

                if (!Matches(candidate.CurrentBytes, current, valueType, comparison, exactTargetFactory(candidate))) continue;
                results.Add(new ScanCandidate
                {
                    Address = candidate.Address,
                    FirstBytes = candidate.FirstBytes.ToArray(),
                    PreviousBytes = candidate.CurrentBytes.ToArray(),
                    CurrentBytes = current,
                    ValueType = valueType,
                    SearchRoutineId = candidate.SearchRoutineId,
                    SearchRoutineName = candidate.SearchRoutineName,
                    ScaleMultiplier = candidate.ScaleMultiplier
                });
            }

            processed += group.Count();
            if ((processed & 0x3FFF) == 0)
            {
                progress?.Report(new ScanProgress((ulong)processed, (ulong)candidates.Count, results.Count));
            }
        }

        progress?.Report(new ScanProgress((ulong)candidates.Count, (ulong)candidates.Count, results.Count));
        return new ScanRunResult(results, false, (ulong)candidates.Count);
    }, cancellationToken);

    private static bool Matches(
        byte[] previous,
        byte[] current,
        MemoryValueType valueType,
        ScanComparison comparison,
        byte[]? exactTarget) => comparison switch
        {
            ScanComparison.Exact => exactTarget is not null && current.AsSpan().SequenceEqual(exactTarget),
            ScanComparison.Changed => !current.AsSpan().SequenceEqual(previous),
            ScanComparison.Unchanged => current.AsSpan().SequenceEqual(previous),
            ScanComparison.Increased => MemoryValueCodec.ToDouble(current, valueType) > MemoryValueCodec.ToDouble(previous, valueType),
            ScanComparison.Decreased => MemoryValueCodec.ToDouble(current, valueType) < MemoryValueCodec.ToDouble(previous, valueType),
            _ => false
        };
}

public sealed record ScanProgress(ulong CompletedBytes, ulong TotalBytes, int ResultCount)
{
    public double Percentage => TotalBytes == 0 ? 0 : Math.Clamp(CompletedBytes * 100d / TotalBytes, 0, 100);
}

public sealed record ScanRunResult(IReadOnlyList<ScanCandidate> Candidates, bool Truncated, ulong ScannedBytes);
