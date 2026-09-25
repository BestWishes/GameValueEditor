using System.Diagnostics;
using System.Runtime.InteropServices;
using GameValueEditor.Models;
using GameValueEditor.Services;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

try
{
    Assert(MemoryValueCodec.TryParse("123456", MemoryValueType.Int32, out var integerBytes), "Int32 parse failed");
    Assert(MemoryValueCodec.Format(integerBytes, MemoryValueType.Int32) == "123456", "Int32 roundtrip failed");
    Assert(MemoryValueCodec.TryParse("1.95", MemoryValueType.Double, out var doubleBytes), "Double parse failed");
    Assert(Math.Abs(BitConverter.ToDouble(doubleBytes) - 1.95) < 0.0000001, "Double roundtrip failed");

    const int marker = 0x13579BDF;
    const int replacement = 0x2468ACE;
    var payload = new byte[128];
    BitConverter.GetBytes(marker).CopyTo(payload, 19);
    var handle = GCHandle.Alloc(payload, GCHandleType.Pinned);
    try
    {
        var expectedAddress = unchecked((ulong)handle.AddrOfPinnedObject().ToInt64()) + 19;
        var scanner = new MemoryScanService();
        var scanResult = await scanner.InitialExactScanAsync(
            Environment.ProcessId,
            MemoryValueType.Int32,
            BitConverter.GetBytes(marker),
            writableOnly: true,
            alignedOnly: false,
            progress: null,
            CancellationToken.None);
        Assert(scanResult.Candidates.Any(candidate => candidate.Address == expectedAddress), "Pinned marker was not found by memory scan");

        using var memory = new ProcessMemoryAccessor(Environment.ProcessId);
        Assert(memory.TryWrite(expectedAddress, BitConverter.GetBytes(replacement), out var error), $"Memory write failed: {error}");
        Assert(BitConverter.ToInt32(payload, 19) == replacement, "Memory write did not update the target value");
    }
    finally
    {
        handle.Free();
    }

    var executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
    Assert(!string.IsNullOrWhiteSpace(executable), "Unable to resolve test executable path");
    var fingerprint = await new VersionFingerprintService().CreateAsync(executable!);
    Assert(fingerprint.Sha256.Length == 64, "SHA-256 fingerprint is invalid");
    Assert(fingerprint.FileSize > 0, "Executable size is invalid");

    Console.WriteLine("Smoke tests passed: codec, scanner, writer, fingerprint.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
