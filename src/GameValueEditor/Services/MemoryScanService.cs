using System.IO;
using GameValueEditor.Models;

namespace GameValueEditor.Services;

public sealed class MemoryScanService
{
    private const int ChunkSize = 1024 * 1024;
    private readonly string _scanRootDirectory;

    public MemoryScanService(string? scanRootDirectory = null)
    {
        _scanRootDirectory = Path.GetFullPath(scanRootDirectory ?? Path.Combine(Path.GetTempPath(), "GameValueEditor", "scan-temp"));
        Directory.CreateDirectory(_scanRootDirectory);
        CleanupStaleStores();
    }

    public Task<ScanRunResult> InitialExactScanAsync(
        int processId,
        IReadOnlyList<ScanTargetDefinition> targets,
        bool writableOnly,
        bool alignedOnly,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (targets.Count == 0) throw new ArgumentException("至少需要一个扫描目标。", nameof(targets));
        var store = CreateStore(targets);
        try
        {
            using var memory = new ProcessMemoryAccessor(processId);
            var regions = memory.EnumerateReadableRegions(writableOnly);
            var total = regions.Aggregate<MemoryRegion, ulong>(0, (current, region) => current + region.RegionSize);
            var maximumValueSize = targets.Max(target => target.ValueType.Size());
            var writers = store.Partitions.Select(partition => new BinaryWriter(new FileStream(
                partition.FilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan))).ToArray();
            try
            {
                ulong completed = 0;
                long resultCount = 0;
                foreach (var region in regions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ulong position = 0;
                    while (position < region.RegionSize)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var payloadLength = (int)Math.Min((ulong)ChunkSize, region.RegionSize - position);
                        var readLength = (int)Math.Min(
                            (ulong)(payloadLength + maximumValueSize - 1),
                            region.RegionSize - position);
                        var chunkAddress = region.BaseAddress + position;

                        if (memory.TryRead(chunkAddress, readLength, out var buffer))
                        {
                            for (var targetIndex = 0; targetIndex < targets.Count; targetIndex++)
                            {
                                var target = targets[targetIndex];
                                var valueSize = target.ValueType.Size();
                                var alignment = alignedOnly ? Math.Min(valueSize, 4) : 1;
                                var firstOffset = alignedOnly
                                    ? (int)((ulong)alignment - (chunkAddress % (ulong)alignment)) % alignment
                                    : 0;
                                var maxOffset = Math.Min(payloadLength - 1, buffer.Length - valueSize);
                                var writer = writers[targetIndex];
                                var partition = store.Partitions[targetIndex];
                                for (var offset = firstOffset; offset <= maxOffset; offset += alignment)
                                {
                                    if (!buffer.AsSpan(offset, valueSize).SequenceEqual(target.TargetBytes)) continue;
                                    writer.Write(chunkAddress + (ulong)offset);
                                    writer.Write(target.TargetBytes);
                                    writer.Write(target.TargetBytes);
                                    partition.Count++;
                                    resultCount++;
                                }
                            }
                        }

                        position += (ulong)payloadLength;
                        if ((position & 0x7FFFFF) == 0)
                            progress?.Report(new ScanProgress(completed + position, total, resultCount));
                    }
                    completed += region.RegionSize;
                    progress?.Report(new ScanProgress(completed, total, resultCount));
                }

                return new ScanRunResult(store, completed);
            }
            finally
            {
                foreach (var writer in writers) writer.Dispose();
            }
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }, cancellationToken);

    public Task<ScanRunResult> NextScanAsync(
        int processId,
        ScanCandidateStore source,
        ScanComparison comparison,
        Func<ScanCandidatePartition, byte[]?> exactTargetFactory,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        var targets = source.Partitions.Select(partition => new ScanTargetDefinition(
            partition.ValueType,
            partition.SearchRoutineId,
            partition.SearchRoutineName,
            partition.ScaleMultiplier,
            partition.FirstBytes.ToArray())).ToList();
        var destination = CreateStore(targets);
        try
        {
            using var memory = new ProcessMemoryAccessor(processId);
            long processed = 0;
            long resultCount = 0;
            for (var partitionIndex = 0; partitionIndex < source.Partitions.Count; partitionIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePartition = source.Partitions[partitionIndex];
                var destinationPartition = destination.Partitions[partitionIndex];
                var exactTarget = comparison == ScanComparison.Exact
                    ? exactTargetFactory(sourcePartition)
                    : null;
                var valueSize = sourcePartition.ValueType.Size();
                using var input = new FileStream(
                    sourcePartition.FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.SequentialScan);
                using var reader = new BinaryReader(input);
                using var writer = new BinaryWriter(new FileStream(
                    destinationPartition.FilePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.SequentialScan));

                ulong cachedPageAddress = ulong.MaxValue;
                byte[] cachedPage = [];
                while (input.Position < input.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var address = reader.ReadUInt64();
                    input.Seek(valueSize, SeekOrigin.Current); // The source generation's previous value is not needed.
                    var previous = reader.ReadBytes(valueSize);
                    if (previous.Length != valueSize)
                        throw new InvalidDataException("扫描候选临时文件不完整。");

                    var pageAddress = address & ~0xFFFUL;
                    if (pageAddress != cachedPageAddress)
                    {
                        cachedPageAddress = pageAddress;
                        memory.TryRead(pageAddress, 4096, out cachedPage);
                    }

                    var pageOffset = (int)(address - pageAddress);
                    byte[] current;
                    if (cachedPage.Length >= pageOffset + valueSize)
                    {
                        current = cachedPage.AsSpan(pageOffset, valueSize).ToArray();
                    }
                    else if (!memory.TryRead(address, valueSize, out current))
                    {
                        processed++;
                        continue;
                    }

                    if (Matches(previous, current, sourcePartition.ValueType, comparison, exactTarget))
                    {
                        writer.Write(address);
                        writer.Write(previous);
                        writer.Write(current);
                        destinationPartition.Count++;
                        resultCount++;
                    }

                    processed++;
                    if ((processed & 0x3FFF) == 0)
                        progress?.Report(new ScanProgress((ulong)processed, (ulong)source.Count, resultCount));
                }
            }

            progress?.Report(new ScanProgress((ulong)source.Count, (ulong)source.Count, resultCount));
            return new ScanRunResult(destination, (ulong)source.Count);
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }, cancellationToken);

    private ScanCandidateStore CreateStore(IReadOnlyList<ScanTargetDefinition> targets)
    {
        var storeDirectory = Path.Combine(_scanRootDirectory, $"scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storeDirectory);
        var lease = new FileStream(
            Path.Combine(storeDirectory, "store.lock"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None);
        try
        {
            var partitions = targets.Select((target, index) => new ScanCandidatePartition
            {
                FilePath = Path.Combine(storeDirectory, $"part-{index:D3}.bin"),
                ValueType = target.ValueType,
                SearchRoutineId = target.SearchRoutineId,
                SearchRoutineName = target.SearchRoutineName,
                ScaleMultiplier = target.ScaleMultiplier,
                FirstBytes = target.TargetBytes.ToArray()
            }).ToList();
            return new ScanCandidateStore(_scanRootDirectory, storeDirectory, partitions, lease);
        }
        catch
        {
            lease.Dispose();
            try { Directory.Delete(storeDirectory, recursive: true); }
            catch { }
            throw;
        }
    }

    private void CleanupStaleStores()
    {
        try
        {
            var root = Path.GetFullPath(_scanRootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var directory in Directory.EnumerateDirectories(_scanRootDirectory, "scan-*", SearchOption.TopDirectoryOnly))
            {
                var fullPath = Path.GetFullPath(directory);
                if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                var lastWrite = Directory.GetLastWriteTimeUtc(fullPath);
                if (DateTime.UtcNow - lastWrite < TimeSpan.FromDays(1)) continue;
                FileStream? leaseProbe = null;
                try
                {
                    leaseProbe = new FileStream(
                        Path.Combine(fullPath, "store.lock"),
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None);
                }
                catch (IOException)
                {
                    // Another application instance still owns this scan generation.
                    continue;
                }
                catch
                {
                    continue;
                }

                leaseProbe.Dispose();
                try { Directory.Delete(fullPath, recursive: true); }
                catch { }
            }
        }
        catch
        {
            // Temporary-store cleanup must never prevent application startup.
        }
    }

    private static bool Matches(
        ReadOnlySpan<byte> previous,
        ReadOnlySpan<byte> current,
        MemoryValueType valueType,
        ScanComparison comparison,
        byte[]? exactTarget) => comparison switch
        {
            ScanComparison.Exact => exactTarget is not null && current.SequenceEqual(exactTarget),
            ScanComparison.Changed => !current.SequenceEqual(previous),
            ScanComparison.Unchanged => current.SequenceEqual(previous),
            ScanComparison.Increased => ToDouble(current, valueType) > ToDouble(previous, valueType),
            ScanComparison.Decreased => ToDouble(current, valueType) < ToDouble(previous, valueType),
            _ => false
        };

    private static double ToDouble(ReadOnlySpan<byte> bytes, MemoryValueType valueType) => valueType switch
    {
        MemoryValueType.Int32 => BitConverter.ToInt32(bytes),
        MemoryValueType.Int64 => BitConverter.ToInt64(bytes),
        MemoryValueType.Float => BitConverter.ToSingle(bytes),
        MemoryValueType.Double => BitConverter.ToDouble(bytes),
        _ => double.NaN
    };
}

public sealed record ScanProgress(ulong CompletedBytes, ulong TotalBytes, long ResultCount)
{
    public double Percentage => TotalBytes == 0 ? 0 : Math.Clamp(CompletedBytes * 100d / TotalBytes, 0, 100);
}

public sealed record ScanRunResult(ScanCandidateStore Candidates, ulong ScannedBytes);
