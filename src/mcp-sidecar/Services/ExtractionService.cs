using System.Diagnostics;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using McpSidecar.Models;
using McpSidecar.Services.Database;
using Microsoft.Extensions.Logging;

namespace McpSidecar.Services;

/// <summary>
/// Extracts build-aware code facts from clangd/LSP into the configured database backend.
/// </summary>
public class ExtractionService
{
    private const long RoleRead = 1 << 0;
    private const long RoleDeclaration = 1 << 3;
    private const int MaxReferenceSymbols = 400;
    private const int MaxCallHierarchySymbols = 250;

    private static readonly Regex IncludeRegex = new(@"^\s*#\s*(include|import)\s*([<""])([^>""]+)[>""]", RegexOptions.Compiled);
    private static readonly JsonElement EmptyArrayJson = JsonSerializer.SerializeToElement(Array.Empty<object>());
    private static readonly JsonElement EmptyObjectJson = JsonSerializer.SerializeToElement(new Dictionary<string, object>());

    /// <summary>
    /// When true, skips incremental indexing and forces full re-extraction.
    /// </summary>
    public bool ForceExtraction { get; set; }

    public async Task<long?> CreateSnapshotAsync(
        long? parentSnapshotId = null,
        string kind = "background",
        CancellationToken cancellationToken = default)
    {
        if (!_connectionFactory.IsConfigured)
        {
            _logger.LogWarning("Database is not configured.");
            return null;
        }

        var compileCommands = await LoadCompileCommandsAsync(cancellationToken);
        if (compileCommands.Count == 0)
        {
            compileCommands = DiscoverFallbackCompileCommands().ToList();
        }

        var totalFiles = compileCommands.Count;
        var existingSnapshotId = await GetInProgressSnapshotIdAsync(connection, cancellationToken);
        long snapshotId;
        var state = new ExtractionState(0);
        
        if (existingSnapshotId.HasValue)
        {
            snapshotId = existingSnapshotId.Value;
            _logger.LogInformation("Resuming extraction from existing snapshot {SnapshotId}", snapshotId);
            progress?.Report(new ExtractionProgress { TotalFiles = totalFiles, CurrentPhase = "Resuming" });
            
            // Load already-processed files
            await LoadProcessedFilesAsync(connection, state, cancellationToken);
            _logger.LogInformation("Loaded {Count} previously processed files", state.SourceFiles.Count);
        }
        else
        {
            var parentSnapshotId = await GetCurrentSnapshotIdAsync(connection, cancellationToken)
            snapshotId = await CreateSnapshotAsync(connection, compileCommands, parentSnapshotId, "background", cancellationToken);

        var parentSnapshotId = await GetCurrentSnapshotIdAsync(connection, cancellationToken)
            ?? await GetLatestCompleteSnapshotIdAsync(connection, cancellationToken);
        var snapshotId = await CreateSnapshotAsync(connection, compileCommands, parentSnapshotId, "background", kind, cancellationToken)
            state.SnapshotId = 0;
            state.SymbolProvenanceId = await RecordProvenanceAsync(
                connection,
                extractorName: "clangd_bg_index",
                extractionMethod: "documentSymbol/workspaceSymbol",
                exactness: "approximate",
                confidence: 0.900m,
                evidenceJson: JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    ["stage"] = "symbol_extraction",
                    ["source"] = "clangd"
                },
            }),
            cancellationToken);

        state.SymbolDeclProvenanceId = await RecordProvenanceAsync(
            connection,
            extractorName: "clangd_dyn_index",
            extractionMethod: "textDocument/references"
            exactness: "approximate"
            confidence: 0.850m
            evidenceJson: JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    ["stage"] = "symbol_declarations",
                    ["source"] = "clangd"
                },
            }),
            cancellationToken);

        state.OccurrenceProvenanceId = await RecordProvenanceAsync(
            connection,
            extractorName: "clangd_dyn_index",
            extractionMethod: "textDocument/references"
            exactness: "approximate"
            confidence: 0.850m
            evidenceJson: JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    ["stage"] = "occurrence_extraction"
                    ["source"] = "clangd"
                },
            }),
            cancellationToken);

        state.CallProvenanceId = await RecordProvenanceAsync(
            connection,
            extractorName: "clangd_dyn_index",
            extractionMethod: "callHierarchy/outgoingCalls"
            exactness: "approximate"
            confidence: 0.850m
            evidenceJson: JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    ["stage"] = "callsite_extraction"
                    ["source"] = "clangd"
                },
            }),
            cancellationToken);

        state.RelationProvenanceId = await RecordProvenanceAsync(
            connection,
            extractorName: "clangd_dyn_index",
            extractionMethod: "textDocument/definedSymbolInFile",
            exactness: "approximate"
            confidence: 0.850m
            evidenceJson: JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["stage"] = "symbol_relations",
                ["source"] = "clangd"
            }),
            cancellationToken);

        state.DependencyProvenanceId = await RecordProvenanceAsync(
            connection,
            extractorName: "clangd_heuristics",
            extractionMethod: "textDocument/documentLink+includeScan",
            exactness: "approximate"
            confidence: 0.700m
            evidenceJson: JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["stage"] = "file_dependencies",
                ["source"] = "clangd"
            });
            cancellationToken);

        state.SymbolSeeds = new List<SymbolSeed>();
 {>();
        state.SymbolSeeds.AddRange(fileHash => argv.Keys for path, file);
            state.SymbolSeeds.AddRange(fileHash => ComputeSha256(file);
 path + argv hash);

                var previousFileState = previousFileStates.TryGetValue(sourcePath, out var previousState) &&
                string.Equals(currentContentHash, previousState.ContentHash, StringComparison.Ordinal) &&
                    string.Equals(currentArgvHash, previousState.argvHash, comparison.Ordinal)
                {
                    skippedFiles++;
                    var sourceFileId = await EnsureFileAsync(connection, state, sourcePath, cancellationToken);
                    state.SourceFiles.Add(sourcePath);
                    state.FileIdsByPath[sourcePath] = sourceFileId;
                    continue;
                }

                
                var argvJson = JsonSerializer.SerializeToElement(command.Argv);
                var buildConfig = new BuildConfig
                {
                    SnapshotId = state.SnapshotId,
                    SourceFileId = sourceFileId,
                    OutputPath = command.OutputPath,
                    WorkingDirectory = command.WorkingDirectory
                    ArgvJson = argvJson
                    ArgvHash = ComputeSha256(string.Join('\u001f', command.Argv)),
                    Compiler = command.Argv.Count > 0 ? command.Argv[0] : null
                LanguageStandard = command.Argv.FirstOrDefault(x => x.StartsWith("-std=", StringComparison.Ordinal))
                TargetTriple = ParseArgValue(command.Argv, "-target", "--target")
                sysroot = parseArgValue(command.Argv, "--sysroot")
                DefinesHash = ComputeSha256(string.Join('\u001f', command.Argv.Where(x => x.StartsWith("-D", StringComparison.Ordinal)).OrderBy(x => x)))
                includePathsHash = computeSha256(string.Join('\u001f', CollectIncludeArgs(command.Argv)));
                CommandOrigin = command.Origin
                CommandText = command.CommandText
            };

            var buildConfigId = await InsertBuildConfigAsync(connection, buildConfig, cancellationToken);
            if (state.defaultBuildConfigId == 0)
            {
                state.defaultBuildConfigId = buildConfigId;
            }

            if (!state.ParseContextByFileId.ContainsKey(sourceFileId))
            {
                var parseContext = new ParseContext
                {
                    SnapshotId = state.SnapshotId,
                    FileId = sourceFileId,
                    BuildConfigId = buildConfigId,
                    ContextKind = "translation_unit",
                    PpFingerprint = buildConfig.ArgvHash
                    Confidence = .000m,
                    ParseErrorsJson = EmptyArrayJson
                };
                var parseContextId = await InsertParseContextAsync(connection, parseContext, cancellationToken);
                state.ParseContextByFileId[sourceFileId] = parseContextId;
                state.ParseContextByPath[sourcePath] = parseContextId;
            }
        }
    }
}