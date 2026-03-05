namespace McpSidecar.Models;

public sealed record FileDependency
{
    public long? DepId { get; init; }
    public required long SnapshotId { get; init; }
    public required long ParseContextId { get; init; }
    public required long FromFileId { get; init; }
    public required long ToFileId { get; init; }
    public required string DirectiveKind { get; init; }
    public string? LiteralText { get; init; }
    public required FactSpan Span { get; init; }
    public bool IsActive { get; init; } = true;
    public required long ProvenanceId { get; init; }
}

public sealed record Callsite
{
    public long? CallsiteId { get; init; }
    public required long SnapshotId { get; init; }
    public required long ParseContextId { get; init; }
    public required long FileId { get; init; }
    public required long CallerSymbolId { get; init; }
    public required FactSpan Span { get; init; }
    public required string DispatchKind { get; init; }
    public string? RawText { get; init; }
    public required long ProvenanceId { get; init; }
}

public sealed record CallTarget
{
    public required long SnapshotId { get; init; }
    public required long CallsiteId { get; init; }
    public required long CalleeSymbolId { get; init; }
    public int Rank { get; init; } = 1;
    public required string ResolutionKind { get; init; }
    public decimal Confidence { get; init; } = 1.000m;
    public required long ProvenanceId { get; init; }
}
