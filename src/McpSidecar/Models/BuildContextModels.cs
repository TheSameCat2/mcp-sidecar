using System.Text.Json;

namespace McpSidecar.Models;

public sealed record Snapshot
{
    public long? SnapshotId { get; init; }
    public required string RepoRoot { get; init; }
    public string? VcsCommit { get; init; }
    public required string WorkspaceHash { get; init; }
    public long? ParentSnapshotId { get; init; }
    public required string Kind { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? LastUpdatedAt { get; init; }
    public string IndexStatus { get; init; } = "in_progress";
    public bool IsArchived { get; init; }
    public DateTimeOffset? ArchivedAt { get; init; }
}

public sealed record BuildConfig
{
    public long? BuildConfigId { get; init; }
    public required long SnapshotId { get; init; }
    public required long SourceFileId { get; init; }
    public string? OutputPath { get; init; }
    public required string WorkingDirectory { get; init; }
    public required JsonElement ArgvJson { get; init; }
    public required string ArgvHash { get; init; }
    public string? Compiler { get; init; }
    public string? LanguageStandard { get; init; }
    public string? TargetTriple { get; init; }
    public string? Sysroot { get; init; }
    public string? DefinesHash { get; init; }
    public string? IncludePathsHash { get; init; }
    public required string CommandOrigin { get; init; }
    public string? CommandText { get; init; }
}

public sealed record ParseContext
{
    public long? ParseContextId { get; init; }
    public required long SnapshotId { get; init; }
    public required long FileId { get; init; }
    public required long BuildConfigId { get; init; }
    public required string ContextKind { get; init; }
    public string? PpFingerprint { get; init; }
    public long? BorrowedFromBuildConfigId { get; init; }
    public decimal Confidence { get; init; } = 1.000m;
    public JsonElement ParseErrorsJson { get; init; }
}
