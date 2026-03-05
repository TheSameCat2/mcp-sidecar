namespace McpSidecar.Models;

public sealed record FactSpan
{
    public required int StartLine { get; init; }
    public required int StartCharacter { get; init; }
    public required int EndLine { get; init; }
    public required int EndCharacter { get; init; }
}

public sealed record Symbol
{
    public long? SymbolId { get; init; }
    public required long SnapshotId { get; init; }
    public required string StableKey { get; init; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public required string QualifiedName { get; init; }
    public long? ParentSymbolId { get; init; }
    public string? Visibility { get; init; } = "unknown";
    public string TemplateKind { get; init; } = "non_template";
    public long? CanonicalDeclId { get; init; }
    public long? CanonicalDefId { get; init; }
    public bool IsExported { get; init; }
    public required long ProvenanceId { get; init; }
}

public sealed record SymbolDecl
{
    public long? DeclId { get; init; }
    public required long SnapshotId { get; init; }
    public required long SymbolId { get; init; }
    public required long FileId { get; init; }
    public long? ParseContextId { get; init; }
    public required string Role { get; init; }
    public required FactSpan Span { get; init; }
    public string? SignatureText { get; init; }
    public string? TypeText { get; init; }
    public string? DocComment { get; init; }
    public bool IsImplicit { get; init; }
    public required long ProvenanceId { get; init; }
}

public sealed record Occurrence
{
    public long? OccurrenceId { get; init; }
    public required long SnapshotId { get; init; }
    public required long ParseContextId { get; init; }
    public required long FileId { get; init; }
    public required long SymbolId { get; init; }
    public required FactSpan Span { get; init; }
    public long RoleBits { get; init; }
    public bool ViaMacro { get; init; }
    public bool IsImplicit { get; init; }
    public required long ProvenanceId { get; init; }
}

public sealed record Relation
{
    public long? RelationId { get; init; }
    public required long SnapshotId { get; init; }
    public required long ParseContextId { get; init; }
    public required long FromSymbolId { get; init; }
    public required long ToSymbolId { get; init; }
    public required string Kind { get; init; }
    public required long ProvenanceId { get; init; }
}
