using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using GameValueEditor.Models;
using GameValueEditor.Services;

internal static class ScanReadRegressionTests
{
    private static int _assertions;
    internal static async Task RunAsync()
    {
        _assertions = 0;
        var root = Path.Combine(Path.GetTempPath(), "gve-scan-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var reader = new ChangedPageReader())
            {
                var scanner = new MemoryScanService(root, _ => reader);
                var result = await scanner.InitialExactScanAsync(Environment.ProcessId,
                    [new ScanTargetDefinition(MemoryValueType.Int32, SearchRoutineIds.DirectNumeric, "test", 1,
                        BitConverter.GetBytes(ChangedPageReader.Marker))], true, false, null, CancellationToken.None);
                using var candidates = result.Candidates;
                var addresses = candidates.ReadCandidates(100).Select(candidate => candidate.Address).ToArray();
                Check(reader.DirectMarkerReadable, "Controlled marker ceased to be readable.");
                Check(reader.LargeReadFailed, "Controlled large read did not fail.");
                Check(addresses.Contains(reader.MarkerAddress), "Readable marker lost when another page in the same chunk became inaccessible.");
            }
            await CheckReadBoundariesAsync(root);
            CheckEnumerationFailures();
            Check(!Directory.EnumerateDirectories(root).Any(), "Scan regression left a candidate generation behind.");
            Console.WriteLine($"Scan read regressions passed: {_assertions} assertions (native changed page, overlap, alignment, failure and cancellation).");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _assertions++;
    }

    private static async Task CheckReadBoundariesAsync(string root)
    {
        foreach (var fallback in new[] { false, true })
        foreach (var aligned in new[] { false, true })
        foreach (var baseOffset in new[] { 0, 3 })
        {
            var reader = new FixtureReader((ulong)(0x10000 + baseOffset), 1024 * 1024 + 3 * Environment.SystemPageSize)
            { FailLargeReads = fallback };
            var targets = new[]
            {
                new ScanTargetDefinition(MemoryValueType.Int32, "int", "int", 1, BitConverter.GetBytes(0x13579BDF)),
                new ScanTargetDefinition(MemoryValueType.Int64, "long", "long", 1, BitConverter.GetBytes(0x123456789ABCDEF1L))
            };
            var expected = new HashSet<(ulong, MemoryValueType)>();
            foreach (var (offset, targetIndex) in new[] { (11, 0), (64, 0), (4094 - baseOffset, 0),
                         (8192 - baseOffset - 4, 1), (1024 * 1024 - 4, 1), (1024 * 1024 + 128, 1) })
            {
                var target = targets[targetIndex];
                target.TargetBytes.CopyTo(reader.Bytes, offset);
                var address = reader.BaseAddress + (ulong)offset;
                if (!aligned || address % 4 == 0) expected.Add((address, target.ValueType));
            }
            var result = await new MemoryScanService(root, _ => reader).InitialExactScanAsync(Environment.ProcessId,
                targets, true, aligned, null, CancellationToken.None);
            using (var candidates = result.Candidates)
            {
                var actual = candidates.ReadCandidates(100).Select(candidate => (candidate.Address, candidate.ValueType)).ToArray();
                Check(actual.Length == expected.Count && actual.ToHashSet().SetEquals(expected), "Boundary scan lost, invented or duplicated a candidate.");
                Check(candidates.Count == actual.Length, "Boundary store count differs from its records.");
                Check(reader.ReadCalls <= 600, "Page fallback read count exceeded its bounded budget.");
                Check(reader.Disposed, "Initial scan did not dispose its memory reader.");
            }
        }

        // A failed overlap must not discard values fully inside the preceding readable page.
        var hole = new FixtureReader(0x20000, Environment.SystemPageSize * 4) { BadPage = 1 };
        var marker = BitConverter.GetBytes(0x13579BDF);
        foreach (var offset in new[] { 32, Environment.SystemPageSize - 4, Environment.SystemPageSize * 2 + 32 })
            marker.CopyTo(hole.Bytes, offset);
        // Bytes spanning a bad page must not be stitched together into a false candidate.
        marker.CopyTo(hole.Bytes, Environment.SystemPageSize - 2);
        var holeRun = await Scan(root, hole, marker);
        using (var candidates = holeRun.Candidates)
        {
            var actual = candidates.ReadCandidates(100).Select(candidate => candidate.Address).ToHashSet();
            Check(actual.SetEquals(new[] { hole.BaseAddress + 32, hole.BaseAddress + (ulong)(Environment.SystemPageSize * 2 + 32) }),
                "Bad page caused an interior omission or fabricated a cross-hole value.");
        }
        var tail = new FixtureReader(0x20000, Environment.SystemPageSize * 2) { BadPage = 1 };
        marker.CopyTo(tail.Bytes, Environment.SystemPageSize - 4);
        var tailRun = await Scan(root, tail, marker);
        using (var candidates = tailRun.Candidates)
            Check(candidates.Count == 1 && candidates.ReadCandidates(1)[0].Address == tail.BaseAddress + (ulong)Environment.SystemPageSize - 4,
                "A failed overlap discarded a complete value at the readable page end.");

        var unmatched = new FixtureReader(0x20000, 4096);
        var empty = await Scan(root, unmatched, marker);
        using (var candidates = empty.Candidates) Check(candidates.Count == 0, "A genuine no-match scan cannot return zero.");
        foreach (var reader in new[] { new FixtureReader(0x20000, 8192) { FailEveryRead = true },
                     new FixtureReader(0x20000, 8192) { EmptyRegions = true } })
        {
            try { var run = await Scan(root, reader, marker); run.Candidates.Dispose(); throw new Exception("Unusable memory reported a successful empty scan."); }
            catch (IOException) { Check(reader.Disposed, "Failed scan retained its reader."); }
        }
        using var cancellation = new CancellationTokenSource();
        var cancelled = new FixtureReader(0x20000, 8192) { FailLargeReads = true, OnRead = call => { if (call == 2) cancellation.Cancel(); } };
        try { var run = await Scan(root, cancelled, marker, cancellation.Token); run.Candidates.Dispose(); throw new Exception("Page fallback ignored cancellation."); }
        catch (OperationCanceledException) { Check(cancelled.ReadCalls == 2 && cancelled.Disposed, "Cancellation performed extra reads or retained its handle."); }
    }

    private static Task<ScanRunResult> Scan(string root, FixtureReader reader, byte[] marker, CancellationToken token = default)
        => new MemoryScanService(root, _ => reader).InitialExactScanAsync(Environment.ProcessId,
            [new ScanTargetDefinition(MemoryValueType.Int32, "int", "int", 1, marker)], true, false, null, token);

    private static void CheckEnumerationFailures()
    {
        var size = (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation64>();
        MemoryRegionQuery Region(ulong address, ulong length, uint protect = 4, uint state = 0x1000) =>
            new(size, new NativeMethods.MemoryBasicInformation64 { BaseAddress = address, RegionSize = length, Protect = protect, State = state }, 0);
        foreach (var error in new[] { 5, 6, 87, 299 })
        {
            try { ProcessMemoryAccessor.EnumerateReadableRegionsCore(true, _ => new(0, default, error)); throw new Exception("Initial query error became an empty success."); }
            catch (Win32Exception exception) { Check(exception.NativeErrorCode == error, "Enumeration lost the native error code."); }
        }
        foreach (var error in new[] { 5, 6, 87 })
        {
            try { ProcessMemoryAccessor.EnumerateReadableRegionsCore(true, address => address == 0 ? Region(0, 4096) : new(0, default, error));
                throw new Exception("Mid-enumeration error became a partial success."); }
            catch (Win32Exception exception) { Check(exception.NativeErrorCode == error, "Mid-enumeration error was not preserved."); }
        }
        var ended = ProcessMemoryAccessor.EnumerateReadableRegionsCore(true, address => address == 0 ? Region(0, 0x7FFF0000) : new(0, default, 87), is32BitTarget: true);
        Check(ended.Count == 1, "Expected end of 32-bit address space was rejected.");
        var ended64 = ProcessMemoryAccessor.EnumerateReadableRegionsCore(true, address => address == 0 ? Region(0, 0x7FFFFFFF0000) : new(0, default, 87));
        Check(ended64.Count == 1, "Expected end of 64-bit address space was rejected.");
        try { ProcessMemoryAccessor.EnumerateReadableRegionsCore(true, address => address == 0 ? Region(0, 0x7FFF0000) : new(0, default, 87));
            throw new Exception("A 64-bit mid-space error was mistaken for a 32-bit end."); }
        catch (Win32Exception) { Check(true, "64-bit mid-space failure rejected."); }
        foreach (var bad in new[] { Region(0, 4096), Region(4096, ulong.MaxValue) })
        {
            try { ProcessMemoryAccessor.EnumerateReadableRegionsCore(true, address => address == 0 ? Region(0, 4096) : bad);
                throw new Exception("Non-advancing or overflowing region accepted."); }
            catch (IOException) { Check(true, "Non-advancing/overflowing region rejected."); }
        }
        foreach (var bad in new[] { Region(0, 0), Region(1, 4096), Region(0, 4096) with { Size = 1 } })
        {
            try { ProcessMemoryAccessor.EnumerateReadableRegionsCore(true, _ => bad); throw new Exception("Invalid query structure accepted."); }
            catch (IOException) { Check(true, "Invalid structure rejected."); }
        }
        foreach (var (protect, writableExpected, readableExpected) in new[] { (4u, true, true), (2u, false, true), (0x104u, false, false), (1u, false, false) })
        {
            var writable = ProcessMemoryAccessor.EnumerateReadableRegionsCore(true, address => address == 0 ? Region(0, 0x7FFFFFFF0000, protect) : new(0, default, 87));
            var readable = ProcessMemoryAccessor.EnumerateReadableRegionsCore(false, address => address == 0 ? Region(0, 0x7FFFFFFF0000, protect) : new(0, default, 87));
            Check((writable.Count == 1) == writableExpected && (readable.Count == 1) == readableExpected, "Protection filtering changed.");
        }
    }

    private sealed class FixtureReader(ulong baseAddress, int length) : IScanMemoryReader
    {
        internal byte[] Bytes { get; } = new byte[length];
        internal ulong BaseAddress => baseAddress;
        internal bool FailLargeReads { get; init; }
        internal bool FailEveryRead { get; init; }
        internal bool EmptyRegions { get; init; }
        internal int BadPage { get; init; } = -1;
        internal Action<int>? OnRead { get; init; }
        internal int ReadCalls { get; private set; }
        internal bool Disposed { get; private set; }
        public int ProcessId => Environment.ProcessId;
        public DateTime StartTimeUtc => DateTime.UnixEpoch;
        public int RegionQueryCount => 1;
        public int RegionQueryError => 0;
        public IReadOnlyList<MemoryRegion> EnumerateReadableRegions(bool writableOnly) => EmptyRegions ? [] : [new(baseAddress, (ulong)length, true)];
        public bool TryRead(ulong address, int readLength, out byte[] data)
        {
            ReadCalls++;
            OnRead?.Invoke(ReadCalls);
            var offset = (int)(address - baseAddress);
            var badStart = BadPage * Environment.SystemPageSize;
            if (FailEveryRead || (FailLargeReads && readLength > Environment.SystemPageSize + 7) ||
                (BadPage >= 0 && offset < badStart + Environment.SystemPageSize && offset + readLength > badStart))
            { data = []; return false; }
            data = Bytes.AsSpan(offset, readLength).ToArray();
            return true;
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class ChangedPageReader : IScanMemoryReader
    {
        internal const int Marker = 0x13579BDF;
        private readonly nint _allocation;
        private readonly ProcessMemoryAccessor _memory = new(Environment.ProcessId);
        private readonly int _size = Environment.SystemPageSize * 4;
        private bool _changed;
        private bool _disposed;
        internal ulong MarkerAddress => unchecked((ulong)_allocation.ToInt64()) + (ulong)(_size - 32);
        internal bool DirectMarkerReadable { get; private set; }
        internal bool LargeReadFailed { get; private set; }
        public int ProcessId => _memory.ProcessId;
        public DateTime StartTimeUtc => _memory.StartTimeUtc;
        public int RegionQueryCount => _memory.RegionQueryCount;
        public int RegionQueryError => _memory.RegionQueryError;

        internal ChangedPageReader()
        {
            _allocation = VirtualAlloc(0, (nuint)_size, 0x3000, 0x04);
            Check(_allocation != 0, "Native test allocation failed.");
            Marshal.WriteInt32(unchecked((nint)(long)MarkerAddress), Marker);
        }

        public IReadOnlyList<MemoryRegion> EnumerateReadableRegions(bool writableOnly)
        {
            var start = unchecked((ulong)_allocation.ToInt64());
            Check(_memory.EnumerateReadableRegions(true).Any(region => start >= region.BaseAddress &&
                start + (ulong)_size <= region.BaseAddress + region.RegionSize), "Native test region was not initially writable.");
            return [new MemoryRegion(start, (ulong)_size, true)];
        }

        public bool TryRead(ulong address, int length, out byte[] data)
        {
            if (!_changed)
            {
                Check(VirtualProtect(_allocation + Environment.SystemPageSize, (nuint)Environment.SystemPageSize, 0x01, out _),
                    "Native test page protection change failed.");
                _changed = true;
                DirectMarkerReadable = _memory.TryRead(MarkerAddress, sizeof(int), out var marker) && BitConverter.ToInt32(marker) == Marker;
            }
            var success = _memory.TryRead(address, length, out data);
            if (length > Environment.SystemPageSize && !success) LargeReadFailed = true;
            return success;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _memory.Dispose();
            Check(VirtualFree(_allocation, 0, 0x8000), "Native test allocation could not be released.");
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint oldProtection);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualFree(nint address, nuint size, uint freeType);
    }
}
