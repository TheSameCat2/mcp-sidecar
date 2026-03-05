using System.Text.Json;

namespace McpSidecar.Models;

public sealed record Provenance
{
    public long? ProvenanceId { get; init; }
    public required string ExtractorName { get; init; }
    public required string ExtractionMethod { get; init; }
    public required string Exactness { get; init; }
    public decimal Confidence { get; init; } = 1.000m;
    public JsonElement EvidenceJson { get; init; }
}
