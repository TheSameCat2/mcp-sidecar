using System.Diagnostics;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using McpSidecar.Models;
using McpSidecar.Services.Database;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;

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

    private readonly ILogger<ExtractionService> _logger;
    private readonly ClangdService _clangd;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ISqlBuilder _sqlBuilder;

    public ExtractionService(
        ILogger<ExtractionService> logger,
        ClangdService clangd,
        IDbConnectionFactory connectionFactory,
        ISqlBuilder sqlBuilder)
    {
        _logger = logger;
        _clangd = clangd;
        _connectionFactory = connectionFactory;
        _sqlBuilder = sqlBuilder;
    }

    public async Task<long?> CreateSnapshotAsync(
        long? parentSnapshotId = null,
        string kind = "background",
        CancellationToken cancellationToken = default)
    {
        if (!_connectionFactory.IsConfigured)
        {
            _logger.LogWarning("Cannot create snapshot: database is not configured.");
            return null;
        }

        var compileCommands = await LoadCompileCommandsAsync(cancellationToken);
        if (compileCommands.Count == 0)
        {
            compileCommands = DiscoverFallbackCompileCommands().ToList();
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var effectiveParentId = parentSnapshotId ?? await GetCurrentSnapshotIdAsync(connection, cancellationToken);
        return await CreateSnapshotAsync(connection, compileCommands, effectiveParentId, kind, cancellationToken);
    }

    public async Task<long?> GetCurrentSnapshotIdAsync(CancellationToken cancellationToken = default)
    {
        if (!_connectionFactory.IsConfigured)
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await GetCurrentSnapshotIdAsync(connection, cancellationToken);
    }

    public async Task<int> ArchiveOldSnapshotsAsync(
        int keepLatest = 5,
        CancellationToken cancellationToken = default)
    {
        if (!_connectionFactory.IsConfigured)
        {
            return 0;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await ArchiveOldSnapshotsAsync(connection, keepLatest, cancellationToken);
    }

    public async Task<long?> RunInitialExtractionAsync(CancellationToken cancellationToken = default)
    {
        if (!_connectionFactory.IsConfigured)
        {
            _logger.LogWarning("Skipping extraction: database is not configured.");
            return null;
        }

        if (!_clangd.IsRunning)
        {
            _logger.LogWarning("Skipping extraction: clangd is not running.");
            return null;
        }

        var compileCommands = await LoadCompileCommandsAsync(cancellationToken);
        if (compileCommands.Count == 0)
        {
            compileCommands = DiscoverFallbackCompileCommands().ToList();
            _logger.LogInformation("No compile_commands.json entries found, using inferred source files: {Count}", compileCommands.Count);
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parentSnapshotId = await GetCurrentSnapshotIdAsync(connection, cancellationToken);
        var snapshotId = await CreateSnapshotAsync(connection, compileCommands, parentSnapshotId, "background", cancellationToken);
        var state = new ExtractionState(snapshotId);

        state.SymbolProvenanceId = await RecordProvenanceAsync(
            connection,
            extractorName: "clangd_bg_index",
            extractionMethod: "documentSymbol/workspaceSymbol",
            exactness: "approximate",
            confidence: 0.900m,
            evidenceJson: JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["stage"] = "symbol_extraction",
                ["source"] = "clangd",
                ["workspace_root"] = NormalizePath(_clangd.WorkspaceRoot)
            }),
            cancellationToken);

        state.SymbolDeclProvenanceId = await RecordProvenanceAsync(
            connection,
            extractorName: "clangd_bg_index",
            extractionMethod: "documentSymbol/workspaceSymbol",
            exactness: "approximate",
            confidence: 0.900m,
            evidenceJson: JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["stage"] = "symbol_declarations",
                ["source"] = "clangd"
            }),
            cancellationToken);

        state.OccurrenceProvenanceId = await RecordProvenanceAsync(
            connection,
            extractorName: "clangd_dyn_index",
            extractionMethod: "textDocument/references",
            exactness: "approximate",
            confidence: 0.850m,
            evidenceJson: JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["stage"] = "occurrence_extraction",
                ["source"] = "clangd"
            }),
            cancellationToken);

        state.CallProvenanceId = await RecordProvenanceAsync(
            connection,
            extractorName: "clangd_dyn_index",
            extractionMethod: "callHierarchy/outgoingCalls",
            exactness: "approximate",
            confidence: 0.850m,
            evidenceJson: JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["stage"] = "callsite_extraction",
                ["source"] = "clangd"
            }),
            cancellationToken);

        state.CallTargetProvenanceId = await RecordProvenanceAsync(
            connection,
            extractorName: "clangd_dyn_index",
            extractionMethod: "callHierarchy/outgoingCalls",
            exactness: "approximate",
            confidence: 0.900m,
            evidenceJson: JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["stage"] = "call_target_resolution",
                ["source"] = "clangd"
            }),
            cancellationToken);

        state.DependencyProvenanceId = await RecordProvenanceAsync(
            connection,
            extractorName: "heuristic",
            extractionMethod: "textDocument/documentLink+includeScan",
            exactness: "inferred",
            confidence: 0.700m,
            evidenceJson: JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["stage"] = "file_dependency_extraction",
                ["source"] = "include_scan"
            }),
            cancellationToken);

        state.RelationProvenanceId = await RecordProvenanceAsync(
            connection,
            extractorName: "clangd_bg_index",
            extractionMethod: "documentSymbolHierarchy",
            exactness: "approximate",
            confidence: 0.800m,
            evidenceJson: JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["stage"] = "symbol_relation_extraction",
                ["source"] = "clangd"
            }),
            cancellationToken);

        _logger.LogInformation(
            "Created snapshot {SnapshotId} for workspace {WorkspaceRoot} (parent_snapshot_id={ParentSnapshotId})",
            snapshotId,
            _clangd.WorkspaceRoot,
            parentSnapshotId);

        try
        {
            await SeedBuildAndParseContextAsync(connection, state, compileCommands, cancellationToken);
            if (state.DefaultBuildConfigId == 0)
            {
                _logger.LogWarning("No source files were discovered for snapshot {SnapshotId}; skipping fact extraction stages.", state.SnapshotId);
                await UpdateSnapshotStatusAsync(connection, state.SnapshotId, "complete", cancellationToken);
                await ArchiveOldSnapshotsAsync(connection, 5, cancellationToken);
                return snapshotId;
            }

            await ExtractSymbolsAsync(connection, state, cancellationToken);
            await ExtractOccurrencesAsync(connection, state, cancellationToken);
            await ExtractCallHierarchyAsync(connection, state, cancellationToken);
            await ExtractFileDependenciesAsync(connection, state, cancellationToken);

            _logger.LogInformation(
                "Extraction complete for snapshot {SnapshotId}: files={FileCount}, symbols={SymbolCount}, seeds={SeedCount}",
                state.SnapshotId,
                state.FileIdsByPath.Count,
                state.SymbolIdsByStableKey.Count,
                state.SymbolSeeds.Count);

            await UpdateSnapshotStatusAsync(connection, state.SnapshotId, "complete", cancellationToken);
            await ArchiveOldSnapshotsAsync(connection, 5, cancellationToken);
            return snapshotId;
        }
        catch
        {
            await UpdateSnapshotStatusAsync(connection, state.SnapshotId, "failed", cancellationToken);
            throw;
        }
    }

    private async Task SeedBuildAndParseContextAsync(
        DbConnection connection,
        ExtractionState state,
        IReadOnlyList<CompileCommandEntry> compileCommands,
        CancellationToken cancellationToken)
    {
        foreach (var command in compileCommands)
        {
            var sourcePath = NormalizePath(command.SourceFile);
            var sourceFileId = await EnsureFileAsync(connection, state, sourcePath, cancellationToken);
            state.SourceFiles.Add(sourcePath);

            var argvJson = JsonSerializer.SerializeToElement(command.Argv);
            var buildConfig = new BuildConfig
            {
                SnapshotId = state.SnapshotId,
                SourceFileId = sourceFileId,
                OutputPath = command.OutputPath,
                WorkingDirectory = command.WorkingDirectory,
                ArgvJson = argvJson,
                ArgvHash = ComputeSha256(string.Join('\u001f', command.Argv)),
                Compiler = command.Argv.Count > 0 ? command.Argv[0] : null,
                LanguageStandard = command.Argv.FirstOrDefault(x => x.StartsWith("-std=", StringComparison.Ordinal)),
                TargetTriple = ParseArgValue(command.Argv, "-target", "--target"),
                Sysroot = ParseArgValue(command.Argv, "--sysroot"),
                DefinesHash = ComputeSha256(string.Join('\u001f', command.Argv.Where(x => x.StartsWith("-D", StringComparison.Ordinal)).OrderBy(x => x))),
                IncludePathsHash = ComputeSha256(string.Join('\u001f', CollectIncludeArgs(command.Argv))),
                CommandOrigin = command.Origin,
                CommandText = command.CommandText
            };

            var buildConfigId = await InsertBuildConfigAsync(connection, buildConfig, cancellationToken);
            if (state.DefaultBuildConfigId == 0)
            {
                state.DefaultBuildConfigId = buildConfigId;
            }

            if (!state.ParseContextByFileId.ContainsKey(sourceFileId))
            {
                var parseContext = new ParseContext
                {
                    SnapshotId = state.SnapshotId,
                    FileId = sourceFileId,
                    BuildConfigId = buildConfigId,
                    ContextKind = "translation_unit",
                    PpFingerprint = buildConfig.ArgvHash,
                    Confidence = 1.000m,
                    ParseErrorsJson = EmptyArrayJson
                };
                var parseContextId = await InsertParseContextAsync(connection, parseContext, cancellationToken);
                state.ParseContextByFileId[sourceFileId] = parseContextId;
                state.ParseContextByPath[sourcePath] = parseContextId;
            }

            await EnsureFileOpenAsync(state, sourcePath, cancellationToken);
        }
    }

    private async Task ExtractSymbolsAsync(
        DbConnection connection,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        foreach (var sourcePath in state.SourceFiles.Distinct(StringComparer.Ordinal))
        {
            var fileUri = PathToFileUri(sourcePath);
            var fileId = await EnsureFileAsync(connection, state, sourcePath, cancellationToken);
            var parseContextId = await EnsureParseContextForFileAsync(connection, state, sourcePath, fileId, cancellationToken);

            await EnsureFileOpenAsync(state, sourcePath, cancellationToken);

            var documentSymbolResponse = await _clangd.SendRequestAsync(
                "textDocument/documentSymbol",
                new { textDocument = new { uri = fileUri } },
                cancellationToken);

            if (documentSymbolResponse.HasValue &&
                documentSymbolResponse.Value.TryGetProperty("result", out var docSymbols) &&
                docSymbols.ValueKind == JsonValueKind.Array)
            {
                foreach (var symbolElement in docSymbols.EnumerateArray())
                {
                    await ProcessDocumentSymbolElementAsync(
                        connection,
                        state,
                        symbolElement,
                        sourcePath,
                        fileUri,
                        fileId,
                        parseContextId,
                        parentSymbolId: null,
                        parentQualifiedName: null,
                        cancellationToken);
                }
            }
        }

        var workspaceSymbolResponse = await _clangd.SendRequestAsync(
            "workspace/symbol",
            new { query = string.Empty },
            cancellationToken);

        if (workspaceSymbolResponse.HasValue &&
            workspaceSymbolResponse.Value.TryGetProperty("result", out var workspaceSymbols) &&
            workspaceSymbols.ValueKind == JsonValueKind.Array)
        {
            foreach (var symbolElement in workspaceSymbols.EnumerateArray())
            {
                await ProcessSymbolInformationAsync(
                    connection,
                    state,
                    symbolElement,
                    defaultFilePath: null,
                    defaultFileUri: null,
                    parentSymbolId: null,
                    parentQualifiedName: null,
                    cancellationToken);
            }
        }
    }

    private async Task ExtractOccurrencesAsync(
        DbConnection connection,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        foreach (var seed in state.SymbolSeeds.Take(MaxReferenceSymbols))
        {
            await EnsureFileOpenAsync(state, seed.FilePath, cancellationToken);

            var referencesResponse = await _clangd.SendRequestAsync(
                "textDocument/references",
                new
                {
                    textDocument = new { uri = seed.FileUri },
                    position = new { line = seed.Line, character = seed.Character },
                    context = new { includeDeclaration = true }
                },
                cancellationToken);

            if (!referencesResponse.HasValue ||
                !referencesResponse.Value.TryGetProperty("result", out var references) ||
                references.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var reference in references.EnumerateArray())
            {
                if (!reference.TryGetProperty("uri", out var uriElement))
                {
                    continue;
                }

                var uri = uriElement.GetString();
                if (string.IsNullOrWhiteSpace(uri))
                {
                    continue;
                }

                var referencePath = UriToPath(uri!);
                var referenceFileId = await EnsureFileAsync(connection, state, referencePath, cancellationToken);
                var referenceParseContextId = await EnsureParseContextForFileAsync(connection, state, referencePath, referenceFileId, cancellationToken);

                if (!reference.TryGetProperty("range", out var rangeElement))
                {
                    continue;
                }

                var span = ParseSpan(rangeElement);
                var isDeclaration = uri == seed.FileUri &&
                                    span.StartLine == seed.Line &&
                                    span.StartCharacter == seed.Character;
                var roleBits = isDeclaration ? RoleDeclaration : RoleRead;

                var occurrence = new Occurrence
                {
                    SnapshotId = state.SnapshotId,
                    ParseContextId = referenceParseContextId,
                    FileId = referenceFileId,
                    SymbolId = seed.SymbolId,
                    Span = span,
                    RoleBits = roleBits,
                    ViaMacro = false,
                    IsImplicit = false,
                    ProvenanceId = state.OccurrenceProvenanceId
                };

                var dedupeKey = $"{occurrence.SymbolId}|{occurrence.FileId}|{occurrence.Span.StartLine}|{occurrence.Span.StartCharacter}|{occurrence.Span.EndLine}|{occurrence.Span.EndCharacter}";
                if (!state.OccurrenceDedupKeys.Add(dedupeKey))
                {
                    continue;
                }

                await InsertOccurrenceAsync(connection, occurrence, cancellationToken);
            }
        }
    }

    private async Task ExtractCallHierarchyAsync(
        DbConnection connection,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        foreach (var seed in state.SymbolSeeds.Where(IsFunctionLike).Take(MaxCallHierarchySymbols))
        {
            await EnsureFileOpenAsync(state, seed.FilePath, cancellationToken);

            var prepareResponse = await _clangd.SendRequestAsync(
                "textDocument/prepareCallHierarchy",
                new
                {
                    textDocument = new { uri = seed.FileUri },
                    position = new { line = seed.Line, character = seed.Character }
                },
                cancellationToken);

            if (!prepareResponse.HasValue ||
                !prepareResponse.Value.TryGetProperty("result", out var prepareResult) ||
                prepareResult.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var hierarchyItem in prepareResult.EnumerateArray())
            {
                if (!hierarchyItem.TryGetProperty("uri", out var itemUriElement))
                {
                    continue;
                }

                var callerPath = UriToPath(itemUriElement.GetString() ?? seed.FileUri);
                var callerFileId = await EnsureFileAsync(connection, state, callerPath, cancellationToken);
                var callerParseContextId = await EnsureParseContextForFileAsync(connection, state, callerPath, callerFileId, cancellationToken);

                var callerSymbolId = seed.SymbolId;
                if (hierarchyItem.TryGetProperty("range", out var hierarchyRange))
                {
                    callerSymbolId = await EnsureSymbolFromHierarchyItemAsync(
                        connection,
                        state,
                        hierarchyItem,
                        callerPath,
                        callerFileId,
                        callerParseContextId,
                        cancellationToken);
                }

                var outgoingResponse = await _clangd.SendRequestAsync(
                    "callHierarchy/outgoingCalls",
                    new { item = hierarchyItem },
                    cancellationToken);

                if (!outgoingResponse.HasValue ||
                    !outgoingResponse.Value.TryGetProperty("result", out var outgoingResult) ||
                    outgoingResult.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var outgoingCall in outgoingResult.EnumerateArray())
                {
                    if (!outgoingCall.TryGetProperty("to", out var calleeItem))
                    {
                        continue;
                    }

                    long calleeSymbolId;
                    if (calleeItem.TryGetProperty("uri", out var calleeUriElement))
                    {
                        var calleePath = UriToPath(calleeUriElement.GetString() ?? string.Empty);
                        var calleeFileId = await EnsureFileAsync(connection, state, calleePath, cancellationToken);
                        var calleeParseContextId = await EnsureParseContextForFileAsync(connection, state, calleePath, calleeFileId, cancellationToken);
                        calleeSymbolId = await EnsureSymbolFromHierarchyItemAsync(
                            connection,
                            state,
                            calleeItem,
                            calleePath,
                            calleeFileId,
                            calleeParseContextId,
                            cancellationToken);
                    }
                    else
                    {
                        continue;
                    }

                    var fromRanges = outgoingCall.TryGetProperty("fromRanges", out var rangesElement) && rangesElement.ValueKind == JsonValueKind.Array
                        ? rangesElement.EnumerateArray().ToList()
                        : new List<JsonElement>();

                    if (fromRanges.Count == 0 && hierarchyItem.TryGetProperty("selectionRange", out var selectionRange))
                    {
                        fromRanges.Add(selectionRange);
                    }

                    foreach (var fromRange in fromRanges)
                    {
                        var span = ParseSpan(fromRange);
                        var dedupeKey = $"{callerSymbolId}|{calleeSymbolId}|{callerFileId}|{span.StartLine}|{span.StartCharacter}|{span.EndLine}|{span.EndCharacter}";
                        if (!state.CallsiteDedupKeys.Add(dedupeKey))
                        {
                            continue;
                        }

                        var callsite = new Callsite
                        {
                            SnapshotId = state.SnapshotId,
                            ParseContextId = callerParseContextId,
                            FileId = callerFileId,
                            CallerSymbolId = callerSymbolId,
                            Span = span,
                            DispatchKind = "direct",
                            RawText = null,
                            ProvenanceId = state.CallProvenanceId
                        };

                        var callsiteId = await InsertCallsiteAsync(connection, callsite, cancellationToken);
                        var callTarget = new CallTarget
                        {
                            SnapshotId = state.SnapshotId,
                            CallsiteId = callsiteId,
                            CalleeSymbolId = calleeSymbolId,
                            Rank = 1,
                            ResolutionKind = "direct",
                            Confidence = 0.900m,
                            ProvenanceId = state.CallTargetProvenanceId
                        };
                        await InsertCallTargetAsync(connection, callTarget, cancellationToken);
                    }
                }
            }
        }
    }

    private async Task ExtractFileDependenciesAsync(
        DbConnection connection,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        foreach (var sourcePath in state.SourceFiles.Distinct(StringComparer.Ordinal))
        {
            var sourceFileId = await EnsureFileAsync(connection, state, sourcePath, cancellationToken);
            var parseContextId = await EnsureParseContextForFileAsync(connection, state, sourcePath, sourceFileId, cancellationToken);
            var sourceUri = PathToFileUri(sourcePath);

            await EnsureFileOpenAsync(state, sourcePath, cancellationToken);

            var linksResponse = await _clangd.SendRequestAsync(
                "textDocument/documentLink",
                new { textDocument = new { uri = sourceUri } },
                cancellationToken);

            var lines = File.Exists(sourcePath) ? await File.ReadAllLinesAsync(sourcePath, cancellationToken) : Array.Empty<string>();

            if (linksResponse.HasValue &&
                linksResponse.Value.TryGetProperty("result", out var linksResult) &&
                linksResult.ValueKind == JsonValueKind.Array)
            {
                foreach (var link in linksResult.EnumerateArray())
                {
                    if (!link.TryGetProperty("range", out var rangeElement))
                    {
                        continue;
                    }

                    var span = ParseSpan(rangeElement);
                    var lineText = span.StartLine >= 0 && span.StartLine < lines.Length ? lines[span.StartLine] : string.Empty;
                    var directiveKind = InferDirectiveKind(lineText);
                    var literalText = ExtractIncludeLiteral(lineText);

                    string? targetPath = null;
                    if (link.TryGetProperty("target", out var targetElement) && targetElement.ValueKind == JsonValueKind.String)
                    {
                        var target = targetElement.GetString();
                        if (!string.IsNullOrWhiteSpace(target))
                        {
                            targetPath = UriLooksLikeFile(target!) ? UriToPath(target!) : BuildExternalIncludePath(target!);
                        }
                    }

                    if (targetPath == null && literalText != null)
                    {
                        targetPath = ResolveIncludePath(sourcePath, literalText);
                    }

                    if (targetPath == null)
                    {
                        continue;
                    }

                    var toFileId = await EnsureFileAsync(connection, state, targetPath, cancellationToken);
                    var dependency = new FileDependency
                    {
                        SnapshotId = state.SnapshotId,
                        ParseContextId = parseContextId,
                        FromFileId = sourceFileId,
                        ToFileId = toFileId,
                        DirectiveKind = directiveKind,
                        LiteralText = literalText,
                        Span = span,
                        IsActive = true,
                        ProvenanceId = state.DependencyProvenanceId
                    };

                    await InsertFileDependencyIfNewAsync(connection, state, dependency, cancellationToken);
                }
            }

            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var match = IncludeRegex.Match(lines[lineIndex]);
                if (!match.Success)
                {
                    continue;
                }

                var directiveKind = match.Groups[1].Value.Equals("import", StringComparison.OrdinalIgnoreCase)
                    ? "import"
                    : "include";
                var includeLiteral = match.Groups[3].Value;
                var targetPath = ResolveIncludePath(sourcePath, includeLiteral);
                var toFileId = await EnsureFileAsync(connection, state, targetPath, cancellationToken);

                var dependency = new FileDependency
                {
                    SnapshotId = state.SnapshotId,
                    ParseContextId = parseContextId,
                    FromFileId = sourceFileId,
                    ToFileId = toFileId,
                    DirectiveKind = directiveKind,
                    LiteralText = includeLiteral,
                    Span = new FactSpan
                    {
                        StartLine = lineIndex,
                        StartCharacter = Math.Max(match.Index, 0),
                        EndLine = lineIndex,
                        EndCharacter = match.Index + match.Length
                    },
                    IsActive = true,
                    ProvenanceId = state.DependencyProvenanceId
                };

                await InsertFileDependencyIfNewAsync(connection, state, dependency, cancellationToken);
            }
        }
    }

    private async Task ProcessDocumentSymbolElementAsync(
        DbConnection connection,
        ExtractionState state,
        JsonElement symbolElement,
        string filePath,
        string fileUri,
        long fileId,
        long parseContextId,
        long? parentSymbolId,
        string? parentQualifiedName,
        CancellationToken cancellationToken)
    {
        if (symbolElement.TryGetProperty("location", out _))
        {
            await ProcessSymbolInformationAsync(
                connection,
                state,
                symbolElement,
                filePath,
                fileUri,
                parentSymbolId,
                parentQualifiedName,
                cancellationToken);
            return;
        }

        if (!symbolElement.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        if (!symbolElement.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        if (!symbolElement.TryGetProperty("range", out var rangeElement))
        {
            return;
        }

        var name = nameElement.GetString() ?? string.Empty;
        var lspKind = kindElement.GetInt32();
        var kind = MapLspKind(lspKind);
        var qualifiedName = string.IsNullOrWhiteSpace(parentQualifiedName) ? name : $"{parentQualifiedName}::{name}";
        var selectionRange = symbolElement.TryGetProperty("selectionRange", out var selectionRangeElement)
            ? selectionRangeElement
            : rangeElement;
        var selectionSpan = ParseSpan(selectionRange);
        var stableKey = BuildStableKey(qualifiedName, kind, filePath, selectionSpan);

        var symbol = new Symbol
        {
            SnapshotId = state.SnapshotId,
            StableKey = stableKey,
            Kind = kind,
            Name = name,
            QualifiedName = qualifiedName,
            ParentSymbolId = parentSymbolId,
            Visibility = "unknown",
            TemplateKind = "non_template",
            IsExported = false,
            ProvenanceId = state.SymbolProvenanceId
        };

        var symbolId = await UpsertSymbolAsync(connection, symbol, cancellationToken);
        state.SymbolIdsByStableKey[stableKey] = symbolId;
        state.SymbolIdsByQualifiedName[qualifiedName] = symbolId;

        var decl = new SymbolDecl
        {
            SnapshotId = state.SnapshotId,
            SymbolId = symbolId,
            FileId = fileId,
            ParseContextId = parseContextId,
            Role = "def",
            Span = selectionSpan,
            SignatureText = null,
            TypeText = null,
            DocComment = null,
            IsImplicit = false,
            ProvenanceId = state.SymbolDeclProvenanceId
        };

        var declId = await InsertSymbolDeclAsync(connection, decl, cancellationToken);
        await UpdateCanonicalDeclAsync(connection, symbolId, declId, isDefinition: true, cancellationToken);

        if (parentSymbolId.HasValue)
        {
            var relation = new Relation
            {
                SnapshotId = state.SnapshotId,
                ParseContextId = parseContextId,
                FromSymbolId = parentSymbolId.Value,
                ToSymbolId = symbolId,
                Kind = "contains",
                ProvenanceId = state.RelationProvenanceId
            };
            await InsertRelationAsync(connection, relation, cancellationToken);
        }

        AddSymbolSeedIfNew(
            state,
            new SymbolSeed(symbolId, stableKey, filePath, fileUri, selectionSpan.StartLine, selectionSpan.StartCharacter, lspKind));

        if (symbolElement.TryGetProperty("children", out var childrenElement) && childrenElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in childrenElement.EnumerateArray())
            {
                await ProcessDocumentSymbolElementAsync(
                    connection,
                    state,
                    child,
                    filePath,
                    fileUri,
                    fileId,
                    parseContextId,
                    symbolId,
                    qualifiedName,
                    cancellationToken);
            }
        }
    }

    private async Task ProcessSymbolInformationAsync(
        DbConnection connection,
        ExtractionState state,
        JsonElement symbolElement,
        string? defaultFilePath,
        string? defaultFileUri,
        long? parentSymbolId,
        string? parentQualifiedName,
        CancellationToken cancellationToken)
    {
        if (!symbolElement.TryGetProperty("name", out var nameElement) ||
            !symbolElement.TryGetProperty("kind", out var kindElement))
        {
            return;
        }

        if (!symbolElement.TryGetProperty("location", out var locationElement))
        {
            return;
        }

        var name = nameElement.GetString() ?? string.Empty;
        var lspKind = kindElement.GetInt32();
        var kind = MapLspKind(lspKind);
        var containerName = symbolElement.TryGetProperty("containerName", out var containerElement) && containerElement.ValueKind == JsonValueKind.String
            ? containerElement.GetString()
            : null;
        var effectiveParentQualifiedName = containerName ?? parentQualifiedName;
        var qualifiedName = string.IsNullOrWhiteSpace(effectiveParentQualifiedName) ? name : $"{effectiveParentQualifiedName}::{name}";

        string? locationUri = null;
        JsonElement rangeElement;

        if (locationElement.TryGetProperty("uri", out var uriElement) &&
            locationElement.TryGetProperty("range", out var directRangeElement))
        {
            locationUri = uriElement.GetString();
            rangeElement = directRangeElement;
        }
        else if (locationElement.TryGetProperty("targetUri", out var targetUriElement) &&
                 locationElement.TryGetProperty("targetRange", out var targetRangeElement))
        {
            locationUri = targetUriElement.GetString();
            rangeElement = targetRangeElement;
        }
        else
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(locationUri))
        {
            locationUri = defaultFileUri;
        }

        if (string.IsNullOrWhiteSpace(locationUri))
        {
            return;
        }

        var filePath = UriToPath(locationUri!);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            filePath = defaultFilePath;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var fileId = await EnsureFileAsync(connection, state, filePath!, cancellationToken);
        var parseContextId = await EnsureParseContextForFileAsync(connection, state, filePath!, fileId, cancellationToken);
        var span = ParseSpan(rangeElement);
        var stableKey = BuildStableKey(qualifiedName, kind, filePath!, span);

        long? resolvedParentId = parentSymbolId;
        if (resolvedParentId is null && !string.IsNullOrWhiteSpace(containerName) &&
            state.SymbolIdsByQualifiedName.TryGetValue(containerName!, out var containerSymbolId))
        {
            resolvedParentId = containerSymbolId;
        }

        var symbol = new Symbol
        {
            SnapshotId = state.SnapshotId,
            StableKey = stableKey,
            Kind = kind,
            Name = name,
            QualifiedName = qualifiedName,
            ParentSymbolId = resolvedParentId,
            Visibility = "unknown",
            TemplateKind = "non_template",
            IsExported = false,
            ProvenanceId = state.SymbolProvenanceId
        };

        var symbolId = await UpsertSymbolAsync(connection, symbol, cancellationToken);
        state.SymbolIdsByStableKey[stableKey] = symbolId;
        state.SymbolIdsByQualifiedName[qualifiedName] = symbolId;

        var decl = new SymbolDecl
        {
            SnapshotId = state.SnapshotId,
            SymbolId = symbolId,
            FileId = fileId,
            ParseContextId = parseContextId,
            Role = "decl",
            Span = span,
            SignatureText = null,
            TypeText = null,
            DocComment = null,
            IsImplicit = false,
            ProvenanceId = state.SymbolDeclProvenanceId
        };
        var declId = await InsertSymbolDeclAsync(connection, decl, cancellationToken);
        await UpdateCanonicalDeclAsync(connection, symbolId, declId, isDefinition: false, cancellationToken);

        if (resolvedParentId.HasValue)
        {
            var relation = new Relation
            {
                SnapshotId = state.SnapshotId,
                ParseContextId = parseContextId,
                FromSymbolId = resolvedParentId.Value,
                ToSymbolId = symbolId,
                Kind = "contains",
                ProvenanceId = state.RelationProvenanceId
            };
            await InsertRelationAsync(connection, relation, cancellationToken);
        }

        AddSymbolSeedIfNew(
            state,
            new SymbolSeed(symbolId, stableKey, filePath!, locationUri!, span.StartLine, span.StartCharacter, lspKind));
    }

    private async Task<long> EnsureSymbolFromHierarchyItemAsync(
        DbConnection connection,
        ExtractionState state,
        JsonElement hierarchyItem,
        string filePath,
        long fileId,
        long parseContextId,
        CancellationToken cancellationToken)
    {
        var name = hierarchyItem.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
        var lspKind = hierarchyItem.TryGetProperty("kind", out var kindElement) ? kindElement.GetInt32() : 12;
        var kind = MapLspKind(lspKind);
        var detail = hierarchyItem.TryGetProperty("detail", out var detailElement) && detailElement.ValueKind == JsonValueKind.String
            ? detailElement.GetString()
            : null;
        var qualifiedName = !string.IsNullOrWhiteSpace(detail) ? $"{detail}::{name}" : name;

        var range = hierarchyItem.TryGetProperty("selectionRange", out var selectionRange)
            ? ParseSpan(selectionRange)
            : hierarchyItem.TryGetProperty("range", out var rangeElement)
                ? ParseSpan(rangeElement)
                : new FactSpan { StartLine = 0, StartCharacter = 0, EndLine = 0, EndCharacter = 1 };

        var stableKey = BuildStableKey(qualifiedName, kind, filePath, range);
        if (state.SymbolIdsByStableKey.TryGetValue(stableKey, out var existingSymbolId))
        {
            return existingSymbolId;
        }

        var symbol = new Symbol
        {
            SnapshotId = state.SnapshotId,
            StableKey = stableKey,
            Kind = kind,
            Name = name,
            QualifiedName = qualifiedName,
            Visibility = "unknown",
            TemplateKind = "non_template",
            IsExported = false,
            ProvenanceId = state.SymbolProvenanceId
        };
        var symbolId = await UpsertSymbolAsync(connection, symbol, cancellationToken);
        state.SymbolIdsByStableKey[stableKey] = symbolId;
        state.SymbolIdsByQualifiedName[qualifiedName] = symbolId;

        var decl = new SymbolDecl
        {
            SnapshotId = state.SnapshotId,
            SymbolId = symbolId,
            FileId = fileId,
            ParseContextId = parseContextId,
            Role = "decl",
            Span = range,
            SignatureText = null,
            TypeText = null,
            DocComment = null,
            IsImplicit = false,
            ProvenanceId = state.SymbolDeclProvenanceId
        };
        var declId = await InsertSymbolDeclAsync(connection, decl, cancellationToken);
        await UpdateCanonicalDeclAsync(connection, symbolId, declId, isDefinition: false, cancellationToken);
        return symbolId;
    }

    private async Task EnsureFileOpenAsync(ExtractionState state, string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        if (!state.OpenedFiles.Add(path))
        {
            return;
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var openParams = new
        {
            textDocument = new
            {
                uri = PathToFileUri(path),
                languageId = "cpp",
                version = 1,
                text
            }
        };
        await _clangd.SendNotificationAsync("textDocument/didOpen", openParams, cancellationToken);
    }

    private async Task<long> EnsureFileAsync(
        DbConnection connection,
        ExtractionState state,
        string rawPath,
        CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizePath(rawPath);
        if (state.FileIdsByPath.TryGetValue(normalizedPath, out var existingFileId))
        {
            return existingFileId;
        }

        var isSyntheticExternal = normalizedPath.StartsWith("external://", StringComparison.Ordinal);
        var fileExists = !isSyntheticExternal && File.Exists(normalizedPath);
        var isExternal = isSyntheticExternal || !IsPathInWorkspace(normalizedPath, _clangd.WorkspaceRoot);
        var language = InferLanguage(normalizedPath);
        var sizeBytes = fileExists ? new FileInfo(normalizedPath).Length : 0;
        var lineCount = fileExists ? CountFileLines(normalizedPath) : 0;
        var contentHash = fileExists ? await HashFileAsync(normalizedPath, cancellationToken) : ComputeSha256(normalizedPath);
        var displayPath = isSyntheticExternal
            ? normalizedPath
            : isExternal
                ? normalizedPath
                : Path.GetRelativePath(_clangd.WorkspaceRoot, normalizedPath).Replace('\\', '/');

        const string sql = """
            INSERT INTO file (
                snapshot_id, path, real_path, content_hash, language, is_generated, is_external, size_bytes, line_count
            )
            VALUES (
                @snapshot_id, @path, @real_path, @content_hash, @language, @is_generated, @is_external, @size_bytes, @line_count
            )
            ON CONFLICT (snapshot_id, path) DO UPDATE SET
                real_path = EXCLUDED.real_path,
                content_hash = EXCLUDED.content_hash,
                language = EXCLUDED.language,
                is_generated = EXCLUDED.is_generated,
                is_external = EXCLUDED.is_external,
                size_bytes = EXCLUDED.size_bytes,
                line_count = EXCLUDED.line_count
            RETURNING file_id;
            """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        var realPathValue = fileExists ? (object)normalizedPath : DBNull.Value;
        cmd.Parameters.AddWithValue("snapshot_id", state.SnapshotId);
        cmd.Parameters.AddWithValue("path", displayPath);
        cmd.Parameters.AddWithValue("real_path", realPathValue);
        cmd.Parameters.AddWithValue("content_hash", contentHash);
        cmd.Parameters.AddWithValue("language", language);
        cmd.Parameters.AddWithValue("is_generated", IsLikelyGeneratedPath(normalizedPath));
        cmd.Parameters.AddWithValue("is_external", isExternal);
        cmd.Parameters.AddWithValue("size_bytes", sizeBytes);
        cmd.Parameters.AddWithValue("line_count", lineCount);

        var fileId = (long)(await cmd.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Failed to insert file."));
        state.FileIdsByPath[normalizedPath] = fileId;
        return fileId;
    }

    private async Task<long> EnsureParseContextForFileAsync(
        DbConnection connection,
        ExtractionState state,
        string filePath,
        long fileId,
        CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizePath(filePath);
        if (state.ParseContextByPath.TryGetValue(normalizedPath, out var parseContextId))
        {
            return parseContextId;
        }

        if (state.ParseContextByFileId.TryGetValue(fileId, out parseContextId))
        {
            state.ParseContextByPath[normalizedPath] = parseContextId;
            return parseContextId;
        }

        if (state.DefaultBuildConfigId == 0)
        {
            throw new InvalidOperationException("No build config available for parse context creation.");
        }

        var parseContext = new ParseContext
        {
            SnapshotId = state.SnapshotId,
            FileId = fileId,
            BuildConfigId = state.DefaultBuildConfigId,
            ContextKind = "header_view",
            PpFingerprint = null,
            BorrowedFromBuildConfigId = state.DefaultBuildConfigId,
            Confidence = 0.600m,
            ParseErrorsJson = EmptyArrayJson
        };

        parseContextId = await InsertParseContextAsync(connection, parseContext, cancellationToken);
        state.ParseContextByFileId[fileId] = parseContextId;
        state.ParseContextByPath[normalizedPath] = parseContextId;
        return parseContextId;
    }

    private async Task<long> CreateSnapshotAsync(
        DbConnection connection,
        IReadOnlyList<CompileCommandEntry> compileCommands,
        long? parentSnapshotId,
        string kind,
        CancellationToken cancellationToken)
    {
        var snapshot = new Snapshot
        {
            RepoRoot = NormalizePath(_clangd.WorkspaceRoot),
            VcsCommit = TryGetGitCommit(_clangd.WorkspaceRoot),
            WorkspaceHash = BuildWorkspaceHash(compileCommands),
            ParentSnapshotId = parentSnapshotId,
            Kind = kind,
            IndexStatus = "in_progress",
            IsArchived = false
        };

        return await InsertSnapshotAsync(connection, snapshot, cancellationToken);
    }

    private async Task<long?> GetCurrentSnapshotIdAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT snapshot_id
            FROM snapshot
            WHERE repo_root = @repo_root
              AND is_archived = FALSE
            ORDER BY created_at DESC, snapshot_id DESC
            LIMIT 1;
            """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("repo_root", NormalizePath(_clangd.WorkspaceRoot));
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result == null ? null : Convert.ToInt64(result);
    }

    private async Task<int> ArchiveOldSnapshotsAsync(
        DbConnection connection,
        int keepLatest,
        CancellationToken cancellationToken)
    {
        var effectiveKeepLatest = Math.Max(1, keepLatest);
        const string sql = """
            WITH ranked AS (
                SELECT
                    snapshot_id,
                    ROW_NUMBER() OVER (
                        PARTITION BY repo_root
                        ORDER BY created_at DESC, snapshot_id DESC
                    ) AS rn
                FROM snapshot
                WHERE is_archived = FALSE
            )
            UPDATE snapshot s
            SET
                is_archived = TRUE,
                archived_at = now(),
                last_updated_at = now()
            WHERE s.snapshot_id IN (
                SELECT snapshot_id
                FROM ranked
                WHERE rn > @keep_latest
            );
            """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("keep_latest", effectiveKeepLatest);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateSnapshotStatusAsync(
        DbConnection connection,
        long snapshotId,
        string indexStatus,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE snapshot
            SET
                index_status = @index_status,
                last_updated_at = now()
            WHERE snapshot_id = @snapshot_id;
            """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", snapshotId);
        cmd.Parameters.AddWithValue("index_status", indexStatus);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> InsertSnapshotAsync(
        DbConnection connection,
        Snapshot snapshot,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO snapshot (
                repo_root,
                vcs_commit,
                workspace_hash,
                parent_snapshot_id,
                kind,
                index_status,
                is_archived,
                archived_at
            )
            VALUES (
                @repo_root,
                @vcs_commit,
                @workspace_hash,
                @parent_snapshot_id,
                @kind,
                @index_status,
                @is_archived,
                @archived_at
            )
            RETURNING snapshot_id;
            """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("repo_root", snapshot.RepoRoot);
        cmd.Parameters.AddWithValue("vcs_commit", (object?)snapshot.VcsCommit ?? DBNull.Value);
        cmd.Parameters.AddWithValue("workspace_hash", snapshot.WorkspaceHash);
        cmd.Parameters.AddWithValue("parent_snapshot_id", (object?)snapshot.ParentSnapshotId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("kind", snapshot.Kind);
        cmd.Parameters.AddWithValue("index_status", snapshot.IndexStatus);
        cmd.Parameters.AddWithValue("is_archived", snapshot.IsArchived);
        cmd.Parameters.AddWithValue("archived_at", (object?)snapshot.ArchivedAt ?? DBNull.Value);
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Failed to insert snapshot."));
    }

    private async Task<long> InsertBuildConfigAsync(
        DbConnection connection,
        BuildConfig buildConfig,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO build_config (
                snapshot_id, source_file_id, output_path, working_directory, argv_json, argv_hash, compiler,
                language_standard, target_triple, sysroot, defines_hash, include_paths_hash, command_origin, command_text
            )
            VALUES (
                @snapshot_id, @source_file_id, @output_path, @working_directory, @argv_json, @argv_hash, @compiler,
                @language_standard, @target_triple, @sysroot, @defines_hash, @include_paths_hash, @command_origin, @command_text
            )
            ON CONFLICT (snapshot_id, source_file_id, argv_hash, output_path) DO UPDATE SET
                command_text = EXCLUDED.command_text
            RETURNING build_config_id;
            """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", buildConfig.SnapshotId);
        cmd.Parameters.AddWithValue("source_file_id", buildConfig.SourceFileId);
        cmd.Parameters.AddWithValue("output_path", buildConfig.OutputPath ?? string.Empty);
        cmd.Parameters.AddWithValue("working_directory", buildConfig.WorkingDirectory);
        cmd.Parameters.AddWithValue("argv_json", NpgsqlDbType.Jsonb, buildConfig.ArgvJson.GetRawText());
        cmd.Parameters.AddWithValue("argv_hash", buildConfig.ArgvHash);
        cmd.Parameters.AddWithValue("compiler", (object?)buildConfig.Compiler ?? DBNull.Value);
        cmd.Parameters.AddWithValue("language_standard", (object?)buildConfig.LanguageStandard ?? DBNull.Value);
        cmd.Parameters.AddWithValue("target_triple", (object?)buildConfig.TargetTriple ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sysroot", (object?)buildConfig.Sysroot ?? DBNull.Value);
        cmd.Parameters.AddWithValue("defines_hash", (object?)buildConfig.DefinesHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("include_paths_hash", (object?)buildConfig.IncludePathsHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("command_origin", buildConfig.CommandOrigin);
        cmd.Parameters.AddWithValue("command_text", (object?)buildConfig.CommandText ?? DBNull.Value);
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Failed to insert build config."));
    }

    private async Task<long> InsertParseContextAsync(
        DbConnection connection,
        ParseContext parseContext,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO parse_context (
                snapshot_id, file_id, build_config_id, context_kind, pp_fingerprint,
                borrowed_from_build_config_id, confidence, parse_errors_json
            )
            VALUES (
                @snapshot_id, @file_id, @build_config_id, @context_kind, @pp_fingerprint,
                @borrowed_from_build_config_id, @confidence, @parse_errors_json
            )
            RETURNING parse_context_id;
            """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", parseContext.SnapshotId);
        cmd.Parameters.AddWithValue("file_id", parseContext.FileId);
        cmd.Parameters.AddWithValue("build_config_id", parseContext.BuildConfigId);
        cmd.Parameters.AddWithValue("context_kind", parseContext.ContextKind);
        cmd.Parameters.AddWithValue("pp_fingerprint", (object?)parseContext.PpFingerprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("borrowed_from_build_config_id", (object?)parseContext.BorrowedFromBuildConfigId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("confidence", parseContext.Confidence);
        cmd.Parameters.AddWithValue("parse_errors_json", NpgsqlDbType.Jsonb, parseContext.ParseErrorsJson.GetRawText());
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Failed to insert parse context."));
    }

    private async Task<long> RecordProvenanceAsync(
        DbConnection connection,
        string extractorName,
        string extractionMethod,
        string exactness,
        decimal confidence,
        JsonElement? evidenceJson,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO provenance (extractor_name, extraction_method, exactness, confidence, evidence_json)
            VALUES (@extractor_name, @extraction_method, @exactness, @confidence, @evidence_json)
            RETURNING provenance_id;
            """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("extractor_name", extractorName);
        cmd.Parameters.AddWithValue("extraction_method", extractionMethod);
        cmd.Parameters.AddWithValue("exactness", exactness);
        cmd.Parameters.AddWithValue("confidence", confidence);
        cmd.Parameters.AddWithValue("evidence_json", NpgsqlDbType.Jsonb, (evidenceJson ?? EmptyObjectJson).GetRawText());
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Failed to insert provenance."));
    }

    private async Task<long> UpsertSymbolAsync(
        DbConnection connection,
        Symbol symbol,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO symbol (
                snapshot_id, stable_key, kind, name, qualified_name, parent_symbol_id,
                visibility, template_kind, canonical_decl_id, canonical_def_id, is_exported, provenance_id
            )
            VALUES (
                @snapshot_id, @stable_key, @kind, @name, @qualified_name, @parent_symbol_id,
                @visibility, @template_kind, @canonical_decl_id, @canonical_def_id, @is_exported, @provenance_id
            )
            ON CONFLICT (snapshot_id, stable_key) DO UPDATE SET
                kind = EXCLUDED.kind,
                name = EXCLUDED.name,
                qualified_name = EXCLUDED.qualified_name,
                parent_symbol_id = COALESCE(EXCLUDED.parent_symbol_id, symbol.parent_symbol_id),
                visibility = COALESCE(EXCLUDED.visibility, symbol.visibility),
                template_kind = EXCLUDED.template_kind,
                is_exported = EXCLUDED.is_exported,
                provenance_id = EXCLUDED.provenance_id
            RETURNING symbol_id;
            """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", symbol.SnapshotId);
        cmd.Parameters.AddWithValue("stable_key", symbol.StableKey);
        cmd.Parameters.AddWithValue("kind", symbol.Kind);
        cmd.Parameters.AddWithValue("name", symbol.Name);
        cmd.Parameters.AddWithValue("qualified_name", symbol.QualifiedName);
        cmd.Parameters.AddWithValue("parent_symbol_id", (object?)symbol.ParentSymbolId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("visibility", (object?)symbol.Visibility ?? DBNull.Value);
        cmd.Parameters.AddWithValue("template_kind", symbol.TemplateKind);
        cmd.Parameters.AddWithValue("canonical_decl_id", (object?)symbol.CanonicalDeclId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("canonical_def_id", (object?)symbol.CanonicalDefId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("is_exported", symbol.IsExported);
        cmd.Parameters.AddWithValue("provenance_id", symbol.ProvenanceId);
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Failed to upsert symbol."));
    }

    private async Task<long> InsertSymbolDeclAsync(
        DbConnection connection,
        SymbolDecl decl,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO symbol_decl (
                snapshot_id, symbol_id, file_id, parse_context_id, role, span, signature_text, type_text, doc_comment, is_implicit, provenance_id
            )
            VALUES (
                @snapshot_id, @symbol_id, @file_id, @parse_context_id, @role, @span, @signature_text, @type_text, @doc_comment, @is_implicit, @provenance_id
            )
            RETURNING decl_id;
            """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", decl.SnapshotId);
        cmd.Parameters.AddWithValue("symbol_id", decl.SymbolId);
        cmd.Parameters.AddWithValue("file_id", decl.FileId);
        cmd.Parameters.AddWithValue("parse_context_id", (object?)decl.ParseContextId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("role", decl.Role);
        cmd.Parameters.AddWithValue("span", NpgsqlDbType.Jsonb, SerializeSpan(decl.Span));
        cmd.Parameters.AddWithValue("signature_text", (object?)decl.SignatureText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("type_text", (object?)decl.TypeText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("doc_comment", (object?)decl.DocComment ?? DBNull.Value);
        cmd.Parameters.AddWithValue("is_implicit", decl.IsImplicit);
        cmd.Parameters.AddWithValue("provenance_id", decl.ProvenanceId);
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Failed to insert symbol decl."));
    }

    private async Task UpdateCanonicalDeclAsync(
        DbConnection connection,
        long symbolId,
        long declId,
        bool isDefinition,
        CancellationToken cancellationToken)
    {
        var sql = isDefinition
            ? "UPDATE symbol SET canonical_def_id = COALESCE(canonical_def_id, @decl_id) WHERE symbol_id = @symbol_id;"
            : "UPDATE symbol SET canonical_decl_id = COALESCE(canonical_decl_id, @decl_id) WHERE symbol_id = @symbol_id;";

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("decl_id", declId);
        cmd.Parameters.AddWithValue("symbol_id", symbolId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertRelationAsync(
        DbConnection connection,
        Relation relation,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO relation (
                snapshot_id, parse_context_id, from_symbol_id, to_symbol_id, kind, provenance_id
            )
            VALUES (
                @snapshot_id, @parse_context_id, @from_symbol_id, @to_symbol_id, @kind, @provenance_id
            )
            ON CONFLICT (snapshot_id, parse_context_id, from_symbol_id, to_symbol_id, kind) DO NOTHING;
            """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", relation.SnapshotId);
        cmd.Parameters.AddWithValue("parse_context_id", relation.ParseContextId);
        cmd.Parameters.AddWithValue("from_symbol_id", relation.FromSymbolId);
        cmd.Parameters.AddWithValue("to_symbol_id", relation.ToSymbolId);
        cmd.Parameters.AddWithValue("kind", relation.Kind);
        cmd.Parameters.AddWithValue("provenance_id", relation.ProvenanceId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertOccurrenceAsync(
        DbConnection connection,
        Occurrence occurrence,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO occurrence (
                snapshot_id, parse_context_id, file_id, symbol_id, span, role_bits, via_macro, is_implicit, provenance_id
            )
            VALUES (
                @snapshot_id, @parse_context_id, @file_id, @symbol_id, @span, @role_bits, @via_macro, @is_implicit, @provenance_id
            );
            """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", occurrence.SnapshotId);
        cmd.Parameters.AddWithValue("parse_context_id", occurrence.ParseContextId);
        cmd.Parameters.AddWithValue("file_id", occurrence.FileId);
        cmd.Parameters.AddWithValue("symbol_id", occurrence.SymbolId);
        cmd.Parameters.AddWithValue("span", NpgsqlDbType.Jsonb, SerializeSpan(occurrence.Span));
        cmd.Parameters.AddWithValue("role_bits", occurrence.RoleBits);
        cmd.Parameters.AddWithValue("via_macro", occurrence.ViaMacro);
        cmd.Parameters.AddWithValue("is_implicit", occurrence.IsImplicit);
        cmd.Parameters.AddWithValue("provenance_id", occurrence.ProvenanceId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> InsertCallsiteAsync(
        DbConnection connection,
        Callsite callsite,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO callsite (
                snapshot_id, parse_context_id, file_id, caller_symbol_id, span, dispatch_kind, raw_text, provenance_id
            )
            VALUES (
                @snapshot_id, @parse_context_id, @file_id, @caller_symbol_id, @span, @dispatch_kind, @raw_text, @provenance_id
            )
            RETURNING callsite_id;
            """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", callsite.SnapshotId);
        cmd.Parameters.AddWithValue("parse_context_id", callsite.ParseContextId);
        cmd.Parameters.AddWithValue("file_id", callsite.FileId);
        cmd.Parameters.AddWithValue("caller_symbol_id", callsite.CallerSymbolId);
        cmd.Parameters.AddWithValue("span", NpgsqlDbType.Jsonb, SerializeSpan(callsite.Span));
        cmd.Parameters.AddWithValue("dispatch_kind", callsite.DispatchKind);
        cmd.Parameters.AddWithValue("raw_text", (object?)callsite.RawText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("provenance_id", callsite.ProvenanceId);
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Failed to insert callsite."));
    }

    private async Task InsertCallTargetAsync(
        DbConnection connection,
        CallTarget callTarget,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO call_target (
                snapshot_id, callsite_id, callee_symbol_id, rank, resolution_kind, confidence, provenance_id
            )
            VALUES (
                @snapshot_id, @callsite_id, @callee_symbol_id, @rank, @resolution_kind, @confidence, @provenance_id
            )
            ON CONFLICT (snapshot_id, callsite_id, callee_symbol_id, rank) DO NOTHING;
            """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", callTarget.SnapshotId);
        cmd.Parameters.AddWithValue("callsite_id", callTarget.CallsiteId);
        cmd.Parameters.AddWithValue("callee_symbol_id", callTarget.CalleeSymbolId);
        cmd.Parameters.AddWithValue("rank", callTarget.Rank);
        cmd.Parameters.AddWithValue("resolution_kind", callTarget.ResolutionKind);
        cmd.Parameters.AddWithValue("confidence", callTarget.Confidence);
        cmd.Parameters.AddWithValue("provenance_id", callTarget.ProvenanceId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertFileDependencyIfNewAsync(
        DbConnection connection,
        ExtractionState state,
        FileDependency dependency,
        CancellationToken cancellationToken)
    {
        var dedupeKey = $"{dependency.ParseContextId}|{dependency.FromFileId}|{dependency.ToFileId}|{dependency.Span.StartLine}|{dependency.Span.StartCharacter}|{dependency.DirectiveKind}|{dependency.LiteralText}";
        if (!state.FileDependencyDedupKeys.Add(dedupeKey))
        {
            return;
        }

        const string sql = """
            INSERT INTO file_dependency (
                snapshot_id, parse_context_id, from_file_id, to_file_id, directive_kind,
                literal_text, span, is_active, provenance_id
            )
            VALUES (
                @snapshot_id, @parse_context_id, @from_file_id, @to_file_id, @directive_kind,
                @literal_text, @span, @is_active, @provenance_id
            );
            """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", dependency.SnapshotId);
        cmd.Parameters.AddWithValue("parse_context_id", dependency.ParseContextId);
        cmd.Parameters.AddWithValue("from_file_id", dependency.FromFileId);
        cmd.Parameters.AddWithValue("to_file_id", dependency.ToFileId);
        cmd.Parameters.AddWithValue("directive_kind", dependency.DirectiveKind);
        cmd.Parameters.AddWithValue("literal_text", (object?)dependency.LiteralText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("span", NpgsqlDbType.Jsonb, SerializeSpan(dependency.Span));
        cmd.Parameters.AddWithValue("is_active", dependency.IsActive);
        cmd.Parameters.AddWithValue("provenance_id", dependency.ProvenanceId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static FactSpan ParseSpan(JsonElement rangeElement)
    {
        var start = rangeElement.GetProperty("start");
        var end = rangeElement.GetProperty("end");
        return new FactSpan
        {
            StartLine = start.GetProperty("line").GetInt32(),
            StartCharacter = start.GetProperty("character").GetInt32(),
            EndLine = end.GetProperty("line").GetInt32(),
            EndCharacter = end.GetProperty("character").GetInt32()
        };
    }

    private static string SerializeSpan(FactSpan span)
    {
        return JsonSerializer.Serialize(new
        {
            start = new { line = span.StartLine, character = span.StartCharacter },
            end = new { line = span.EndLine, character = span.EndCharacter }
        });
    }

    private static bool IsFunctionLike(SymbolSeed seed)
    {
        return seed.LspKind is 6 or 9 or 12 or 24;
    }

    private static void AddSymbolSeedIfNew(ExtractionState state, SymbolSeed seed)
    {
        if (state.SymbolSeedStableKeys.Add(seed.StableKey))
        {
            state.SymbolSeeds.Add(seed);
        }
    }

    private static string MapLspKind(int lspKind)
    {
        return lspKind switch
        {
            3 => "namespace",
            5 => "class",
            6 => "method",
            8 => "field",
            9 => "constructor",
            10 => "enum",
            12 => "function",
            13 => "var",
            22 => "struct",
            24 => "operator",
            _ => $"kind_{lspKind}"
        };
    }

    private async Task<List<CompileCommandEntry>> LoadCompileCommandsAsync(CancellationToken cancellationToken)
    {
        var compileCommandsPath = _clangd.CompileCommandsPath;
        if (string.IsNullOrWhiteSpace(compileCommandsPath) || !File.Exists(compileCommandsPath))
        {
            return new List<CompileCommandEntry>();
        }

        using var stream = File.OpenRead(compileCommandsPath);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new List<CompileCommandEntry>();
        }

        var result = new List<CompileCommandEntry>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("file", out var fileElement) || fileElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var directory = item.TryGetProperty("directory", out var directoryElement) && directoryElement.ValueKind == JsonValueKind.String
                ? directoryElement.GetString() ?? _clangd.WorkspaceRoot
                : _clangd.WorkspaceRoot;

            var sourceFile = fileElement.GetString() ?? string.Empty;
            if (!Path.IsPathRooted(sourceFile))
            {
                sourceFile = Path.Combine(directory, sourceFile);
            }
            sourceFile = NormalizePath(sourceFile);

            var output = item.TryGetProperty("output", out var outputElement) && outputElement.ValueKind == JsonValueKind.String
                ? outputElement.GetString()
                : null;

            var argv = new List<string>();
            string? commandText = null;
            if (item.TryGetProperty("arguments", out var argumentsElement) && argumentsElement.ValueKind == JsonValueKind.Array)
            {
                argv.AddRange(argumentsElement.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => !string.IsNullOrWhiteSpace(x)));
                commandText = string.Join(" ", argv);
            }
            else if (item.TryGetProperty("command", out var commandElement) && commandElement.ValueKind == JsonValueKind.String)
            {
                commandText = commandElement.GetString();
                argv.AddRange(SplitCommand(commandText ?? string.Empty));
            }

            if (argv.Count == 0)
            {
                argv = new List<string> { "clang++", "-c", sourceFile };
                commandText = string.Join(" ", argv);
            }

            result.Add(new CompileCommandEntry(
                SourceFile: sourceFile,
                WorkingDirectory: NormalizePath(directory),
                OutputPath: output,
                Argv: argv,
                CommandText: commandText,
                Origin: "exact"));
        }

        return result;
    }

    private IEnumerable<CompileCommandEntry> DiscoverFallbackCompileCommands()
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".c", ".cc", ".cpp", ".cxx", ".c++", ".m", ".mm"
        };

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_clangd.WorkspaceRoot, "*.*", SearchOption.AllDirectories)
                .Where(path => extensions.Contains(Path.GetExtension(path)));
        }
        catch
        {
            files = Array.Empty<string>();
        }

        foreach (var file in files)
        {
            var normalizedFile = NormalizePath(file);
            yield return new CompileCommandEntry(
                SourceFile: normalizedFile,
                WorkingDirectory: NormalizePath(Path.GetDirectoryName(normalizedFile) ?? _clangd.WorkspaceRoot),
                OutputPath: null,
                Argv: new List<string> { "clang++", "-c", normalizedFile },
                CommandText: $"clang++ -c {normalizedFile}",
                Origin: "inferred");
        }
    }

    private static string ParseArgValue(IReadOnlyList<string> argv, params string[] argNames)
    {
        for (var i = 0; i < argv.Count; i++)
        {
            foreach (var argName in argNames)
            {
                var arg = argv[i];
                if (arg.Equals(argName, StringComparison.Ordinal) && i + 1 < argv.Count)
                {
                    return argv[i + 1];
                }

                if (arg.StartsWith(argName + "=", StringComparison.Ordinal))
                {
                    return arg[(argName.Length + 1)..];
                }
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> CollectIncludeArgs(IReadOnlyList<string> argv)
    {
        for (var i = 0; i < argv.Count; i++)
        {
            var arg = argv[i];
            if (arg.StartsWith("-I", StringComparison.Ordinal))
            {
                yield return arg;
                continue;
            }

            if (arg.Equals("-isystem", StringComparison.Ordinal) && i + 1 < argv.Count)
            {
                yield return $"{arg}:{argv[i + 1]}";
                i++;
            }
        }
    }

    private static IReadOnlyList<string> SplitCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return Array.Empty<string>();
        }

        var tokens = new List<string>();
        foreach (Match match in Regex.Matches(command, @"(?:""[^""]*""|'[^']*'|\S+)"))
        {
            var token = match.Value.Trim();
            if ((token.StartsWith("\"", StringComparison.Ordinal) && token.EndsWith("\"", StringComparison.Ordinal)) ||
                (token.StartsWith("'", StringComparison.Ordinal) && token.EndsWith("'", StringComparison.Ordinal)))
            {
                token = token[1..^1];
            }

            if (!string.IsNullOrWhiteSpace(token))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    private static string InferLanguage(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.ToLowerInvariant() switch
        {
            ".c" => "c",
            ".h" or ".hh" or ".hpp" or ".hxx" or ".inl" => "header",
            ".ixx" => "module_interface",
            ".cppm" => "module_impl",
            _ => "c++"
        };
    }

    private static bool IsLikelyGeneratedPath(string path)
    {
        var normalized = path.ToLowerInvariant();
        return normalized.Contains("/generated/", StringComparison.Ordinal) ||
               normalized.Contains("\\generated\\", StringComparison.Ordinal) ||
               normalized.EndsWith(".pb.h", StringComparison.Ordinal) ||
               normalized.EndsWith(".pb.cc", StringComparison.Ordinal);
    }

    private static int CountFileLines(string path)
    {
        var count = 0;
        using var reader = new StreamReader(path);
        while (reader.ReadLine() != null)
        {
            count++;
        }

        return count;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeSha256(string value)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(value);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string BuildWorkspaceHash(IReadOnlyList<CompileCommandEntry> compileCommands)
    {
        var compileCommandsPath = _clangd.CompileCommandsPath;
        if (!string.IsNullOrWhiteSpace(compileCommandsPath) && File.Exists(compileCommandsPath))
        {
            try
            {
                var compileCommandsHash = ComputeSha256(File.ReadAllText(compileCommandsPath));
                return ComputeSha256($"{NormalizePath(_clangd.WorkspaceRoot)}|{compileCommandsHash}");
            }
            catch
            {
                // Fall back to normalized command fingerprint when compile_commands cannot be read.
            }
        }

        var fingerprint = new StringBuilder();
        fingerprint.Append(NormalizePath(_clangd.WorkspaceRoot));
        foreach (var command in compileCommands.OrderBy(x => x.SourceFile, StringComparer.Ordinal))
        {
            fingerprint.Append('|');
            fingerprint.Append(command.SourceFile);
            fingerprint.Append('|');
            fingerprint.Append(command.CommandText);
        }

        return ComputeSha256(fingerprint.ToString());
    }

    private static string? TryGetGitCommit(string workspaceRoot)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "rev-parse HEAD",
                    WorkingDirectory = workspaceRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(2000);
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPathInWorkspace(string path, string workspaceRoot)
    {
        var normalizedPath = NormalizePath(path);
        var normalizedRoot = NormalizePath(workspaceRoot).TrimEnd('/');
        return normalizedPath.Equals(normalizedRoot, StringComparison.Ordinal) ||
               normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.Ordinal);
    }

    private static string NormalizePath(string path)
    {
        if (path.StartsWith("external://", StringComparison.Ordinal))
        {
            return path;
        }

        return Path.GetFullPath(path).Replace('\\', '/');
    }

    private static string PathToFileUri(string path)
    {
        return new Uri(path).AbsoluteUri;
    }

    private static bool UriLooksLikeFile(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile;
    }

    private static string UriToPath(string uriOrPath)
    {
        if (Uri.TryCreate(uriOrPath, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            return NormalizePath(uri.LocalPath);
        }

        if (Path.IsPathRooted(uriOrPath))
        {
            return NormalizePath(uriOrPath);
        }

        return uriOrPath.StartsWith("external://", StringComparison.Ordinal)
            ? uriOrPath
            : BuildExternalIncludePath(uriOrPath);
    }

    private static string BuildStableKey(string qualifiedName, string kind, string filePath, FactSpan span)
    {
        return $"{qualifiedName}|{kind}|{NormalizePath(filePath)}|{span.StartLine}:{span.StartCharacter}";
    }

    private static string InferDirectiveKind(string lineText)
    {
        if (lineText.Contains("#import", StringComparison.Ordinal))
        {
            return "import";
        }

        if (lineText.TrimStart().StartsWith("import ", StringComparison.Ordinal))
        {
            return "module_import";
        }

        return "include";
    }

    private static string? ExtractIncludeLiteral(string lineText)
    {
        var match = IncludeRegex.Match(lineText);
        return match.Success ? match.Groups[3].Value : null;
    }

    private static string ResolveIncludePath(string sourcePath, string includeLiteral)
    {
        var sourceDir = Path.GetDirectoryName(sourcePath) ?? sourcePath;
        var candidate = NormalizePath(Path.Combine(sourceDir, includeLiteral));
        if (File.Exists(candidate))
        {
            return candidate;
        }

        return BuildExternalIncludePath(includeLiteral);
    }

    private static string BuildExternalIncludePath(string includeLiteral)
    {
        return $"external://{includeLiteral.Replace('\\', '/').Trim()}";
    }

    private sealed record CompileCommandEntry(
        string SourceFile,
        string WorkingDirectory,
        string? OutputPath,
        IReadOnlyList<string> Argv,
        string? CommandText,
        string Origin);

    private sealed record SymbolSeed(
        long SymbolId,
        string StableKey,
        string FilePath,
        string FileUri,
        int Line,
        int Character,
        int LspKind);

    private sealed class ExtractionState
    {
        public ExtractionState(long snapshotId)
        {
            SnapshotId = snapshotId;
        }

        public long SnapshotId { get; }
        public long SymbolProvenanceId { get; set; }
        public long SymbolDeclProvenanceId { get; set; }
        public long OccurrenceProvenanceId { get; set; }
        public long CallProvenanceId { get; set; }
        public long CallTargetProvenanceId { get; set; }
        public long DependencyProvenanceId { get; set; }
        public long RelationProvenanceId { get; set; }
        public long DefaultBuildConfigId { get; set; }
        public Dictionary<string, long> FileIdsByPath { get; } = new(StringComparer.Ordinal);
        public Dictionary<long, long> ParseContextByFileId { get; } = new();
        public Dictionary<string, long> ParseContextByPath { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> SymbolIdsByStableKey { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> SymbolIdsByQualifiedName { get; } = new(StringComparer.Ordinal);
        public List<SymbolSeed> SymbolSeeds { get; } = new();
        public HashSet<string> SymbolSeedStableKeys { get; } = new(StringComparer.Ordinal);
        public HashSet<string> OpenedFiles { get; } = new(StringComparer.Ordinal);
        public HashSet<string> OccurrenceDedupKeys { get; } = new(StringComparer.Ordinal);
        public HashSet<string> FileDependencyDedupKeys { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CallsiteDedupKeys { get; } = new(StringComparer.Ordinal);
        public List<string> SourceFiles { get; } = new();
    }
}
