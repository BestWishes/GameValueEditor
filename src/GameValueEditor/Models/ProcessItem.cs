namespace GameValueEditor.Models;

public sealed class ProcessItem
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public DateTime StartTimeUtc { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(WindowTitle)
        ? $"{ProcessName}  ·  PID {ProcessId}"
        : $"{WindowTitle}  ·  {ProcessName}  ·  PID {ProcessId}";
}

public sealed class ScanCandidate
{
    public ulong Address { get; init; }
    public byte[] PreviousBytes { get; set; } = [];
    public byte[] CurrentBytes { get; set; } = [];
    public MemoryValueType ValueType { get; init; }

    public string AddressDisplay => $"0x{Address:X}";
    public string PreviousDisplay => MemoryValueCodec.Format(PreviousBytes, ValueType);
    public string CurrentDisplay => MemoryValueCodec.Format(CurrentBytes, ValueType);
}

public static class MemoryValueCodec
{
    public static bool TryParse(string text, MemoryValueType type, out byte[] bytes)
    {
        bytes = [];
        text = text.Trim();
        switch (type)
        {
            case MemoryValueType.Int32 when int.TryParse(text, out var int32):
                bytes = BitConverter.GetBytes(int32);
                return true;
            case MemoryValueType.Int64 when long.TryParse(text, out var int64):
                bytes = BitConverter.GetBytes(int64);
                return true;
            case MemoryValueType.Float when float.TryParse(text, out var single):
                bytes = BitConverter.GetBytes(single);
                return true;
            case MemoryValueType.Double when double.TryParse(text, out var dbl):
                bytes = BitConverter.GetBytes(dbl);
                return true;
            default:
                return false;
        }
    }

    public static string Format(byte[] bytes, MemoryValueType type)
    {
        if (bytes.Length < type.Size()) return "?";
        return type switch
        {
            MemoryValueType.Int32 => BitConverter.ToInt32(bytes).ToString(),
            MemoryValueType.Int64 => BitConverter.ToInt64(bytes).ToString(),
            MemoryValueType.Float => BitConverter.ToSingle(bytes).ToString("G9"),
            MemoryValueType.Double => BitConverter.ToDouble(bytes).ToString("G17"),
            _ => "?"
        };
    }

    public static double ToDouble(byte[] bytes, MemoryValueType type) => type switch
    {
        MemoryValueType.Int32 => BitConverter.ToInt32(bytes),
        MemoryValueType.Int64 => BitConverter.ToInt64(bytes),
        MemoryValueType.Float => BitConverter.ToSingle(bytes),
        MemoryValueType.Double => BitConverter.ToDouble(bytes),
        _ => double.NaN
    };
}
