namespace McpSidecar.Models;

/// <summary>
/// LSP Position (0-based)
/// </summary>
public record LspPosition(int Line, int Character);

/// <summary>
/// LSP Location
/// </summary>
public record LspLocation(string Uri, LspPosition Range);

/// <summary>
/// LSP Symbol Information
/// </summary>
public record LspSymbolInfo(
    string Name,
    string Kind,
    LspLocation Location,
    string? ContainerName
);

/// <summary>
/// Call Hierarchy Item
/// </summary>
public record CallHierarchyItem(
    string Name,
    string Kind,
    string Uri,
    LspPosition Range,
    LspPosition SelectionRange
);

/// <summary>
/// Call Hierarchy Incoming Call
/// </summary>
public record CallHierarchyIncomingCall(
    CallHierarchyItem From,
    LspPosition[] FromRanges
);

/// <summary>
/// Call Hierarchy Outgoing Call
/// </summary>
public record CallHierarchyOutgoingCall(
    CallHierarchyItem To,
    LspPosition[] FromRanges
);
