namespace GameValueEditor.Models;

public enum MemoryValueType
{
    Int32,
    Int64,
    Float,
    Double
}

public enum ScanComparison
{
    Exact,
    Changed,
    Unchanged,
    Increased,
    Decreased
}

public static class SearchRoutineIds
{
    public const string All = "all";
    public const string DirectNumeric = "direct-numeric";
    public const string ScaledNumeric = "scaled-numeric";
}

public sealed record SearchRoutineOption(string Id, string DisplayName, string Description);

public static class MemoryValueTypeExtensions
{
    public static string ToDisplayName(this MemoryValueType type) => type switch
    {
        MemoryValueType.Int32 => "4 Bytes",
        MemoryValueType.Int64 => "8 Bytes",
        MemoryValueType.Float => "Float",
        MemoryValueType.Double => "Double",
        _ => type.ToString()
    };

    public static int Size(this MemoryValueType type) => type switch
    {
        MemoryValueType.Int32 => 4,
        MemoryValueType.Int64 => 8,
        MemoryValueType.Float => 4,
        MemoryValueType.Double => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}
