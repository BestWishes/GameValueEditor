using System.Globalization;
using System.Windows.Media;
using GameValueEditor.Infrastructure;

namespace GameValueEditor.Models;

public sealed class ProcessItem
{
    public int ProcessId { get; init; }
    public int ParentProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public string CommandLine { get; init; } = string.Empty;
    public DateTime StartTimeUtc { get; init; }
    public long WorkingSetBytes { get; init; }
    public GameProcessRole Role { get; init; }
    public GameRuntimeKind RuntimeKind { get; init; }
    public ImageSource? Icon { get; init; }

    public string DisplayName
    {
        get
        {
            var role = Role == GameProcessRole.Unknown ? string.Empty : $"  ·  {Role.DisplayName()}";
            return string.IsNullOrWhiteSpace(WindowTitle)
                ? $"{ProcessName}  ·  PID {ProcessId}{role}"
                : $"{WindowTitle}  ·  {ProcessName}  ·  PID {ProcessId}{role}";
        }
    }
}

public enum GameRuntimeKind
{
    Unknown,
    Native,
    Electron,
    NwJs,
    Unity,
    UnityMono,
    UnityIl2Cpp,
    Unreal,
    Godot
}

public enum GameProcessRole
{
    Unknown,
    Main,
    Renderer,
    Gpu,
    Network,
    Audio,
    Utility
}

public static class GameProcessRoleExtensions
{
    public static string DisplayName(this GameProcessRole role) => role switch
    {
        GameProcessRole.Main => "主进程",
        GameProcessRole.Renderer => "游戏数据",
        GameProcessRole.Gpu => "图形辅助",
        GameProcessRole.Network => "网络辅助",
        GameProcessRole.Audio => "音频辅助",
        GameProcessRole.Utility => "辅助进程",
        _ => "未知角色"
    };
}

public sealed class ScanCandidate : ObservableObject
{
    private byte[] _previousBytes = [];
    private byte[] _currentBytes = [];

    public ulong Address { get; init; }
    public byte[] FirstBytes { get; init; } = [];
    public byte[] PreviousBytes
    {
        get => _previousBytes;
        set
        {
            if (!SetProperty(ref _previousBytes, value)) return;
            OnPropertyChanged(nameof(PreviousDisplay));
        }
    }
    public byte[] CurrentBytes
    {
        get => _currentBytes;
        set
        {
            if (!SetProperty(ref _currentBytes, value)) return;
            OnPropertyChanged(nameof(CurrentDisplay));
            OnPropertyChanged(nameof(RawCurrentDisplay));
        }
    }
    public MemoryValueType ValueType { get; init; }
    public string SearchRoutineId { get; init; } = SearchRoutineIds.DirectNumeric;
    public string SearchRoutineName { get; init; } = "直接数值";
    public double ScaleMultiplier { get; init; } = 1d;

    public string AddressDisplay => $"0x{Address:X}";
    public string FirstDisplay => MemoryValueCodec.FormatDecoded(FirstBytes, ValueType, ScaleMultiplier);
    public string PreviousDisplay => MemoryValueCodec.FormatDecoded(PreviousBytes, ValueType, ScaleMultiplier);
    public string CurrentDisplay => MemoryValueCodec.FormatDecoded(CurrentBytes, ValueType, ScaleMultiplier);
    public string RawCurrentDisplay => MemoryValueCodec.Format(CurrentBytes, ValueType);
    public string RoutineDisplay => ScaleMultiplier == 1d
        ? SearchRoutineName
        : $"{SearchRoutineName} ×{ScaleMultiplier.ToString("G", CultureInfo.InvariantCulture)}";
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

    public static string FormatDecoded(byte[] bytes, MemoryValueType type, double multiplier)
    {
        if (multiplier == 0 || double.IsNaN(multiplier) || double.IsInfinity(multiplier)) return "?";
        if (Math.Abs(multiplier - 1d) < double.Epsilon) return Format(bytes, type);
        var decoded = ToDouble(bytes, type) / multiplier;
        return type is MemoryValueType.Int32 or MemoryValueType.Int64
            ? decoded.ToString("G17", CultureInfo.InvariantCulture)
            : decoded.ToString("G9", CultureInfo.InvariantCulture);
    }

    public static bool TryParseEncoded(string text, MemoryValueType type, double multiplier, out byte[] bytes)
    {
        bytes = [];
        if (multiplier <= 0 || double.IsNaN(multiplier) || double.IsInfinity(multiplier) ||
            !double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var displayValue))
        {
            return false;
        }

        var encoded = displayValue * multiplier;
        switch (type)
        {
            case MemoryValueType.Int32 when encoded >= int.MinValue && encoded <= int.MaxValue && encoded == Math.Truncate(encoded):
                bytes = BitConverter.GetBytes((int)encoded);
                return true;
            case MemoryValueType.Int64 when encoded >= long.MinValue && encoded <= long.MaxValue && encoded == Math.Truncate(encoded):
                bytes = BitConverter.GetBytes((long)encoded);
                return true;
            case MemoryValueType.Float when encoded is >= -float.MaxValue and <= float.MaxValue:
                bytes = BitConverter.GetBytes((float)encoded);
                return true;
            case MemoryValueType.Double:
                bytes = BitConverter.GetBytes(encoded);
                return true;
            default:
                return false;
        }
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
