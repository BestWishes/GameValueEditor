namespace GameValueEditor.Models;

// Installation identity is deliberately separate from build identity and process lifetime.
public sealed record GameIdentityEvidence
{
    public string InstallationExecutablePath { get; init; } = string.Empty;
    public string ExecutableName { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public string PackageId { get; init; } = string.Empty;
    public string PlatformName { get; init; } = string.Empty;
    public string PlatformAppId { get; init; } = string.Empty;
    public bool IsTemporaryExecutable { get; init; }
    public bool IsSharedExecutable { get; init; }
}

internal sealed record GameAssociationRequest(string GameName, IReadOnlyList<ProcessItem> Processes,
    IReadOnlyList<ProcessItem> SuggestedProcesses);
