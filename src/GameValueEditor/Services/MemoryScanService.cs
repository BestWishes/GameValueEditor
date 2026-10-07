using System.IO;
using GameValueEditor.Models;

namespace GameValueEditor.Services;

public sealed class MemoryScanService
{
    private const int ChunkSize = 1024 * 1024;
    private readonly string _scanRootDirectory;
    private readonly Func<int, IScanMemoryReader> _openScanMemory;

    public MemoryScanService(string? scanRootDirectory = null)
        : this(scanRootDirectory, processId => new ProcessMemoryAccessor(processId)) { }

    internal MemoryScanService(string? scanRootDirectory, Func<int, IScanMemoryReader> openScanMemory)
    {
        _openScanMemory = openScanMemory;
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
            using var memory = _openScanMemory(processId);
            store.ProcessId = memory.ProcessId;
            store.ProcessStartTimeUtc = memory.StartTimeUtc;
            var regions = memory.EnumerateReadableRegions(writableOnly);
            if (regions.Count == 0)
                throw new IOException("未找到可读取的扫描区域，请检查目标进程并重新连接。");
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
                long readAttempts = 0;
                long successfulReads = 0;
                bool TryRead(ulong address, int length, out byte[] bytes)
                {
                    readAttempts++;
                    if (!memory.TryRead(address, length, out bytes)) return false;
                    successfulReads++;
                    return true;
                }

                void ScanBuffer(ulong address, int payloadLength, byte[] buffer)
                {
                    for (var targetIndex = 0; targetIndex < targets.Count; targetIndex++)
                    {
                        var target = targets[targetIndex];
                        var valueSize = target.ValueType.Size();
                        var alignment = alignedOnly ? Math.Min(valueSize, 4) : 1;
                        var firstOffset = alignedOnly
                            ? (int)((ulong)alignment - (address % (ulong)alignment)) % alignment
                            : 0;
                        var maxOffset = Math.Min(payloadLength - 1, buffer.Length - valueSize);
                        var writer = writers[targetIndex];
                        var partition = store.Partitions[targetIndex];
                        for (var offset = firstOffset; offset <= maxOffset; offset += alignment)
                        {
                            if (!buffer.AsSpan(offset, valueSize).SequenceEqual(target.TargetBytes)) continue;
                            writer.Write(address + (ulong)offset);
                            writer.Write(target.TargetBytes);
                            writer.Write(target.TargetBytes);
                            partition.Count++;
                            resultCount++;
                        }
                    }
                }
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

                        if (TryRead(chunkAddress, readLength, out var buffer))
                        {
                            ScanBuffer(chunkAddress, payloadLength, buffer);
                        }
                        else
                        {
                            // A region can change after enumeration. Recover only readable pages,
                            // never concatenate bytes across a failed page or retry the whole scan.
                            for (var pageOffset = 0; pageOffset < payloadLength;)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var pageAddress = chunkAddress + (ulong)pageOffset;
                                var pagePayload = Math.Min(payloadLength - pageOffset,
                                    Environment.SystemPageSize - (int)(pageAddress % (ulong)Environment.SystemPageSize));
                                var pageReadLength = (int)Math.Min((ulong)(pagePayload + maximumValueSize - 1),
                                    region.RegionSize - position - (ulong)pageOffset);
                                if (TryRead(pageAddress, pageReadLength, out var pageBuffer) ||
                                    (pageReadLength > pagePayload && TryRead(pageAddress, pagePayload, out pageBuffer)))
                                    ScanBuffer(pageAddress, pagePayload, pageBuffer);
                                pageOffset += pagePayload;
                            }
                        }

                        position += (ulong)payloadLength;
                        if ((position & 0x7FFFFF) == 0)
                            progress?.Report(new ScanProgress(completed + position, total, resultCount));
                    }
                    completed += region.RegionSize;
                    progress?.Report(new ScanProgress(completed, total, resultCount));
                }

                if (successfulReads == 0)
                    throw new IOException("扫描区域均无法读取，目标进程可能已退出或内存权限已变化，请重新连接后再试。");
                return new ScanRunResult(store, completed)
                {
                    Diagnostics = new ScanDiagnostics(regions.Count, memory.RegionQueryCount,
                        memory.RegionQueryError, readAttempts, successfulReads)
                };
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

    public async Task<ScanRunResult> NextScanAsync(
        int processId,
        ScanCandidateStore source,
        ScanComparison comparison,
        Func<ScanCandidatePartition, byte[]?> exactTargetFactory,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var sourceLease = source.AcquireReadLease();
        return await Task.Run(() =>
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
            using var memory = _openScanMemory(processId);
            if (processId != source.ProcessId || memory.ProcessId != source.ProcessId ||
                memory.StartTimeUtc != source.ProcessStartTimeUtc)
                throw new InvalidOperationException("目标进程实例已变化，请重新连接并扫描。");
            destination.ProcessId = memory.ProcessId;
            destination.ProcessStartTimeUtc = memory.StartTimeUtc;
            long processed = 0;
            long resultCount = 0;
            long readableCandidates = 0;
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
                        if (!memory.TryRead(pageAddress, 4096, out cachedPage)) cachedPage = [];
                    }

                    var pageOffset = (int)(address - pageAddress);
                    byte[] current;
                    if (cachedPage.Length >= pageOffset + valueSize)
                    {
                        current = cachedPage.AsSpan(pageOffset, valueSize).ToArray();
                    }
                    else if (!memory.TryRead(address, valueSize, out current) || current.Length != valueSize)
                    {
                        processed++;
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    readableCandidates++;
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

            cancellationToken.ThrowIfCancellationRequested();
            if (source.Count > 0 && readableCandidates == 0)
                throw new IOException("本次筛选无法读取任何候选地址，已保留上一轮扫描结果。请检查游戏进程或重新扫描。");
            progress?.Report(new ScanProgress((ulong)source.Count, (ulong)source.Count, resultCount));
            return new ScanRunResult(destination, (ulong)source.Count);
        }
        catch
        {
            destination.Dispose();
            throw;
        }
        }, cancellationToken).ConfigureAwait(false);
    }

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
            ScanComparison.Increased => Compare(current, previous, valueType) > 0,
            ScanComparison.Decreased => Compare(current, previous, valueType) < 0,
            _ => false
        };

    private static int Compare(ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous, MemoryValueType valueType) => valueType switch
    {
        MemoryValueType.Int32 => BitConverter.ToInt32(current).CompareTo(BitConverter.ToInt32(previous)),
        MemoryValueType.Int64 => BitConverter.ToInt64(current).CompareTo(BitConverter.ToInt64(previous)),
        MemoryValueType.Float => CompareFloating(BitConverter.ToSingle(current), BitConverter.ToSingle(previous)),
        MemoryValueType.Double => CompareFloating(BitConverter.ToDouble(current), BitConverter.ToDouble(previous)),
        _ => 0
    };

    private static int CompareFloating(double current, double previous) =>
        double.IsNaN(current) || double.IsNaN(previous) ? 0 : current.CompareTo(previous);
}

public sealed record ScanProgress(ulong CompletedBytes, ulong TotalBytes, long ResultCount)
{
    public double Percentage => TotalBytes == 0 ? 0 : Math.Clamp(CompletedBytes * 100d / TotalBytes, 0, 100);
}

public sealed record ScanRunResult(ScanCandidateStore Candidates, ulong ScannedBytes)
{
    internal ScanDiagnostics? Diagnostics { get; init; }
}

internal sealed record ScanDiagnostics(int RegionCount, int QueryCount, int QueryError, long ReadAttempts, long SuccessfulReads);
