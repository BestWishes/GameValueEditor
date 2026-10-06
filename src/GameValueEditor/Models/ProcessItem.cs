using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
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
            return string.IsNullOrWhiteSpace(WindowTitle)
                ? $"{ProcessName}  ·  PID {ProcessId}"
                : $"{WindowTitle}  ·  {ProcessName}  ·  PID {ProcessId}";
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
    public Guid ScanGenerationId { get; init; }
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
            case MemoryValueType.Float when float.TryParse(text, out var single) && float.IsFinite(single):
                bytes = BitConverter.GetBytes(single);
                return true;
            case MemoryValueType.Double when double.TryParse(text, out var dbl) && double.IsFinite(dbl):
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
        if (multiplier <= 0 || !double.IsFinite(multiplier) || bytes.Length < type.Size()) return "?";
        if (Math.Abs(multiplier - 1d) < double.Epsilon) return Format(bytes, type);
        if (type is MemoryValueType.Int32 or MemoryValueType.Int64)
        {
            try
            {
                var scale = (decimal)multiplier;
                if (scale == 0) return "?";
                var integer = type == MemoryValueType.Int64 ? (decimal)BitConverter.ToInt64(bytes) : BitConverter.ToInt32(bytes);
                return (integer / scale).ToString("G29", CultureInfo.InvariantCulture);
            }
            catch (OverflowException) { return "?"; }
        }
        var decoded = ToDouble(bytes, type) / multiplier;
        return decoded.ToString(type == MemoryValueType.Double ? "G17" : "G9", CultureInfo.InvariantCulture);
    }

    public static bool TryParseEncoded(string text, MemoryValueType type, double multiplier, out byte[] bytes)
    {
        bytes = [];
        if (multiplier <= 0 || !double.IsFinite(multiplier)) return false;
        if (type is MemoryValueType.Int32 or MemoryValueType.Int64)
        {
            // Parse the input as an exact rational, never allowing decimal/double rounding
            // to turn a fractional value into an integer at the Int64 boundary.
            if (!TryParseRational(text, out var numerator, out var denominator)) return false;
            try
            {
                var scale = (decimal)multiplier;
                if (scale == 0) return false;
                var parts = decimal.GetBits(scale);
                var scaleNumerator = (new BigInteger((uint)parts[2]) << 64) |
                                     (new BigInteger((uint)parts[1]) << 32) | (uint)parts[0];
                var scaleDenominator = BigInteger.Pow(10, (parts[3] >> 16) & 0xFF);
                var integer = BigInteger.DivRem(numerator * scaleNumerator,
                    denominator * scaleDenominator, out var remainder);
                if (!remainder.IsZero) return false;
                if (type == MemoryValueType.Int32 && integer >= int.MinValue && integer <= int.MaxValue)
                    bytes = BitConverter.GetBytes((int)integer);
                else if (type == MemoryValueType.Int64 && integer >= long.MinValue && integer <= long.MaxValue)
                    bytes = BitConverter.GetBytes((long)integer);
                else return false;
                return true;
            }
            catch (OverflowException) { return false; }
        }
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var displayValue) ||
            !double.IsFinite(displayValue)) return false;
        var encoded = displayValue * multiplier;
        if (!double.IsFinite(encoded)) return false;
        if (type == MemoryValueType.Float && encoded is >= -float.MaxValue and <= float.MaxValue)
            bytes = BitConverter.GetBytes((float)encoded);
        else if (type == MemoryValueType.Double) bytes = BitConverter.GetBytes(encoded);
        else return false;
        return true;
    }

    private static bool TryParseRational(string text, out BigInteger numerator, out BigInteger denominator)
    {
        numerator = default;
        denominator = BigInteger.One;
        text = text.Trim();
        if (text.Length > 256) return false;
        var match = Regex.Match(text, @"^([+-]?)([0-9]*)(?:\.([0-9]*))?(?:[eE]([+-]?[0-9]+))?$");
        if (!match.Success || match.Groups[2].Length + match.Groups[3].Length == 0) return false;
        var exponent = 0;
        if (match.Groups[4].Success && (!int.TryParse(match.Groups[4].Value, NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out exponent) || exponent is < -1000 or > 1000)) return false;
        numerator = BigInteger.Parse(match.Groups[2].Value + match.Groups[3].Value, CultureInfo.InvariantCulture);
        if (match.Groups[1].Value == "-") numerator = -numerator;
        exponent -= match.Groups[3].Length;
        if (exponent < 0) denominator = BigInteger.Pow(10, -exponent);
        else numerator *= BigInteger.Pow(10, exponent);
        return true;
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
