using System.IO;
using GameValueEditor.Models;

namespace GameValueEditor.Services;

/// <summary>
/// Compact, disk-backed storage for a complete scan generation. The UI may
/// materialize only a small preview while later scans continue to process every
/// candidate address.
/// </summary>
public sealed class ScanCandidateStore : IDisposable
{
    private readonly string _rootDirectory;
    private readonly string _storeDirectory;
    private readonly FileStream _lease;
    private bool _disposed;

    internal ScanCandidateStore(
        string rootDirectory,
        string storeDirectory,
        IReadOnlyList<ScanCandidatePartition> partitions,
        FileStream lease)
    {
        _rootDirectory = rootDirectory;
        _storeDirectory = storeDirectory;
        Partitions = partitions;
        _lease = lease;
    }

    public IReadOnlyList<ScanCandidatePartition> Partitions { get; }
    public long Count => Partitions.Sum(partition => partition.Count);

    public IReadOnlyList<ScanCandidate> ReadCandidates(int maximumCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (maximumCount <= 0 || Count == 0) return [];

        var result = new List<ScanCandidate>((int)Math.Min(Count, maximumCount));
        foreach (var partition in Partitions)
        {
            using var stream = new FileStream(
                partition.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            using var reader = new BinaryReader(stream);
            var valueSize = partition.ValueType.Size();
            while (stream.Position < stream.Length && result.Count < maximumCount)
            {
                var address = reader.ReadUInt64();
                var previous = reader.ReadBytes(valueSize);
                var current = reader.ReadBytes(valueSize);
                if (previous.Length != valueSize || current.Length != valueSize)
                    throw new InvalidDataException("扫描候选临时文件不完整。");
                result.Add(new ScanCandidate
                {
                    Address = address,
                    FirstBytes = partition.FirstBytes.ToArray(),
                    PreviousBytes = previous,
                    CurrentBytes = current,
                    ValueType = partition.ValueType,
                    SearchRoutineId = partition.SearchRoutineId,
                    SearchRoutineName = partition.SearchRoutineName,
                    ScaleMultiplier = partition.ScaleMultiplier
                });
            }

            if (result.Count >= maximumCount) break;
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lease.Dispose();
        try
        {
            var root = Path.GetFullPath(_rootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var store = Path.GetFullPath(_storeDirectory);
            if (!store.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(store)) return;
            Directory.Delete(store, recursive: true);
        }
        catch
        {
            // A terminated scan or antivirus may briefly hold a file. Stale scan
            // directories are retried during a later application startup.
        }
    }
}

public sealed class ScanCandidatePartition
{
    internal string FilePath { get; init; } = string.Empty;
    public required MemoryValueType ValueType { get; init; }
    public required string SearchRoutineId { get; init; }
    public required string SearchRoutineName { get; init; }
    public required double ScaleMultiplier { get; init; }
    public required byte[] FirstBytes { get; init; }
    public long Count { get; internal set; }
}

public sealed record ScanTargetDefinition(
    MemoryValueType ValueType,
    string SearchRoutineId,
    string SearchRoutineName,
    double ScaleMultiplier,
    byte[] TargetBytes);
