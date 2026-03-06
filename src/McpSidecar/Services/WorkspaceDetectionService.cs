using McpSidecar.Models;
using Microsoft.Extensions.Logging;

namespace McpSidecar.Services;

/// <summary>
/// Provides workspace root detection functionality.
/// </summary>
public class WorkspaceDetectionService
{
    private readonly ILogger<WorkspaceDetectionService> _logger;

    // Marker files/directories that indicate a project root
    private static readonly string[] ProjectRootMarkers = new[]
    {
        ".git",
        ".svn",
        ".hg",
        "CMakeLists.txt",
        "Makefile",
        "configure.ac",
        "configure",
        "meson.build",
        "BUILD.bazel",
        "WORKSPACE",
        "Package.swift",
        "Cargo.toml",
        "go.mod",
        "pom.xml",
        "build.gradle",
        "package.json"
    };

    public WorkspaceDetectionService(ILogger<WorkspaceDetectionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Detects the workspace root by walking up from the current directory
    /// looking for project marker files.
    /// </summary>
    public string DetectWorkspaceRoot(string? explicitRoot = null)
    {
        // Priority 1: Explicit parameter
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            var normalized = NormalizePath(explicitRoot);
            _logger.LogDebug("Using explicit workspace root: {WorkspaceRoot}", normalized);
            return normalized;
        }

        // Priority 2: Environment variable
        var envRoot = Environment.GetEnvironmentVariable("MCP_WORKSPACE_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot))
        {
            var normalized = NormalizePath(envRoot);
            _logger.LogDebug("Using MCP_WORKSPACE_ROOT: {WorkspaceRoot}", normalized);
            return normalized;
        }

        // Priority 3: Walk up from current directory looking for markers
        var detectedRoot = DetectFromCurrentDirectory();
        if (detectedRoot != null)
        {
            _logger.LogInformation("Auto-detected workspace root: {WorkspaceRoot}", detectedRoot);
            return detectedRoot;
        }

        // Priority 4: Fallback to current directory
        var cwd = Directory.GetCurrentDirectory();
        _logger.LogWarning("Could not detect workspace root, using current directory: {WorkspaceRoot}", cwd);
        return NormalizePath(cwd);
    }

    /// <summary>
    /// Walks up from the current directory looking for project root markers.
    /// </summary>
    private string? DetectFromCurrentDirectory()
    {
        var currentDir = Directory.GetCurrentDirectory();
        var dir = new DirectoryInfo(currentDir);

        while (dir != null)
        {
            // Check for any project root markers
            foreach (var marker in ProjectRootMarkers)
            {
                var markerPath = Path.Combine(dir.FullName, marker);
                if (File.Exists(markerPath) || Directory.Exists(markerPath))
                {
                    _logger.LogDebug("Found project marker '{Marker}' at {Directory}", marker, dir.FullName);
                    return NormalizePath(dir.FullName);
                }
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// Detects compile_commands.json location using multiple strategies.
    /// </summary>
    public string? DetectCompileCommands(string workspaceRoot, string? explicitPath = null)
    {
        // Priority 1: Explicit parameter
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var normalized = NormalizePath(explicitPath);
            if (File.Exists(normalized))
            {
                _logger.LogDebug("Using explicit compile_commands.json: {Path}", normalized);
                return normalized;
            }
            _logger.LogWarning("Explicit compile_commands.json not found: {Path}", normalized);
            return null;
        }

        // Priority 2: Environment variable
        var envPath = Environment.GetEnvironmentVariable("MCP_COMPILE_COMMANDS");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var normalized = NormalizePath(envPath);
            if (File.Exists(normalized))
            {
                _logger.LogDebug("Using MCP_COMPILE_COMMANDS: {Path}", normalized);
                return normalized;
            }
            _logger.LogWarning("MCP_COMPILE_COMMANDS file not found: {Path}", normalized);
        }

        // Priority 3: Search common locations
        var candidates = new[]
        {
            Path.Combine(workspaceRoot, "compile_commands.json"),
            Path.Combine(workspaceRoot, "build", "compile_commands.json"),
            Path.Combine(workspaceRoot, "cmake-build-debug", "compile_commands.json"),
            Path.Combine(workspaceRoot, "cmake-build-release", "compile_commands.json"),
            Path.Combine(workspaceRoot, "out", "build", "compile_commands.json"),
            Path.Combine(workspaceRoot, "out", "compile_commands.json"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                _logger.LogDebug("Found compile_commands.json: {Path}", candidate);
                return NormalizePath(candidate);
            }
        }

        // Priority 4: Search one level deep for build directories
        try
        {
            foreach (var dir in Directory.GetDirectories(workspaceRoot))
            {
                var cc = Path.Combine(dir, "compile_commands.json");
                if (File.Exists(cc))
                {
                    _logger.LogDebug("Found compile_commands.json in subdirectory: {Path}", cc);
                    return NormalizePath(cc);
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Could not search subdirectories for compile_commands.json");
        }

        _logger.LogWarning("Could not find compile_commands.json in workspace: {WorkspaceRoot}", workspaceRoot);
        return null;
    }

    /// <summary>
    /// Validates compile_commands.json and returns statistics.
    /// </summary>
    public (int Total, int Valid, List<string> MissingFiles, int AgeDays) ValidateCompileCommands(string path)
    {
        if (!File.Exists(path))
        {
            return (0, 0, new List<string>(), 0);
        }

        var fileInfo = new FileInfo(path);
        var age = DateTime.Now - fileInfo.LastWriteTime;
        var ageDays = (int)age.TotalDays;

        try
        {
            var json = File.ReadAllText(path);
            var entries = System.Text.Json.JsonSerializer.Deserialize<List<CompileCommandEntry>>(json);
            
            if (entries == null || entries.Count == 0)
            {
                return (0, 0, new List<string>(), ageDays);
            }

            var missing = new List<string>();
            var valid = 0;

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.File))
                {
                    continue;
                }

                if (File.Exists(entry.File))
                {
                    valid++;
                }
                else
                {
                    missing.Add(entry.File);
                }
            }

            return (entries.Count, valid, missing, ageDays);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate compile_commands.json: {Path}", path);
            return (0, 0, new List<string>(), ageDays);
        }
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).Replace('\\', '/');
    }
}
