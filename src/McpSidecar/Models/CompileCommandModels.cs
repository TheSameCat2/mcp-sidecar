namespace McpSidecar.Models;

/// <summary>
/// Represents a single entry from compile_commands.json.
/// </summary>
public sealed record CompileCommandEntry(
    string File,
    string Directory,
    string? Output,
    IReadOnlyList<string> Arguments,
    string? Command,
    string Origin = "compile_commands.json"
);
