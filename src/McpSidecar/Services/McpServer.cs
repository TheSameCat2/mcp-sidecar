using System.Linq;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using McpSidecar.Services.Database;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpSidecar.Services;

/// <summary>
/// MCP server implementation using stdio transport.
/// </summary>
public class McpServer
{
    private const long RoleDeclarationBit = 1 << 3;
    private static readonly Regex UnresolvedIdentifierRegex = new(@"[A-Za-z_][A-Za-z0-9_]*(?:::[A-Za-z_][A-Za-z0-9_]*)*", RegexOptions.Compiled);

    private readonly ILogger<McpServer> _logger;
    private readonly ClangdService _clangd;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ISqlBuilder _sqlBuilder;
    private readonly IHostApplicationLifetime _appLifetime;

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true
    };

    public McpServer(
        ILogger<McpServer> logger,
        ClangdService clangd,
        IDbConnectionFactory connectionFactory,
        ISqlBuilder sqlBuilder,
        IHostApplicationLifetime appLifetime)
    {
        _logger = logger;
        _clangd = clangd;
        _connectionFactory = connectionFactory;
        _sqlBuilder = sqlBuilder;
        _appLifetime = appLifetime;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MCP server started, reading from stdin...");

        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        using var reader = new StreamReader(stdin, Encoding.UTF8);
        using var writer = new StreamWriter(stdout, Encoding.UTF8) { AutoFlush = true };
        var stdinReachedEof = false;
        var consecutiveErrors = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var message = await ReadMessageAsync(reader, cancellationToken);
                if (message == null)
                {
                    stdinReachedEof = true;
                    _logger.LogInformation("MCP stdin reached EOF; stopping server loop.");
                    break;
                }

                var response = await HandleMessageAsync(message.Value, cancellationToken);
                if (response != null)
                {
                    await WriteMessageAsync(writer, response.Value, cancellationToken);
                }

                consecutiveErrors = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                _logger.LogError(ex, "Error processing MCP message");
                var backoffMs = Math.Min(1000, consecutiveErrors * 50);
                try
                {
                    await Task.Delay(backoffMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        if (stdinReachedEof && !cancellationToken.IsCancellationRequested)
        {
            _appLifetime.StopApplication();
        }
    }

    private async Task<JsonElement?> ReadMessageAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        // Read headers
        string? line;
        int contentLength = 0;

        while (true)
        {
            line = await reader.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                break;
            }

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                contentLength = int.Parse(line.Split(':')[1].Trim());
            }
        }

        if (contentLength <= 0)
        {
            throw new InvalidDataException("Invalid MCP message: missing or non-positive Content-Length header.");
        }

        // Read content
        var buffer = new char[contentLength];
        var totalRead = 0;
        while (totalRead < contentLength)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(totalRead, contentLength - totalRead), cancellationToken);
            if (read == 0)
            {
                return null;
            }

            totalRead += read;
        }

        var content = new string(buffer, 0, totalRead);

        return JsonSerializer.Deserialize<JsonElement>(content);
    }

    private async Task WriteMessageAsync(StreamWriter writer, JsonElement message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message);
        var content = $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}";
        await writer.WriteAsync(content);
        await writer.FlushAsync(cancellationToken);
        Console.Out.Flush();
    }

    private async Task<JsonElement?> HandleMessageAsync(JsonElement message, CancellationToken cancellationToken)
    {
        var method = message.GetProperty("method").GetString();
        var id = message.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : (int?)null;
        var parameters = message.TryGetProperty("params", out var paramsProp) ? paramsProp : (JsonElement?)null;

        _logger.LogDebug("Received MCP request: {Method}", method);

        return method switch
        {
            "initialize" => CreateResponse(id, new
            {
                protocolVersion = "2024-11-05",
                capabilities = new
                {
                    tools = new { }
                },
                serverInfo = new
                {
                    name = "mcp-sidecar",
                    version = "0.1.0"
                }
            }),
            "tools/list" => CreateResponse(id, new
            {
                tools = GetToolDefinitions()
            }),
            "tools/call" => await HandleToolCallAsync(id, parameters, cancellationToken),
            _ => CreateErrorResponse(id, $"Unknown method: {method}")
        };
    }

    private object[] GetToolDefinitions()
    {
        return new object[]
        {
            new
            {
                name = "symbol_resolve",
                description = "Resolve a symbol name or path to its canonical definition(s). Returns symbol locations with file, line, column, and kind.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string", description = "Symbol name or qualified path to search for (e.g., 'my_function' or 'MyClass::method')" },
                        limit = new { type = "integer", description = "Max results to return (default: 20)" }
                    },
                    required = new[] { "query" }
                }
            },
            new
            {
                name = "symbol_refs",
                description = "Find all references to a symbol at a given location. Returns list of file:line:column where the symbol is used.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        file = new { type = "string", description = "File path (absolute or relative to workspace)" },
                        line = new { type = "integer", description = "Line number (1-based)" },
                        column = new { type = "integer", description = "Column number (1-based)" }
                    },
                    required = new[] { "file", "line", "column" }
                }
            },
            new
            {
                name = "symbol_callers",
                description = "Find callers of a function/method (incoming call hierarchy). Returns functions that call this symbol.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        file = new { type = "string", description = "File path" },
                        line = new { type = "integer", description = "Line number (1-based)" },
                        column = new { type = "integer", description = "Column number (1-based)" },
                        depth = new { type = "integer", description = "Max recursion depth (default: 1, max: 3)" }
                    },
                    required = new[] { "file", "line", "column" }
                }
            },
            new
            {
                name = "symbol_callees",
                description = "Find callees of a function/method (outgoing call hierarchy). Returns functions called by this symbol.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        file = new { type = "string", description = "File path" },
                        line = new { type = "integer", description = "Line number (1-based)" },
                        column = new { type = "integer", description = "Column number (1-based)" },
                        depth = new { type = "integer", description = "Max recursion depth (default: 1, max: 3)" }
                    },
                    required = new[] { "file", "line", "column" }
                }
            },
            new
            {
                name = "symbol_card",
                description = "Get a comprehensive 'card' for a symbol: definition, signature, hover info, callers summary, refs count.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        file = new { type = "string", description = "File path" },
                        line = new { type = "integer", description = "Line number (1-based)" },
                        column = new { type = "integer", description = "Column number (1-based)" }
                    },
                    required = new[] { "file", "line", "column" }
                }
            },
            new
            {
                name = "change_impact",
                description = "Analyze impact of changing a symbol. Returns transitive set of affected files and symbols.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        file = new { type = "string", description = "File path" },
                        line = new { type = "integer", description = "Line number (1-based)" },
                        column = new { type = "integer", description = "Column number (1-based)" },
                        depth = new { type = "integer", description = "Max recursion depth (default: 2, max: 5)" }
                    },
                    required = new[] { "file", "line", "column" }
                }
            },
            new
            {
                name = "cpp.build_explain",
                description = "Explain the compile context for a C/C++ file: selected compile command, origin, key flags, include paths, macros, target, and parse warnings.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        file = new { type = "string", description = "File path (absolute or relative to workspace)." },
                        line = new { type = "integer", description = "Optional 1-based line for context selection." },
                        column = new { type = "integer", description = "Optional 1-based column for context selection." }
                    },
                    required = new[] { "file" }
                }
            },
            new
            {
                name = "cpp.snapshot_status",
                description = "Return indexing snapshot health: current snapshot_id, totals, coverage, freshness, and indexing status.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        snapshot_id = new { type = "integer", description = "Optional explicit snapshot_id. Defaults to latest non-archived snapshot." }
                    }
                }
            },
            new
            {
                name = "cpp.include_explain",
                description = "Explain include usage for a C/C++ file: currently used headers, removable headers, missing headers, and candidate headers for unresolved symbols.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        file = new { type = "string", description = "File path (absolute or relative to workspace)." },
                        unresolved_identifier = new { type = "string", description = "Optional unresolved identifier to suggest candidate headers for." },
                        symbol_id = new { type = "integer", description = "Optional symbol_id to suggest candidate headers for." }
                    },
                    required = new[] { "file" }
                }
            },
            new
            {
                name = "cpp.flow_summary",
                description = "Return precomputed 1-hop value/taint/ownership/escape flows for a callable, including source/target ports and provenance evidence.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        symbol_id = new { type = "integer", description = "Select flows for this symbol_id." },
                        qualified_name = new { type = "string", description = "Select flows by symbol qualified_name when symbol_id is not available." }
                    },
                    anyOf = new object[]
                    {
                        new { required = new[] { "symbol_id" } },
                        new { required = new[] { "qualified_name" } }
                    }
                }
            },
            new
            {
                name = "cpp.context_pack",
                description = "Build a token-budgeted context bundle for LLM consumption with symbol cards, call graph slices, related types, and include context.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        symbol_id = new { type = "integer", description = "Primary symbol_id for the context pack target." },
                        file = new { type = "string", description = "File path (absolute or relative to workspace) to seed primary symbols and include context." },
                        token_budget = new { type = "integer", description = "Approximate max output tokens (default: 8000)." }
                    },
                    anyOf = new object[]
                    {
                        new { required = new[] { "symbol_id" } },
                        new { required = new[] { "file" } }
                    }
                }
            }
        };
    }

    private async Task<JsonElement?> HandleToolCallAsync(int? id, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (parameters == null)
            return CreateErrorResponse(id, "Missing parameters");

        var toolName = parameters.Value.GetProperty("name").GetString();
        var args = parameters.Value.TryGetProperty("arguments", out var argsProp) ? argsProp : default;

        _logger.LogInformation("Tool call: {ToolName}", toolName);

        try
        {
            var result = toolName switch
            {
                "symbol_resolve" => await SymbolResolveAsync(args, cancellationToken),
                "symbol_refs" => await SymbolRefsAsync(args, cancellationToken),
                "symbol_callers" => await SymbolCallersAsync(args, cancellationToken),
                "symbol_callees" => await SymbolCalleesAsync(args, cancellationToken),
                    "symbol_card" => await SymbolCardAsync(args, cancellationToken),
                    "change_impact" => await ChangeImpactAsync(args, cancellationToken),
                    "cpp.build_explain" => await CppBuildExplainAsync(args, cancellationToken),
                    "cpp.snapshot_status" => await CppSnapshotStatusAsync(args, cancellationToken),
                    "cpp.include_explain" => await CppIncludeExplainAsync(args, cancellationToken),
                    "cpp.flow_summary" => await CppFlowSummaryAsync(args, cancellationToken),
                    "cpp.context_pack" => await CppContextPackAsync(args, cancellationToken),
                    _ => $"Unknown tool: {toolName}"
                };

            return CreateResponse(id, new
            {
                content = new[]
                {
                    new { type = "text", text = result }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool call failed: {ToolName}", toolName);
            return CreateErrorResponse(id, $"Tool error: {ex.Message}");
        }
    }

    // Tool implementations

    private async Task<string> CppBuildExplainAsync(JsonElement args, CancellationToken cancellationToken)
    {
        if (!args.TryGetProperty("file", out var fileElement) || fileElement.ValueKind != JsonValueKind.String)
        {
            return "Missing required argument: file";
        }

        var inputFile = fileElement.GetString();
        if (string.IsNullOrWhiteSpace(inputFile))
        {
            return "Argument 'file' cannot be empty";
        }

        var line = args.TryGetProperty("line", out var lineElement) && lineElement.ValueKind == JsonValueKind.Number
            ? lineElement.GetInt32()
            : (int?)null;
        var column = args.TryGetProperty("column", out var columnElement) && columnElement.ValueKind == JsonValueKind.Number
            ? columnElement.GetInt32()
            : (int?)null;

        var normalizedFile = NormalizeInputPath(inputFile);

        var explain = await TryGetBuildExplainFromPostgresAsync(normalizedFile, cancellationToken);
        if (explain == null)
        {
            explain = await TryGetBuildExplainFromCompileCommandsAsync(normalizedFile, cancellationToken);
        }

        if (explain == null)
        {
            explain = BuildInferredExplain(normalizedFile);
        }

        var compiler = string.IsNullOrWhiteSpace(explain.Compiler)
            ? (explain.Argv.Count > 0 ? explain.Argv[0] : null)
            : explain.Compiler;
        var languageStandard = NormalizeLanguageStandard(explain.LanguageStandard)
            ?? NormalizeLanguageStandard(ParseArgValue(explain.Argv, "-std", "--std"));
        var targetTriple = FirstNonEmpty(explain.TargetTriple, ParseArgValue(explain.Argv, "-target", "--target"));
        var sysroot = FirstNonEmpty(explain.Sysroot, ParseArgValue(explain.Argv, "--sysroot", "-isysroot"));
        var defines = ExtractDefines(explain.Argv);
        var includePaths = ExtractIncludePaths(explain.Argv, explain.WorkingDirectory);
        var keyFlags = ExtractKeyFlags(explain.Argv);
        var buildConfidence = explain.ParseContextConfidence ?? (explain.ResolutionSource switch
        {
            "postgres" => 0.900m,
            "clangd.compile_commands" => 0.700m,
            "inferred" => 0.500m,
            _ => 0.650m
        });

        var payload = new Dictionary<string, object?>
        {
            ["file"] = explain.FilePath,
            ["line"] = line,
            ["column"] = column,
            ["resolution_source"] = explain.ResolutionSource,
            ["command_origin"] = explain.CommandOrigin,
            ["compiler"] = compiler,
            ["language_standard"] = languageStandard,
            ["target_triple"] = targetTriple,
            ["target"] = targetTriple,
            ["sysroot"] = sysroot,
            ["working_directory"] = explain.WorkingDirectory,
            ["source_file"] = explain.SourceFile,
            ["output"] = explain.OutputPath,
            ["key_flags"] = keyFlags,
            ["include_paths"] = includePaths,
            ["defines"] = defines,
            ["parse_warnings"] = explain.ParseWarnings,
            ["parse_errors"] = explain.ParseErrors,
            ["snapshot_id"] = explain.SnapshotId,
            ["build_config_id"] = explain.BuildConfigId,
            ["parse_context_kind"] = explain.ParseContextKind,
            ["parse_context_confidence"] = explain.ParseContextConfidence,
            ["confidence"] = buildConfidence,
            ["command_text"] = string.IsNullOrWhiteSpace(explain.CommandText) ? string.Join(" ", explain.Argv) : explain.CommandText,
            ["argv"] = explain.Argv
        };

        return JsonSerializer.Serialize(payload, PrettyJson);
    }

    private async Task<string> CppSnapshotStatusAsync(JsonElement args, CancellationToken cancellationToken)
    {
        if (!_connectionFactory.IsConfigured)
        {
            return "cpp.snapshot_status requires a configured database connection.";
        }

        var requestedSnapshotId = TryGetOptionalInt64Argument(args, "snapshot_id", "snapshotId");

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var status = await LoadSnapshotStatusAsync(connection, requestedSnapshotId, cancellationToken);
        if (status == null)
        {
            var unavailablePayload = new Dictionary<string, object?>
            {
                ["requested_snapshot_id"] = requestedSnapshotId,
                ["status"] = "unavailable",
                ["reason"] = "No matching snapshot rows were found.",
                ["indexing_in_progress"] = false
            };

            return JsonSerializer.Serialize(unavailablePayload, PrettyJson);
        }

        var coveragePct = status.TotalFiles > 0
            ? decimal.Round(status.ParsedFiles * 100m / status.TotalFiles, 2, MidpointRounding.AwayFromZero)
            : 0m;
        var parseFailurePct = status.ParseContextCount > 0
            ? decimal.Round(status.ParseErrorContexts * 100m / status.ParseContextCount, 2, MidpointRounding.AwayFromZero)
            : 0m;
        var indexingInProgress = string.Equals(status.IndexStatus, "in_progress", StringComparison.OrdinalIgnoreCase);

        var payload = new Dictionary<string, object?>
        {
            ["snapshot_id"] = status.SnapshotId,
            ["requested_snapshot_id"] = requestedSnapshotId,
            ["repo_root"] = status.RepoRoot,
            ["workspace_hash"] = status.WorkspaceHash,
            ["vcs_commit"] = status.VcsCommit,
            ["parent_snapshot_id"] = status.ParentSnapshotId,
            ["snapshot_kind"] = status.Kind,
            ["index_status"] = status.IndexStatus,
            ["indexing_in_progress"] = indexingInProgress,
            ["summary"] = new
            {
                total_symbols = status.TotalSymbols,
                total_refs = status.TotalRefs,
                total_files = status.TotalFiles,
                parsed_files = status.ParsedFiles,
                parse_contexts = status.ParseContextCount,
                parse_error_contexts = status.ParseErrorContexts,
                borrowed_contexts = status.BorrowedContexts,
                parse_coverage_pct = coveragePct,
                parse_failure_pct = parseFailurePct,
                avg_parse_confidence = status.AvgParseConfidence
            },
            ["coverage"] = new
            {
                total_files = status.TotalFiles,
                parsed_files = status.ParsedFiles,
                coverage_pct = coveragePct,
                borrowed_contexts = status.BorrowedContexts,
                avg_confidence = status.AvgParseConfidence
            },
            ["last_updated"] = status.LastUpdatedAt,
            ["created_at"] = status.CreatedAt
        };

        return JsonSerializer.Serialize(payload, PrettyJson);
    }

    private async Task<string> CppIncludeExplainAsync(JsonElement args, CancellationToken cancellationToken)
    {
        if (!args.TryGetProperty("file", out var fileElement) || fileElement.ValueKind != JsonValueKind.String)
        {
            return "Missing required argument: file";
        }

        var inputFile = fileElement.GetString();
        if (string.IsNullOrWhiteSpace(inputFile))
        {
            return "Argument 'file' cannot be empty";
        }

        var requestedIdentifier = TryGetOptionalStringArgument(
            args,
            "unresolved_identifier",
            "unresolvedIdentifier",
            "identifier",
            "symbol");
        var requestedSymbolId = TryGetOptionalInt64Argument(args, "symbol_id", "symbolId");

        var normalizedFile = NormalizeInputPath(inputFile);
        var explain = await TryGetIncludeExplainFromPostgresAsync(
            normalizedFile,
            requestedIdentifier,
            requestedSymbolId,
            cancellationToken);

        if (explain == null)
        {
            var unavailablePayload = new Dictionary<string, object?>
            {
                ["file"] = normalizedFile,
                ["resolution_source"] = "unavailable",
                ["requested_unresolved_identifier"] = requestedIdentifier,
                ["requested_symbol_id"] = requestedSymbolId,
                ["currently_used_headers"] = Array.Empty<object>(),
                ["removable_headers"] = Array.Empty<object>(),
                ["missing_headers"] = Array.Empty<object>(),
                ["candidate_headers"] = Array.Empty<object>(),
                ["confidence"] = 0m,
                ["rationale"] = "No snapshot/file_dependency facts were found for this file in the latest snapshot."
            };

            return JsonSerializer.Serialize(unavailablePayload, PrettyJson);
        }

        var currentlyUsed = explain.DirectIncludes
            .Where(x => x.UsedSymbolCount > 0)
            .ToList();
        var removable = explain.DirectIncludes
            .Where(x => x.UsedSymbolCount == 0)
            .ToList();
        var includeConfidence = explain.DirectIncludes.Count > 0
            ? Math.Round(explain.DirectIncludes.Average(x => (double)x.MaxConfidence), 3, MidpointRounding.AwayFromZero)
            : 0d;

        var clangdProbe = await TryProbeIncludeCleanerAsync(normalizedFile, explain.DirectIncludes, cancellationToken);

        var payload = new Dictionary<string, object?>
        {
            ["file"] = explain.FilePath,
            ["snapshot_id"] = explain.SnapshotId,
            ["requested_unresolved_identifier"] = requestedIdentifier,
            ["requested_symbol_id"] = requestedSymbolId,
            ["summary"] = new
            {
                direct_include_count = explain.DirectIncludes.Count,
                referenced_symbol_count = explain.ReferencedSymbolCount,
                currently_used_header_count = currentlyUsed.Count,
                removable_header_count = removable.Count,
                missing_header_count = explain.MissingHeaders.Count,
                candidate_header_count = explain.CandidateHeaders.Count,
                confidence = includeConfidence
            },
            ["confidence"] = includeConfidence,
            ["direct_includes"] = explain.DirectIncludes.Select(x => new
            {
                header = x.HeaderPath,
                literal = x.LiteralText,
                directive_kind = x.DirectiveKind,
                include_line = x.IncludeLine >= 0 ? x.IncludeLine + 1 : (int?)null,
                support_count = x.SupportCount,
                confidence = x.MaxConfidence,
                used_symbol_count = x.UsedSymbolCount,
                sample_symbols = x.SampleSymbols,
                rationale = x.UsedSymbolCount > 0
                    ? BuildUsedHeaderRationale(x)
                    : BuildInactiveHeaderRationale(x)
            }),
            ["currently_used_headers"] = currentlyUsed.Select(x => new
            {
                header = x.HeaderPath,
                literal = x.LiteralText,
                directive_kind = x.DirectiveKind,
                include_line = x.IncludeLine >= 0 ? x.IncludeLine + 1 : (int?)null,
                confidence = x.MaxConfidence,
                used_symbol_count = x.UsedSymbolCount,
                sample_symbols = x.SampleSymbols,
                rationale = BuildUsedHeaderRationale(x)
            }),
            ["removable_headers"] = removable.Select(x => new
            {
                header = x.HeaderPath,
                literal = x.LiteralText,
                directive_kind = x.DirectiveKind,
                include_line = x.IncludeLine >= 0 ? x.IncludeLine + 1 : (int?)null,
                support_count = x.SupportCount,
                confidence = x.MaxConfidence,
                rationale = BuildInactiveHeaderRationale(x)
            }),
            ["missing_headers"] = explain.MissingHeaders.Select(x => new
            {
                symbol = x.SymbolName,
                symbol_id = x.SymbolId,
                source = x.Source,
                raw_text = x.RawText,
                raw_line = x.RawLine >= 0 ? x.RawLine + 1 : (int?)null,
                confidence = x.Confidence,
                candidate_headers = x.CandidateHeaders,
                rationale = BuildMissingHeaderRationale(x)
            }),
            ["candidate_headers"] = explain.CandidateHeaders.Select(x => new
            {
                symbol = x.SymbolName,
                symbol_id = x.SymbolId,
                header = x.HeaderPath,
                declaration_count = x.DeclarationCount,
                definition_count = x.DefinitionCount,
                confidence = x.Confidence,
                match_kind = x.MatchKind,
                requested_query = x.Query,
                query_source = x.QuerySource,
                rationale = BuildCandidateHeaderRationale(x)
            }),
            ["clangd_include_cleaner"] = new
            {
                attempted = clangdProbe.Attempted,
                method = clangdProbe.Method,
                available = clangdProbe.Available,
                hints = clangdProbe.Hints.Select(x => new
                {
                    header = x.HeaderPath,
                    include_line = x.IncludeLine,
                    item_count = x.ItemCount,
                    rationale = x.Rationale
                }),
                notes = clangdProbe.Notes
            }
        };

        return JsonSerializer.Serialize(payload, PrettyJson);
    }

    private async Task<string> CppFlowSummaryAsync(JsonElement args, CancellationToken cancellationToken)
    {
        if (!_connectionFactory.IsConfigured)
        {
            return "cpp.flow_summary requires a configured database connection.";
        }

        var symbolId = TryGetOptionalInt64Argument(args, "symbol_id", "symbolId");
        var qualifiedName = TryGetOptionalStringArgument(args, "qualified_name", "qualifiedName");
        var resolutionSource = symbolId.HasValue ? "symbol_id" : "qualified_name";

        if (symbolId == null && string.IsNullOrWhiteSpace(qualifiedName))
        {
            return "Missing argument: symbol_id or qualified_name is required.";
        }

        if (symbolId == null)
        {
            symbolId = await ResolveSymbolIdByQualifiedNameAsync(qualifiedName!, cancellationToken);
            if (symbolId == null)
            {
                return $"No symbol found for qualified_name '{qualifiedName}'";
            }
        }

        var rows = await LoadFlowSummaryRowsAsync(symbolId.Value, cancellationToken);
        var flows = rows
            .Select(r =>
            {
                var sourcePortName = FormatFlowPortName(r.FromPortKind, r.FromLabel, r.FromOrdinal);
                var targetPortName = FormatFlowPortName(r.ToPortKind, r.ToLabel, r.ToOrdinal);
                var normalizedFlowKind = NormalizeFlowKind(r.FlowKind);

                return new
                {
                    snapshot_id = r.SnapshotId,
                    callable_symbol_id = r.CallableSymbolId,
                    callable_symbol_name = r.CallableSymbolName,
                    source_port = sourcePortName,
                    target_port = targetPortName,
                    flow_kind = normalizedFlowKind,
                    flow_kind_raw = r.FlowKind,
                    condition_kind = r.ConditionKind,
                    engine = r.Engine,
                    evidence = r.EvidenceJson,
                    confidence = r.Confidence,
                    provenance = new
                    {
                        provenance_id = r.ProvenanceId,
                        extractor_name = r.Extractor,
                        extraction_method = r.Method,
                        exactness = r.Exactness
                    },
                    source_port_details = new
                    {
                        port_id = r.FromPortId,
                        kind = r.FromPortKind,
                        label = r.FromLabel,
                        ordinal = r.FromOrdinal,
                        pointee_depth = r.FromPointeeDepth,
                        type_name = r.FromTypeName
                    },
                    target_port_details = new
                    {
                        port_id = r.ToPortId,
                        kind = r.ToPortKind,
                        label = r.ToLabel,
                        ordinal = r.ToOrdinal,
                        pointee_depth = r.ToPointeeDepth,
                        type_name = r.ToTypeName
                    }
                };
            })
            .ToList();

        var payload = new
        {
            symbol_id = symbolId,
            qualified_name = qualifiedName,
            resolution_source = resolutionSource,
            summary = new
            {
                flow_count = flows.Count,
                flow_kinds = flows.Select(x => x.flow_kind).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            },
            flows
        };

        return JsonSerializer.Serialize(payload, PrettyJson);
    }

    private async Task<string> CppContextPackAsync(JsonElement args, CancellationToken cancellationToken)
    {
        if (!_connectionFactory.IsConfigured)
        {
            return "cpp.context_pack requires a configured database connection.";
        }

        var symbolId = TryGetOptionalInt64Argument(args, "symbol_id", "symbolId");
        var inputFile = TryGetOptionalStringArgument(args, "file", "file_path", "path");

        if (symbolId == null && string.IsNullOrWhiteSpace(inputFile))
        {
            return "Missing argument: symbol_id or file is required.";
        }

        var requestedTokenBudget = TryGetOptionalInt64Argument(args, "token_budget", "tokenBudget");
        var tokenBudget = (int)Math.Clamp(requestedTokenBudget ?? 8000, 512, 64000);
        var normalizedFile = !string.IsNullOrWhiteSpace(inputFile) ? NormalizeInputPath(inputFile!) : null;

        const int primarySymbolLimit = 8;
        const int callDepth = 2;

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var snapshotId = await ResolveLatestSnapshotIdAsync(connection, cancellationToken);
        if (snapshotId == null)
        {
            var unavailablePayload = new Dictionary<string, object?>
            {
                ["request"] = new
                {
                    symbol_id = symbolId,
                    file = normalizedFile,
                    token_budget = tokenBudget
                },
                ["resolution_source"] = "unavailable",
                ["reason"] = "No snapshot rows were found.",
                ["primary_symbols"] = Array.Empty<object>(),
                ["callers"] = Array.Empty<object>(),
                ["callees"] = Array.Empty<object>(),
                ["related_types"] = Array.Empty<object>(),
                ["include_context"] = Array.Empty<object>()
            };

            return JsonSerializer.Serialize(unavailablePayload, PrettyJson);
        }

        SnapshotFileRef? targetFile = null;
        if (!string.IsNullOrWhiteSpace(normalizedFile))
        {
            targetFile = await ResolveLatestFileAsync(connection, normalizedFile, cancellationToken);
        }

        var fileSeedSymbols = targetFile == null
            ? new List<long>()
            : await LoadContextPackPrimarySymbolIdsForFileAsync(
                connection,
                snapshotId.Value,
                targetFile.FileId,
                primarySymbolLimit,
                cancellationToken);

        var primarySeedSymbolIds = new List<long>();
        if (symbolId.HasValue)
        {
            primarySeedSymbolIds.Add(symbolId.Value);
        }

        primarySeedSymbolIds.AddRange(fileSeedSymbols);

        var dedupedSeedSymbolIds = primarySeedSymbolIds
            .Distinct()
            .Take(primarySymbolLimit)
            .ToList();

        var primaryCards = await LoadContextPackSymbolCardsAsync(
            connection,
            snapshotId.Value,
            dedupedSeedSymbolIds,
            symbolId,
            primarySymbolLimit,
            cancellationToken);

        var primarySymbolIds = primaryCards
            .Select(x => x.SymbolId)
            .Distinct()
            .ToList();

        var callerEdges = await LoadContextPackCallEdgesAsync(
            connection,
            snapshotId.Value,
            primarySymbolIds,
            incoming: true,
            maxDepth: callDepth,
            limit: 80,
            cancellationToken);

        var calleeEdges = await LoadContextPackCallEdgesAsync(
            connection,
            snapshotId.Value,
            primarySymbolIds,
            incoming: false,
            maxDepth: callDepth,
            limit: 80,
            cancellationToken);

        var relatedTypes = await LoadContextPackRelatedTypesAsync(
            connection,
            snapshotId.Value,
            primarySymbolIds,
            limit: 80,
            cancellationToken);

        var includeFileIds = CollectContextPackFileIds(targetFile, primaryCards);
        var includeContext = await LoadContextPackIncludeContextAsync(
            connection,
            snapshotId.Value,
            includeFileIds,
            preferredFileId: targetFile?.FileId,
            limit: 12,
            cancellationToken);

        var warnings = new List<string>();
        if (symbolId.HasValue && primaryCards.All(x => x.SymbolId != symbolId.Value))
        {
            warnings.Add($"No symbol_card_mv row found for symbol_id {symbolId.Value} in snapshot {snapshotId.Value}.");
        }

        if (!string.IsNullOrWhiteSpace(normalizedFile) && targetFile == null)
        {
            warnings.Add($"No file row found for '{normalizedFile}' in snapshot {snapshotId.Value}.");
        }

        if (primaryCards.Count == 0)
        {
            warnings.Add("No primary symbols were resolved from symbol_id/file input.");
        }

        var primaryItems = primaryCards
            .Select(card => new
            {
                snapshot_id = card.SnapshotId,
                symbol_id = card.SymbolId,
                kind = card.Kind,
                name = card.Name,
                qualified_name = card.QualifiedName,
                signature = card.SignatureText,
                visibility = card.Visibility,
                template_kind = card.TemplateKind,
                is_exported = card.IsExported,
                owner_symbol_id = card.OwnerSymbolId,
                owner_qualified_name = card.OwnerQualifiedName,
                declaration_file = card.PrimaryFilePath,
                declaration_file_id = card.PrimaryFileId,
                declaration_count = card.DeclCount,
                definition_count = card.DefCount,
                forward_declaration_count = card.FwdDeclCount,
                key_effects = card.KeyEffects,
                macro_hazard_count = card.MacroHazardCount,
                macro_expand_count = card.MacroExpandCount,
                top_callers = card.TopCallers,
                top_callees = card.TopCallees,
                top_refs = card.TopRefs
            })
            .ToList();

        var directCallerItems = callerEdges
            .Where(x => x.Depth == 1)
            .OrderByDescending(x => x.CallsiteCount)
            .ThenBy(x => x.FromSymbolName, StringComparer.Ordinal)
            .Select(x => new
            {
                from_symbol_id = x.FromSymbolId,
                from_symbol_name = x.FromSymbolName,
                to_symbol_id = x.ToSymbolId,
                to_symbol_name = x.ToSymbolName,
                depth = x.Depth,
                callsite_count = x.CallsiteCount,
                max_confidence = x.MaxConfidence
            })
            .ToList();

        var transitiveCallerItems = callerEdges
            .Where(x => x.Depth > 1)
            .OrderBy(x => x.Depth)
            .ThenByDescending(x => x.CallsiteCount)
            .ThenBy(x => x.FromSymbolName, StringComparer.Ordinal)
            .Select(x => new
            {
                from_symbol_id = x.FromSymbolId,
                from_symbol_name = x.FromSymbolName,
                to_symbol_id = x.ToSymbolId,
                to_symbol_name = x.ToSymbolName,
                depth = x.Depth,
                callsite_count = x.CallsiteCount,
                max_confidence = x.MaxConfidence
            })
            .ToList();

        var calleeItems = calleeEdges
            .OrderBy(x => x.Depth)
            .ThenByDescending(x => x.CallsiteCount)
            .ThenBy(x => x.ToSymbolName, StringComparer.Ordinal)
            .Select(x => new
            {
                from_symbol_id = x.FromSymbolId,
                from_symbol_name = x.FromSymbolName,
                to_symbol_id = x.ToSymbolId,
                to_symbol_name = x.ToSymbolName,
                depth = x.Depth,
                callsite_count = x.CallsiteCount,
                max_confidence = x.MaxConfidence
            })
            .ToList();

        var relatedTypeItems = relatedTypes
            .Select(x => new
            {
                owner_symbol_id = x.OwnerSymbolId,
                owner_symbol_name = x.OwnerSymbolName,
                target_symbol_id = x.TargetSymbolId,
                target_symbol_name = x.TargetSymbolName,
                edge_kind = x.EdgeKind,
                edge_count = x.EdgeCount
            })
            .ToList();

        var includeItems = includeContext
            .Select(x => new
            {
                file_id = x.FileId,
                file = x.FilePath,
                include_count = x.IncludeCount,
                import_count = x.ImportCount,
                defined_symbol_count = x.DefinedSymbolCount,
                referenced_symbol_count = x.ReferencedSymbolCount,
                macro_density = x.MacroDensity,
                parse_coverage = x.ParseCoverage,
                top_external_dependencies = x.TopExternalDependencies,
                relevant_headers = x.RelevantHeaders
            })
            .ToList();

        var droppedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var estimatedTokensUsed = EstimateTokenCount(new
        {
            snapshot_id = snapshotId.Value,
            request = new { symbol_id = symbolId, file = normalizedFile, token_budget = tokenBudget },
            warnings
        });

        var selectedPrimary = TakeItemsByBudget(
            primaryItems,
            "primary_symbols",
            tokenBudget,
            ref estimatedTokensUsed,
            droppedCounts,
            allowFirstOverflow: true);
        var selectedDirectCallers = TakeItemsByBudget(
            directCallerItems,
            "direct_callers",
            tokenBudget,
            ref estimatedTokensUsed,
            droppedCounts);
        var selectedCallees = TakeItemsByBudget(
            calleeItems,
            "callees",
            tokenBudget,
            ref estimatedTokensUsed,
            droppedCounts);
        var selectedTransitiveCallers = TakeItemsByBudget(
            transitiveCallerItems,
            "transitive_callers",
            tokenBudget,
            ref estimatedTokensUsed,
            droppedCounts);
        var selectedTypes = TakeItemsByBudget(
            relatedTypeItems,
            "related_types",
            tokenBudget,
            ref estimatedTokensUsed,
            droppedCounts);
        var selectedIncludes = TakeItemsByBudget(
            includeItems,
            "include_context",
            tokenBudget,
            ref estimatedTokensUsed,
            droppedCounts);

        var selectedCallers = selectedDirectCallers
            .Concat(selectedTransitiveCallers)
            .ToList();

        var payload = new Dictionary<string, object?>
        {
            ["request"] = new
            {
                symbol_id = symbolId,
                file = normalizedFile,
                token_budget = tokenBudget
            },
            ["snapshot_id"] = snapshotId.Value,
            ["resolution"] = new
            {
                resolution_source = symbolId.HasValue ? "symbol_id" : "file",
                target_file = targetFile?.FilePath,
                target_file_id = targetFile?.FileId,
                primary_symbol_candidates = primaryCards.Count,
                caller_edge_candidates = callerEdges.Count,
                callee_edge_candidates = calleeEdges.Count,
                related_type_candidates = relatedTypes.Count,
                include_context_candidates = includeContext.Count
            },
            ["token_budget"] = new
            {
                requested = requestedTokenBudget ?? 8000,
                applied = tokenBudget,
                estimated_used = estimatedTokensUsed,
                estimated_remaining = Math.Max(tokenBudget - estimatedTokensUsed, 0),
                truncated = droppedCounts.Count > 0
            },
            ["primary_symbols"] = selectedPrimary,
            ["callers"] = selectedCallers,
            ["callees"] = selectedCallees,
            ["related_types"] = selectedTypes,
            ["include_context"] = selectedIncludes,
            ["warnings"] = warnings,
            ["truncation"] = new
            {
                dropped_items = droppedCounts
            }
        };

        return JsonSerializer.Serialize(payload, PrettyJson);
    }

    private async Task<SnapshotStatusInfo?> LoadSnapshotStatusAsync(
        DbConnection connection,
        long? requestedSnapshotId,
        CancellationToken cancellationToken)
    {
        const string sql = """
                           WITH target_snapshot AS (
                               SELECT
                                   s.snapshot_id,
                                   s.repo_root,
                                   s.workspace_hash,
                                   s.vcs_commit,
                                   s.parent_snapshot_id,
                                   s.kind,
                                   s.created_at,
                                   s.last_updated_at,
                                   s.index_status
                               FROM snapshot s
                               WHERE
                                   (@requested_snapshot_id IS NOT NULL AND s.snapshot_id = @requested_snapshot_id)
                                   OR (
                                       @requested_snapshot_id IS NULL
                                       AND s.repo_root = @repo_root
                                       AND s.is_archived = FALSE
                                   )
                               ORDER BY s.created_at DESC, s.snapshot_id DESC
                               LIMIT 1
                           ),
                           symbol_stats AS (
                               SELECT COUNT(*)::BIGINT AS total_symbols
                               FROM symbol s
                               JOIN target_snapshot t
                                 ON t.snapshot_id = s.snapshot_id
                           ),
                           ref_stats AS (
                               SELECT COUNT(*)::BIGINT AS total_refs
                               FROM occurrence o
                               JOIN target_snapshot t
                                 ON t.snapshot_id = o.snapshot_id
                           ),
                           file_stats AS (
                               SELECT COUNT(*)::BIGINT AS total_files
                               FROM file f
                               JOIN target_snapshot t
                                 ON t.snapshot_id = f.snapshot_id
                               WHERE f.is_external = FALSE
                           ),
                           parse_stats AS (
                               SELECT
                                   COUNT(*)::BIGINT AS parse_context_count,
                                   COUNT(DISTINCT pc.file_id)::BIGINT AS parsed_files,
                                   COUNT(*) FILTER (WHERE pc.borrowed_from_build_config_id IS NOT NULL)::BIGINT AS borrowed_contexts,
                                   COUNT(*) FILTER (
                                       (
                                           jsonb_typeof(pc.parse_errors_json) = 'array'
                                           AND jsonb_array_length(pc.parse_errors_json) > 0
                                       )
                                       OR (
                                           jsonb_typeof(pc.parse_errors_json) = 'object'
                                           AND pc.parse_errors_json <> '{}'::jsonb
                                       )
                                   )::BIGINT AS parse_error_contexts,
                                   COALESCE(AVG(pc.confidence), 0)::NUMERIC(6,3) AS avg_parse_confidence
                               FROM parse_context pc
                               JOIN target_snapshot t
                                 ON t.snapshot_id = pc.snapshot_id
                           )
                           SELECT
                               t.snapshot_id,
                               t.repo_root,
                               t.workspace_hash,
                               t.vcs_commit,
                               t.parent_snapshot_id,
                               t.kind,
                               t.created_at,
                               t.last_updated_at,
                               t.index_status,
                               COALESCE(ss.total_symbols, 0) AS total_symbols,
                               COALESCE(rs.total_refs, 0) AS total_refs,
                               COALESCE(fs.total_files, 0) AS total_files,
                               COALESCE(ps.parsed_files, 0) AS parsed_files,
                               COALESCE(ps.parse_context_count, 0) AS parse_context_count,
                               COALESCE(ps.parse_error_contexts, 0) AS parse_error_contexts,
                               COALESCE(ps.borrowed_contexts, 0) AS borrowed_contexts,
                               COALESCE(ps.avg_parse_confidence, 0)::NUMERIC(6,3) AS avg_parse_confidence
                           FROM target_snapshot t
                           CROSS JOIN symbol_stats ss
                           CROSS JOIN ref_stats rs
                           CROSS JOIN file_stats fs
                           CROSS JOIN parse_stats ps;
                           """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue(
            "requested_snapshot_id",
            requestedSnapshotId.HasValue ? requestedSnapshotId.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("repo_root", NormalizePath(_clangd.WorkspaceRoot));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SnapshotStatusInfo
        {
            SnapshotId = reader.GetInt64(reader.GetOrdinal("snapshot_id")),
            RepoRoot = reader.GetString(reader.GetOrdinal("repo_root")),
            WorkspaceHash = reader.GetString(reader.GetOrdinal("workspace_hash")),
            VcsCommit = GetNullableString(reader, "vcs_commit"),
            ParentSnapshotId = GetNullableInt64(reader, "parent_snapshot_id"),
            Kind = reader.GetString(reader.GetOrdinal("kind")),
            CreatedAt = ReadDateTimeFlexible(reader, "created_at"),
            LastUpdatedAt = ReadDateTimeFlexible(reader, "last_updated_at"),
            IndexStatus = reader.GetString(reader.GetOrdinal("index_status")),
            TotalSymbols = reader.GetInt64(reader.GetOrdinal("total_symbols")),
            TotalRefs = reader.GetInt64(reader.GetOrdinal("total_refs")),
            TotalFiles = reader.GetInt64(reader.GetOrdinal("total_files")),
            ParsedFiles = reader.GetInt64(reader.GetOrdinal("parsed_files")),
            ParseContextCount = reader.GetInt64(reader.GetOrdinal("parse_context_count")),
            ParseErrorContexts = reader.GetInt64(reader.GetOrdinal("parse_error_contexts")),
            BorrowedContexts = reader.GetInt64(reader.GetOrdinal("borrowed_contexts")),
            AvgParseConfidence = ReadDecimalFlexible(reader, "avg_parse_confidence")
        };
    }

    private async Task<long?> ResolveLatestSnapshotIdAsync(
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

    private async Task<List<long>> LoadContextPackPrimarySymbolIdsForFileAsync(
        DbConnection connection,
        long snapshotId,
        long fileId,
        int limit,
        CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT sd.symbol_id
                           FROM symbol_decl sd
                           JOIN symbol s
                             ON s.snapshot_id = @snapshot_id
                            AND s.symbol_id = sd.symbol_id
                           WHERE sd.snapshot_id = @snapshot_id
                             AND sd.file_id = @file_id
                           GROUP BY sd.symbol_id
                           ORDER BY
                               COUNT(*) FILTER (WHERE sd.role = 'def') DESC,
                               COUNT(*) DESC,
                               sd.symbol_id
                           LIMIT @limit;
                           """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", snapshotId);
        cmd.Parameters.AddWithValue("file_id", fileId);
        cmd.Parameters.AddWithValue("limit", limit);

        var symbolIds = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            symbolIds.Add(reader.GetInt64(reader.GetOrdinal("symbol_id")));
        }

        return symbolIds;
    }

    private async Task<List<ContextPackSymbolCard>> LoadContextPackSymbolCardsAsync(
        DbConnection connection,
        long snapshotId,
        IReadOnlyList<long> symbolIds,
        long? preferredSymbolId,
        int limit,
        CancellationToken cancellationToken)
    {
        var cards = new List<ContextPackSymbolCard>();
        if (symbolIds.Count == 0)
        {
            return cards;
        }

        const string sql = """
                           WITH target_symbols AS (
                               SELECT DISTINCT unnest(@symbol_ids::BIGINT[]) AS symbol_id
                           ),
                           primary_file AS (
                               SELECT
                                   sd.symbol_id,
                                   sd.file_id,
                                   COALESCE(f.real_path, f.path) AS file_path,
                                   ROW_NUMBER() OVER (
                                       PARTITION BY sd.symbol_id
                                       ORDER BY
                                           CASE sd.role
                                               WHEN 'def' THEN 0
                                               WHEN 'decl' THEN 1
                                               ELSE 2
                                           END,
                                           sd.decl_id
                                   ) AS rn
                               FROM symbol_decl sd
                               JOIN file f
                                 ON f.snapshot_id = @snapshot_id
                                AND f.file_id = sd.file_id
                               WHERE sd.snapshot_id = @snapshot_id
                                 AND sd.symbol_id = ANY(@symbol_ids)
                           )
                           SELECT
                               sc.snapshot_id,
                               sc.symbol_id,
                               sc.kind,
                               sc.name,
                               sc.qualified_name,
                               sc.visibility,
                               sc.template_kind,
                               sc.is_exported,
                               sc.owner_symbol_id,
                               sc.owner_qualified_name,
                               sc.signature_text,
                               sc.decl_count,
                               sc.def_count,
                               sc.fwd_decl_count,
                               sc.top_callers,
                               sc.top_callees,
                               sc.top_refs,
                               sc.key_effects,
                               sc.macro_hazard_count,
                               sc.macro_expand_count,
                               pf.file_id AS primary_file_id,
                               pf.file_path AS primary_file_path
                           FROM symbol_card_mv sc
                           JOIN target_symbols ts
                             ON ts.symbol_id = sc.symbol_id
                           LEFT JOIN primary_file pf
                             ON pf.symbol_id = sc.symbol_id
                            AND pf.rn = 1
                           WHERE sc.snapshot_id = @snapshot_id
                           ORDER BY
                               CASE
                                   WHEN @preferred_symbol_id IS NOT NULL AND sc.symbol_id = @preferred_symbol_id THEN 0
                                   ELSE 1
                               END,
                               sc.def_count DESC,
                               sc.decl_count DESC,
                               sc.symbol_id
                           LIMIT @limit;
                           """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", snapshotId);
        cmd.Parameters.AddWithValue("symbol_ids", symbolIds.ToArray());
        cmd.Parameters.AddWithValue("preferred_symbol_id", preferredSymbolId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cards.Add(new ContextPackSymbolCard
            {
                SnapshotId = reader.GetInt64(reader.GetOrdinal("snapshot_id")),
                SymbolId = reader.GetInt64(reader.GetOrdinal("symbol_id")),
                Kind = reader.GetString(reader.GetOrdinal("kind")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                QualifiedName = reader.GetString(reader.GetOrdinal("qualified_name")),
                Visibility = reader.GetString(reader.GetOrdinal("visibility")),
                TemplateKind = reader.GetString(reader.GetOrdinal("template_kind")),
                IsExported = ReadBooleanFlexible(reader, "is_exported"),
                OwnerSymbolId = GetNullableInt64(reader, "owner_symbol_id"),
                OwnerQualifiedName = GetNullableString(reader, "owner_qualified_name"),
                SignatureText = GetNullableString(reader, "signature_text"),
                DeclCount = reader.GetInt64(reader.GetOrdinal("decl_count")),
                DefCount = reader.GetInt64(reader.GetOrdinal("def_count")),
                FwdDeclCount = reader.GetInt64(reader.GetOrdinal("fwd_decl_count")),
                TopCallers = ParseJsonElementOrDefault(ReadJsonAsString(reader, "top_callers")),
                TopCallees = ParseJsonElementOrDefault(ReadJsonAsString(reader, "top_callees")),
                TopRefs = ParseJsonElementOrDefault(ReadJsonAsString(reader, "top_refs")),
                KeyEffects = ReadTextArray(reader, "key_effects"),
                MacroHazardCount = reader.GetInt64(reader.GetOrdinal("macro_hazard_count")),
                MacroExpandCount = reader.GetInt64(reader.GetOrdinal("macro_expand_count")),
                PrimaryFileId = GetNullableInt64(reader, "primary_file_id"),
                PrimaryFilePath = GetNullableString(reader, "primary_file_path")
            });
        }

        return cards;
    }

    private async Task<List<ContextPackCallEdge>> LoadContextPackCallEdgesAsync(
        DbConnection connection,
        long snapshotId,
        IReadOnlyList<long> rootSymbolIds,
        bool incoming,
        int maxDepth,
        int limit,
        CancellationToken cancellationToken)
    {
        var edges = new List<ContextPackCallEdge>();
        if (rootSymbolIds.Count == 0)
        {
            return edges;
        }

        var sql = incoming
            ? """
              WITH RECURSIVE walk AS (
                  SELECT
                      cr.caller_symbol_id AS from_symbol_id,
                      cr.callee_symbol_id AS to_symbol_id,
                      cr.callsite_count,
                      cr.max_confidence,
                      1 AS depth
                  FROM call_rollup_mv cr
                  WHERE cr.snapshot_id = @snapshot_id
                    AND cr.callee_symbol_id = ANY(@root_symbol_ids)
                  UNION ALL
                  SELECT
                      cr.caller_symbol_id,
                      cr.callee_symbol_id,
                      cr.callsite_count,
                      cr.max_confidence,
                      walk.depth + 1
                  FROM call_rollup_mv cr
                  JOIN walk
                    ON cr.callee_symbol_id = walk.from_symbol_id
                  WHERE cr.snapshot_id = @snapshot_id
                    AND walk.depth < @max_depth
              ),
              ranked AS (
                  SELECT
                      walk.from_symbol_id,
                      walk.to_symbol_id,
                      walk.depth,
                      walk.callsite_count,
                      walk.max_confidence,
                      ROW_NUMBER() OVER (
                          PARTITION BY walk.from_symbol_id, walk.to_symbol_id
                          ORDER BY walk.depth, walk.callsite_count DESC, walk.max_confidence DESC NULLS LAST
                      ) AS rn
                  FROM walk
              )
              SELECT
                  ranked.from_symbol_id,
                  COALESCE(NULLIF(sf.qualified_name, ''), sf.name) AS from_symbol_name,
                  ranked.to_symbol_id,
                  COALESCE(NULLIF(st.qualified_name, ''), st.name) AS to_symbol_name,
                  ranked.depth,
                  ranked.callsite_count,
                  ranked.max_confidence
              FROM ranked
              JOIN symbol sf
                ON sf.snapshot_id = @snapshot_id
               AND sf.symbol_id = ranked.from_symbol_id
              JOIN symbol st
                ON st.snapshot_id = @snapshot_id
               AND st.symbol_id = ranked.to_symbol_id
              WHERE ranked.rn = 1
              ORDER BY ranked.depth, ranked.callsite_count DESC, ranked.from_symbol_id, ranked.to_symbol_id
              LIMIT @limit;
              """
            : """
              WITH RECURSIVE walk AS (
                  SELECT
                      cr.caller_symbol_id AS from_symbol_id,
                      cr.callee_symbol_id AS to_symbol_id,
                      cr.callsite_count,
                      cr.max_confidence,
                      1 AS depth
                  FROM call_rollup_mv cr
                  WHERE cr.snapshot_id = @snapshot_id
                    AND cr.caller_symbol_id = ANY(@root_symbol_ids)
                  UNION ALL
                  SELECT
                      cr.caller_symbol_id,
                      cr.callee_symbol_id,
                      cr.callsite_count,
                      cr.max_confidence,
                      walk.depth + 1
                  FROM call_rollup_mv cr
                  JOIN walk
                    ON cr.caller_symbol_id = walk.to_symbol_id
                  WHERE cr.snapshot_id = @snapshot_id
                    AND walk.depth < @max_depth
              ),
              ranked AS (
                  SELECT
                      walk.from_symbol_id,
                      walk.to_symbol_id,
                      walk.depth,
                      walk.callsite_count,
                      walk.max_confidence,
                      ROW_NUMBER() OVER (
                          PARTITION BY walk.from_symbol_id, walk.to_symbol_id
                          ORDER BY walk.depth, walk.callsite_count DESC, walk.max_confidence DESC NULLS LAST
                      ) AS rn
                  FROM walk
              )
              SELECT
                  ranked.from_symbol_id,
                  COALESCE(NULLIF(sf.qualified_name, ''), sf.name) AS from_symbol_name,
                  ranked.to_symbol_id,
                  COALESCE(NULLIF(st.qualified_name, ''), st.name) AS to_symbol_name,
                  ranked.depth,
                  ranked.callsite_count,
                  ranked.max_confidence
              FROM ranked
              JOIN symbol sf
                ON sf.snapshot_id = @snapshot_id
               AND sf.symbol_id = ranked.from_symbol_id
              JOIN symbol st
                ON st.snapshot_id = @snapshot_id
               AND st.symbol_id = ranked.to_symbol_id
              WHERE ranked.rn = 1
              ORDER BY ranked.depth, ranked.callsite_count DESC, ranked.from_symbol_id, ranked.to_symbol_id
              LIMIT @limit;
              """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", snapshotId);
        cmd.Parameters.AddWithValue("root_symbol_ids", rootSymbolIds.ToArray());
        cmd.Parameters.AddWithValue("max_depth", maxDepth);
        cmd.Parameters.AddWithValue("limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            edges.Add(new ContextPackCallEdge
            {
                FromSymbolId = reader.GetInt64(reader.GetOrdinal("from_symbol_id")),
                FromSymbolName = reader.GetString(reader.GetOrdinal("from_symbol_name")),
                ToSymbolId = reader.GetInt64(reader.GetOrdinal("to_symbol_id")),
                ToSymbolName = reader.GetString(reader.GetOrdinal("to_symbol_name")),
                Depth = reader.GetInt32(reader.GetOrdinal("depth")),
                CallsiteCount = reader.GetInt64(reader.GetOrdinal("callsite_count")),
                MaxConfidence = GetNullableDecimal(reader, "max_confidence")
            });
        }

        return edges;
    }

    private async Task<List<ContextPackRelatedType>> LoadContextPackRelatedTypesAsync(
        DbConnection connection,
        long snapshotId,
        IReadOnlyList<long> rootSymbolIds,
        int limit,
        CancellationToken cancellationToken)
    {
        var types = new List<ContextPackRelatedType>();
        if (rootSymbolIds.Count == 0)
        {
            return types;
        }

        const string sql = """
                           SELECT
                               te.owner_symbol_id,
                               COALESCE(NULLIF(owner_symbol.qualified_name, ''), owner_symbol.name) AS owner_symbol_name,
                               te.target_symbol_id,
                               COALESCE(NULLIF(target_symbol.qualified_name, ''), target_symbol.name) AS target_symbol_name,
                               te.kind AS edge_kind,
                               COUNT(*)::BIGINT AS edge_count
                           FROM type_edge te
                           LEFT JOIN symbol owner_symbol
                             ON owner_symbol.snapshot_id = te.snapshot_id
                            AND owner_symbol.symbol_id = te.owner_symbol_id
                           LEFT JOIN symbol target_symbol
                             ON target_symbol.snapshot_id = te.snapshot_id
                            AND target_symbol.symbol_id = te.target_symbol_id
                           WHERE te.snapshot_id = @snapshot_id
                             AND (
                                 te.owner_symbol_id = ANY(@root_symbol_ids)
                                 OR te.target_symbol_id = ANY(@root_symbol_ids)
                             )
                           GROUP BY
                               te.owner_symbol_id,
                               owner_symbol_name,
                               te.target_symbol_id,
                               target_symbol_name,
                               te.kind
                           ORDER BY
                               CASE
                                   WHEN te.owner_symbol_id = ANY(@root_symbol_ids) THEN 0
                                   ELSE 1
                               END,
                               edge_count DESC,
                               te.owner_symbol_id,
                               te.target_symbol_id,
                               te.kind
                           LIMIT @limit;
                           """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", snapshotId);
        cmd.Parameters.AddWithValue("root_symbol_ids", rootSymbolIds.ToArray());
        cmd.Parameters.AddWithValue("limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            types.Add(new ContextPackRelatedType
            {
                OwnerSymbolId = reader.GetInt64(reader.GetOrdinal("owner_symbol_id")),
                OwnerSymbolName = reader.GetString(reader.GetOrdinal("owner_symbol_name")),
                TargetSymbolId = reader.GetInt64(reader.GetOrdinal("target_symbol_id")),
                TargetSymbolName = reader.GetString(reader.GetOrdinal("target_symbol_name")),
                EdgeKind = reader.GetString(reader.GetOrdinal("edge_kind")),
                EdgeCount = reader.GetInt64(reader.GetOrdinal("edge_count"))
            });
        }

        return types;
    }

    private async Task<List<ContextPackIncludeContext>> LoadContextPackIncludeContextAsync(
        DbConnection connection,
        long snapshotId,
        IReadOnlyList<long> fileIds,
        long? preferredFileId,
        int limit,
        CancellationToken cancellationToken)
    {
        var includes = new List<ContextPackIncludeContext>();
        if (fileIds.Count == 0)
        {
            return includes;
        }

        const string sql = """
                           SELECT
                               fov.snapshot_id,
                               fov.file_id,
                               COALESCE(fov.real_path, fov.path) AS file_path,
                               fov.include_count,
                               fov.import_count,
                               fov.defined_symbol_count,
                               fov.referenced_symbol_count,
                               fov.top_external_dependencies,
                               fov.macro_density,
                               fov.parse_coverage
                           FROM file_overview_mv fov
                           WHERE fov.snapshot_id = @snapshot_id
                             AND fov.file_id = ANY(@file_ids)
                           ORDER BY
                               CASE
                                   WHEN @preferred_file_id IS NOT NULL AND fov.file_id = @preferred_file_id THEN 0
                                   ELSE 1
                               END,
                               fov.include_count DESC,
                               fov.referenced_symbol_count DESC,
                               fov.file_id
                           LIMIT @limit;
                           """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", snapshotId);
        cmd.Parameters.AddWithValue("file_ids", fileIds.ToArray());
        cmd.Parameters.AddWithValue("preferred_file_id", preferredFileId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var dependencies = ParseJsonElementOrDefault(ReadJsonAsString(reader, "top_external_dependencies"));
            includes.Add(new ContextPackIncludeContext
            {
                SnapshotId = reader.GetInt64(reader.GetOrdinal("snapshot_id")),
                FileId = reader.GetInt64(reader.GetOrdinal("file_id")),
                FilePath = reader.GetString(reader.GetOrdinal("file_path")),
                IncludeCount = reader.GetInt64(reader.GetOrdinal("include_count")),
                ImportCount = reader.GetInt64(reader.GetOrdinal("import_count")),
                DefinedSymbolCount = reader.GetInt64(reader.GetOrdinal("defined_symbol_count")),
                ReferencedSymbolCount = reader.GetInt64(reader.GetOrdinal("referenced_symbol_count")),
                TopExternalDependencies = dependencies,
                MacroDensity = ReadDecimalFlexible(reader, "macro_density"),
                ParseCoverage = ReadDecimalFlexible(reader, "parse_coverage"),
                RelevantHeaders = ExtractExternalDependencyPaths(dependencies)
            });
        }

        return includes;
    }

    private static List<long> CollectContextPackFileIds(
        SnapshotFileRef? targetFile,
        IReadOnlyList<ContextPackSymbolCard> primaryCards)
    {
        var ordered = new List<long>();
        var seen = new HashSet<long>();

        void TryAdd(long? fileId)
        {
            if (!fileId.HasValue)
            {
                return;
            }

            if (seen.Add(fileId.Value))
            {
                ordered.Add(fileId.Value);
            }
        }

        TryAdd(targetFile?.FileId);
        foreach (var card in primaryCards)
        {
            TryAdd(card.PrimaryFileId);
            foreach (var refFileId in ExtractFileIdsFromTopRefs(card.TopRefs))
            {
                TryAdd(refFileId);
            }
        }

        return ordered.Take(16).ToList();
    }

    private static List<long> ExtractFileIdsFromTopRefs(JsonElement topRefs)
    {
        var fileIds = new List<long>();
        if (topRefs.ValueKind != JsonValueKind.Array)
        {
            return fileIds;
        }

        foreach (var item in topRefs.EnumerateArray())
        {
            if (!item.TryGetProperty("file_id", out var fileIdElement))
            {
                continue;
            }

            if (fileIdElement.ValueKind == JsonValueKind.Number && fileIdElement.TryGetInt64(out var numericId))
            {
                fileIds.Add(numericId);
                continue;
            }

            if (fileIdElement.ValueKind == JsonValueKind.String &&
                long.TryParse(fileIdElement.GetString(), out var parsedId))
            {
                fileIds.Add(parsedId);
            }
        }

        return fileIds;
    }

    private static List<string> ExtractExternalDependencyPaths(JsonElement dependencies)
    {
        var paths = new List<string>();
        if (dependencies.ValueKind != JsonValueKind.Array)
        {
            return paths;
        }

        foreach (var dependency in dependencies.EnumerateArray())
        {
            if (dependency.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String)
            {
                var value = pathElement.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    paths.Add(value);
                }
            }
        }

        return DistinctNonEmpty(paths);
    }

    private static List<T> TakeItemsByBudget<T>(
        IReadOnlyList<T> items,
        string bucketName,
        int tokenBudget,
        ref int estimatedUsedTokens,
        Dictionary<string, int> droppedCounts,
        bool allowFirstOverflow = false)
    {
        var selected = new List<T>();
        foreach (var item in items)
        {
            var itemTokens = EstimateTokenCount(item);
            var fitsBudget = estimatedUsedTokens + itemTokens <= tokenBudget;
            if (!fitsBudget && allowFirstOverflow && selected.Count == 0)
            {
                fitsBudget = true;
            }

            if (!fitsBudget)
            {
                if (droppedCounts.TryGetValue(bucketName, out var current))
                {
                    droppedCounts[bucketName] = current + 1;
                }
                else
                {
                    droppedCounts[bucketName] = 1;
                }

                continue;
            }

            selected.Add(item);
            estimatedUsedTokens += itemTokens;
        }

        return selected;
    }

    private static int EstimateTokenCount(object? value)
    {
        if (value == null)
        {
            return 0;
        }

        var json = value is string text
            ? text
            : JsonSerializer.Serialize(value);

        if (string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Ceiling(json.Length / 4.0));
    }

    private async Task<long?> ResolveSymbolIdByQualifiedNameAsync(string qualifiedName, CancellationToken cancellationToken)
    {
        if (!_connectionFactory.IsConfigured)
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        const string sql = """
            SELECT symbol_id
            FROM symbol
            WHERE snapshot_id = (
                SELECT snapshot_id
                FROM snapshot
                WHERE repo_root = @repo_root
                  AND is_archived = FALSE
                ORDER BY created_at DESC, snapshot_id DESC
                LIMIT 1
            )
              AND (lower(qualified_name) = lower(@qualified_name) OR lower(name) = lower(@qualified_name))
            ORDER BY symbol_id
            LIMIT 1;
            """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("qualified_name", qualifiedName);
        cmd.Parameters.AddWithValue("repo_root", NormalizePath(_clangd.WorkspaceRoot));

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result == null ? null : Convert.ToInt64(result);
    }

    private async Task<List<FlowSummaryRow>> LoadFlowSummaryRowsAsync(long symbolId, CancellationToken cancellationToken)
    {
        var rows = new List<FlowSummaryRow>();
        if (!_connectionFactory.IsConfigured)
        {
            return rows;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        const string sql = """
            SELECT
                fs.snapshot_id,
                fs.callable_symbol_id,
                COALESCE(NULLIF(cs.qualified_name, ''), cs.name) AS callable_symbol_name,
                fs.flow_kind,
                fs.condition_kind,
                fs.engine,
                fs.from_port_id,
                fs.to_port_id,
                fpf.port_kind AS from_port_kind,
                fpf.label AS from_label,
                fpf.ordinal AS from_ordinal,
                fpf.pointee_depth AS from_pointee_depth,
                COALESCE(NULLIF(ntf.qualified_name, ''), ntf.name) AS from_type_name,
                fpt.port_kind AS to_port_kind,
                fpt.label AS to_label,
                fpt.ordinal AS to_ordinal,
                fpt.pointee_depth AS to_pointee_depth,
                COALESCE(NULLIF(ntt.qualified_name, ''), ntt.name) AS to_type_name,
                fs.provenance_id,
                p.extractor_name AS extractor,
                p.extraction_method AS method,
                p.exactness,
                p.confidence,
                p.evidence_json
            FROM flow_summary fs
            JOIN callable_port fpf ON fs.from_port_id = fpf.port_id
            JOIN callable_port fpt ON fs.to_port_id = fpt.port_id
            LEFT JOIN symbol cs ON cs.snapshot_id = fs.snapshot_id AND cs.symbol_id = fs.callable_symbol_id
            LEFT JOIN symbol ntf ON ntf.snapshot_id = fs.snapshot_id AND ntf.symbol_id = fpf.type_symbol_id
            LEFT JOIN symbol ntt ON ntt.snapshot_id = fs.snapshot_id AND ntt.symbol_id = fpt.type_symbol_id
            JOIN provenance p ON p.provenance_id = fs.provenance_id
            WHERE fs.callable_symbol_id = @symbol_id
              AND fs.flow_kind IN ('value', 'taint', 'escape', 'ownership_transfer')
            ORDER BY fs.flow_id DESC
            LIMIT 200;
            """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("symbol_id", symbolId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var evidenceJson = ParseJsonElementOrDefault(ReadJsonAsString(reader, "evidence_json"));

            rows.Add(new FlowSummaryRow
            {
                SnapshotId = reader.GetInt64(reader.GetOrdinal("snapshot_id")),
                CallableSymbolId = reader.GetInt64(reader.GetOrdinal("callable_symbol_id")),
                CallableSymbolName = reader.GetString(reader.GetOrdinal("callable_symbol_name")),
                FlowKind = reader.GetString(reader.GetOrdinal("flow_kind")),
                ConditionKind = reader.GetString(reader.GetOrdinal("condition_kind")),
                Engine = reader.GetString(reader.GetOrdinal("engine")),
                FromPortId = reader.GetInt64(reader.GetOrdinal("from_port_id")),
                ToPortId = reader.GetInt64(reader.GetOrdinal("to_port_id")),
                FromPortKind = reader.GetString(reader.GetOrdinal("from_port_kind")),
                FromLabel = reader.GetString(reader.GetOrdinal("from_label")),
                FromOrdinal = reader.GetInt32(reader.GetOrdinal("from_ordinal")),
                FromPointeeDepth = reader.GetInt32(reader.GetOrdinal("from_pointee_depth")),
                FromTypeName = reader.IsDBNull(reader.GetOrdinal("from_type_name"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("from_type_name")),
                ToPortKind = reader.GetString(reader.GetOrdinal("to_port_kind")),
                ToLabel = reader.GetString(reader.GetOrdinal("to_label")),
                ToOrdinal = reader.GetInt32(reader.GetOrdinal("to_ordinal")),
                ToPointeeDepth = reader.GetInt32(reader.GetOrdinal("to_pointee_depth")),
                ToTypeName = reader.IsDBNull(reader.GetOrdinal("to_type_name"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("to_type_name")),
                ProvenanceId = reader.GetInt64(reader.GetOrdinal("provenance_id")),
                Extractor = reader.GetString(reader.GetOrdinal("extractor")),
                Method = reader.GetString(reader.GetOrdinal("method")),
                Exactness = reader.GetString(reader.GetOrdinal("exactness")),
                Confidence = ReadDecimalFlexible(reader, "confidence"),
                EvidenceJson = evidenceJson
            });
        }

        return rows;
    }

    private async Task<IncludeExplainInfo?> TryGetIncludeExplainFromPostgresAsync(
        string normalizedFile,
        string? requestedIdentifier,
        long? requestedSymbolId,
        CancellationToken cancellationToken)
    {
        if (!_connectionFactory.IsConfigured)
        {
            return null;
        }

        try
        {
            await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

            var target = await ResolveLatestFileAsync(connection, normalizedFile, cancellationToken);
            if (target == null)
            {
                return null;
            }

            var referencedSymbolCount = await GetReferencedSymbolCountAsync(connection, target, cancellationToken);
            var directIncludes = await LoadIncludeUsageAsync(connection, target, cancellationToken);
            var missingFromReferences = await LoadMissingHeadersFromReferencesAsync(connection, target, cancellationToken);
            var unresolvedCallsites = await LoadUnresolvedCallIdentifiersAsync(connection, target, cancellationToken);

            var candidateHeaders = new List<CandidateHeaderMatch>();
            var missingHeaders = new List<MissingHeaderRecommendation>(missingFromReferences);
            var identifierCandidateCache = new Dictionary<string, List<CandidateHeaderMatch>>(StringComparer.OrdinalIgnoreCase);

            if (requestedSymbolId.HasValue)
            {
                var fromSymbolId = await LoadCandidateHeadersBySymbolIdAsync(
                    connection,
                    target.SnapshotId,
                    requestedSymbolId.Value,
                    "symbol_id",
                    cancellationToken);
                candidateHeaders.AddRange(fromSymbolId);
            }

            if (!string.IsNullOrWhiteSpace(requestedIdentifier))
            {
                var fromIdentifier = await LoadCandidateHeadersByIdentifierAsync(
                    connection,
                    target.SnapshotId,
                    requestedIdentifier,
                    "requested_identifier",
                    cancellationToken);
                identifierCandidateCache[requestedIdentifier] = fromIdentifier;
                candidateHeaders.AddRange(fromIdentifier);
            }

            foreach (var unresolved in unresolvedCallsites.Take(12))
            {
                var identifier = ExtractIdentifierFromRawText(unresolved.RawText);
                if (string.IsNullOrWhiteSpace(identifier))
                {
                    continue;
                }

                if (!identifierCandidateCache.TryGetValue(identifier, out var unresolvedCandidates))
                {
                    unresolvedCandidates = await LoadCandidateHeadersByIdentifierAsync(
                        connection,
                        target.SnapshotId,
                        identifier,
                        "unresolved_callsite",
                        cancellationToken);
                    identifierCandidateCache[identifier] = unresolvedCandidates;
                }

                var unresolvedWithSource = unresolvedCandidates
                    .Select(x => x with { QuerySource = "unresolved_callsite", Query = identifier })
                    .ToList();
                candidateHeaders.AddRange(unresolvedWithSource);

                if (unresolvedWithSource.Count > 0)
                {
                    missingHeaders.Add(new MissingHeaderRecommendation(
                        SymbolId: null,
                        SymbolName: identifier,
                        Source: "unresolved_callsite",
                        RawText: unresolved.RawText,
                        RawLine: unresolved.RawLine,
                        Confidence: unresolvedWithSource.Max(x => x.Confidence),
                        CandidateHeaders: DistinctNonEmpty(unresolvedWithSource.Select(x => x.HeaderPath).Take(5))));
                }
            }

            var dedupedCandidates = candidateHeaders
                .GroupBy(
                    x => $"{x.QuerySource}|{x.Query}|{x.SymbolId}|{x.HeaderPath}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => g
                    .OrderBy(x => x.MatchRank)
                    .ThenBy(x => x.HeaderRank)
                    .ThenByDescending(x => x.Confidence)
                    .ThenByDescending(x => x.DeclarationCount)
                    .First())
                .OrderBy(x => x.MatchRank)
                .ThenBy(x => x.HeaderRank)
                .ThenByDescending(x => x.Confidence)
                .ThenByDescending(x => x.DeclarationCount)
                .ThenBy(x => x.HeaderPath, StringComparer.Ordinal)
                .Take(40)
                .ToList();

            var dedupedMissing = DeduplicateMissingHeaders(missingHeaders);

            return new IncludeExplainInfo
            {
                FilePath = target.FilePath,
                SnapshotId = target.SnapshotId,
                FileId = target.FileId,
                ReferencedSymbolCount = referencedSymbolCount,
                DirectIncludes = directIncludes,
                MissingHeaders = dedupedMissing,
                CandidateHeaders = dedupedCandidates,
                UnresolvedCallsites = unresolvedCallsites
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed querying include_explain data from database for {FilePath}", normalizedFile);
            return null;
        }
    }

    private async Task<SnapshotFileRef?> ResolveLatestFileAsync(
        DbConnection connection,
        string normalizedFile,
        CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT
                               f.snapshot_id,
                               f.file_id,
                               COALESCE(f.real_path, f.path) AS file_path
                           FROM file f
                           WHERE f.snapshot_id = (
                               SELECT snapshot_id
                               FROM snapshot
                               WHERE repo_root = @repo_root
                                 AND is_archived = FALSE
                               ORDER BY created_at DESC, snapshot_id DESC
                               LIMIT 1
                           )
                               AND (f.path = @file_path OR f.real_path = @file_path)
                           ORDER BY
                               CASE WHEN f.real_path = @file_path THEN 0 ELSE 1 END,
                               f.file_id
                           LIMIT 1;
                           """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("file_path", normalizedFile);
        cmd.Parameters.AddWithValue("repo_root", NormalizePath(_clangd.WorkspaceRoot));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SnapshotFileRef(
            SnapshotId: reader.GetInt64(reader.GetOrdinal("snapshot_id")),
            FileId: reader.GetInt64(reader.GetOrdinal("file_id")),
            FilePath: reader.GetString(reader.GetOrdinal("file_path")));
    }

    private async Task<long> GetReferencedSymbolCountAsync(
        DbConnection connection,
        SnapshotFileRef target,
        CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT COUNT(DISTINCT o.symbol_id)::BIGINT AS referenced_symbol_count
                           FROM occurrence o
                           WHERE o.snapshot_id = @snapshot_id
                               AND o.file_id = @file_id
                               AND o.is_implicit = FALSE
                               AND (o.role_bits & @role_decl_mask) = 0;
                           """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", target.SnapshotId);
        cmd.Parameters.AddWithValue("file_id", target.FileId);
        cmd.Parameters.AddWithValue("role_decl_mask", RoleDeclarationBit);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is long count ? count : 0;
    }

    private async Task<List<IncludeUsageHeader>> LoadIncludeUsageAsync(
        DbConnection connection,
        SnapshotFileRef target,
        CancellationToken cancellationToken)
    {
        const string sql = """
                           WITH direct_includes AS (
                               SELECT
                                   fd.to_file_id,
                                   COALESCE(tf.real_path, tf.path) AS header_path,
                                   fd.literal_text,
                                   fd.directive_kind,
                                   COUNT(*)::BIGINT AS support_count,
                                   MIN(COALESCE(NULLIF(fd.span ->> 'StartLine', '')::INT, NULLIF(fd.span ->> 'startLine', '')::INT, -1)) AS include_line,
                                   COALESCE(MAX(p.confidence), 0.000)::NUMERIC(4,3) AS max_confidence
                               FROM file_dependency fd
                               JOIN file tf
                                 ON tf.snapshot_id = fd.snapshot_id
                                AND tf.file_id = fd.to_file_id
                               LEFT JOIN provenance p
                                 ON p.provenance_id = fd.provenance_id
                               WHERE fd.snapshot_id = @snapshot_id
                                   AND fd.from_file_id = @file_id
                                   AND fd.is_active = TRUE
                                   AND fd.directive_kind IN ('include', 'import', 'module_import', 'header_unit')
                               GROUP BY fd.to_file_id, COALESCE(tf.real_path, tf.path), fd.literal_text, fd.directive_kind
                           ),
                           referenced_symbols AS (
                               SELECT DISTINCT o.symbol_id
                               FROM occurrence o
                               WHERE o.snapshot_id = @snapshot_id
                                   AND o.file_id = @file_id
                                   AND o.is_implicit = FALSE
                                   AND (o.role_bits & @role_decl_mask) = 0
                           ),
                           include_hits AS (
                               SELECT
                                   di.to_file_id,
                                   COUNT(DISTINCT rs.symbol_id)::BIGINT AS used_symbol_count,
                                   ARRAY_AGG(DISTINCT COALESCE(NULLIF(s.qualified_name, ''), s.name) ORDER BY COALESCE(NULLIF(s.qualified_name, ''), s.name))
                                       FILTER (WHERE COALESCE(NULLIF(s.qualified_name, ''), s.name) IS NOT NULL) AS sample_symbols
                               FROM direct_includes di
                               JOIN symbol_decl sd
                                 ON sd.snapshot_id = @snapshot_id
                                AND sd.file_id = di.to_file_id
                               JOIN symbol s
                                 ON s.snapshot_id = @snapshot_id
                                AND s.symbol_id = sd.symbol_id
                               JOIN referenced_symbols rs
                                 ON rs.symbol_id = sd.symbol_id
                               GROUP BY di.to_file_id
                           )
                           SELECT
                               di.to_file_id,
                               di.header_path,
                               di.literal_text,
                               di.directive_kind,
                               di.support_count,
                               di.include_line,
                               di.max_confidence,
                               COALESCE(ih.used_symbol_count, 0) AS used_symbol_count,
                               COALESCE(ih.sample_symbols, ARRAY[]::TEXT[]) AS sample_symbols
                           FROM direct_includes di
                           LEFT JOIN include_hits ih
                             ON ih.to_file_id = di.to_file_id
                           ORDER BY di.header_path, di.directive_kind, di.literal_text NULLS LAST;
                           """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", target.SnapshotId);
        cmd.Parameters.AddWithValue("file_id", target.FileId);
        cmd.Parameters.AddWithValue("role_decl_mask", RoleDeclarationBit);

        var includes = new List<IncludeUsageHeader>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            includes.Add(new IncludeUsageHeader(
                ToFileId: reader.GetInt64(reader.GetOrdinal("to_file_id")),
                HeaderPath: reader.GetString(reader.GetOrdinal("header_path")),
                LiteralText: GetNullableString(reader, "literal_text"),
                DirectiveKind: reader.GetString(reader.GetOrdinal("directive_kind")),
                SupportCount: reader.GetInt64(reader.GetOrdinal("support_count")),
                IncludeLine: reader.GetInt32(reader.GetOrdinal("include_line")),
                MaxConfidence: ReadDecimalFlexible(reader, "max_confidence"),
                UsedSymbolCount: reader.GetInt64(reader.GetOrdinal("used_symbol_count")),
                SampleSymbols: ReadTextArray(reader, "sample_symbols")));
        }

        return includes;
    }

    private async Task<List<MissingHeaderRecommendation>> LoadMissingHeadersFromReferencesAsync(
        DbConnection connection,
        SnapshotFileRef target,
        CancellationToken cancellationToken)
    {
        const string sql = """
                           WITH direct_headers AS (
                               SELECT DISTINCT fd.to_file_id
                               FROM file_dependency fd
                               WHERE fd.snapshot_id = @snapshot_id
                                   AND fd.from_file_id = @file_id
                                   AND fd.is_active = TRUE
                                   AND fd.directive_kind IN ('include', 'import', 'module_import', 'header_unit')
                           ),
                           referenced_symbols AS (
                               SELECT DISTINCT o.symbol_id
                               FROM occurrence o
                               WHERE o.snapshot_id = @snapshot_id
                                   AND o.file_id = @file_id
                                   AND o.is_implicit = FALSE
                                   AND (o.role_bits & @role_decl_mask) = 0
                           ),
                           decl_candidates AS (
                               SELECT
                                   rs.symbol_id,
                                   COALESCE(NULLIF(s.qualified_name, ''), s.name) AS symbol_name,
                                   sd.file_id AS decl_file_id,
                                   COALESCE(df.real_path, df.path) AS decl_path,
                                   COALESCE(p.confidence, 0.500)::NUMERIC(4,3) AS decl_confidence,
                                   CASE
                                       WHEN df.language = 'header' THEN 0
                                       WHEN lower(df.path) LIKE '%.h'
                                         OR lower(df.path) LIKE '%.hh'
                                         OR lower(df.path) LIKE '%.hpp'
                                         OR lower(df.path) LIKE '%.hxx'
                                         OR lower(df.path) LIKE '%.inc'
                                         OR lower(df.path) LIKE '%.ipp' THEN 1
                                       WHEN df.is_external = TRUE THEN 2
                                       ELSE 3
                                   END AS rank_bucket
                               FROM referenced_symbols rs
                               JOIN symbol s
                                 ON s.snapshot_id = @snapshot_id
                                AND s.symbol_id = rs.symbol_id
                               JOIN symbol_decl sd
                                 ON sd.snapshot_id = @snapshot_id
                                AND sd.symbol_id = rs.symbol_id
                               JOIN file df
                                 ON df.snapshot_id = @snapshot_id
                                AND df.file_id = sd.file_id
                               LEFT JOIN provenance p
                                 ON p.provenance_id = sd.provenance_id
                               WHERE sd.file_id <> @file_id
                           ),
                           per_symbol AS (
                               SELECT
                                   dc.symbol_id,
                                   MAX(dc.symbol_name) AS symbol_name,
                                   COALESCE(MAX(dc.decl_confidence), 0.500)::NUMERIC(4,3) AS max_confidence,
                                   COUNT(*) FILTER (WHERE dc.decl_file_id IN (SELECT to_file_id FROM direct_headers))::BIGINT AS direct_include_hits,
                                   COUNT(*)::BIGINT AS total_decl_hits
                               FROM decl_candidates dc
                               GROUP BY dc.symbol_id
                           )
                           SELECT
                               ps.symbol_id,
                               ps.symbol_name,
                               ps.max_confidence,
                               COALESCE(
                                   (
                                       SELECT ARRAY_AGG(c.path ORDER BY c.rank_bucket, c.path)
                                       FROM (
                                           SELECT
                                               dc.decl_path AS path,
                                               MIN(dc.rank_bucket) AS rank_bucket
                                           FROM decl_candidates dc
                                           WHERE dc.symbol_id = ps.symbol_id
                                           GROUP BY dc.decl_path
                                           ORDER BY MIN(dc.rank_bucket), dc.decl_path
                                           LIMIT 5
                                       ) c
                                   ),
                                   ARRAY[]::TEXT[]
                               ) AS candidate_headers
                           FROM per_symbol ps
                           WHERE ps.direct_include_hits = 0
                               AND ps.total_decl_hits > 0
                           ORDER BY ps.symbol_name
                           LIMIT 40;
                           """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", target.SnapshotId);
        cmd.Parameters.AddWithValue("file_id", target.FileId);
        cmd.Parameters.AddWithValue("role_decl_mask", RoleDeclarationBit);

        var missing = new List<MissingHeaderRecommendation>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            missing.Add(new MissingHeaderRecommendation(
                SymbolId: GetNullableInt64(reader, "symbol_id"),
                SymbolName: reader.GetString(reader.GetOrdinal("symbol_name")),
                Source: "referenced_symbol_no_direct_include",
                RawText: null,
                RawLine: null,
                Confidence: ReadDecimalFlexible(reader, "max_confidence"),
                CandidateHeaders: ReadTextArray(reader, "candidate_headers")));
        }

        return missing;
    }

    private async Task<List<UnresolvedCallsiteInfo>> LoadUnresolvedCallIdentifiersAsync(
        DbConnection connection,
        SnapshotFileRef target,
        CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT
                               cs.raw_text,
                               MIN(COALESCE(NULLIF(cs.span ->> 'StartLine', '')::INT, NULLIF(cs.span ->> 'startLine', '')::INT, -1)) AS raw_line
                           FROM callsite cs
                           LEFT JOIN call_target ct
                             ON ct.snapshot_id = cs.snapshot_id
                            AND ct.callsite_id = cs.callsite_id
                            AND ct.resolution_kind = 'unknown'
                           WHERE cs.snapshot_id = @snapshot_id
                               AND cs.file_id = @file_id
                               AND cs.raw_text IS NOT NULL
                               AND (
                                   cs.dispatch_kind = 'unresolved'
                                   OR ct.callsite_id IS NOT NULL
                               )
                           GROUP BY cs.raw_text
                           ORDER BY cs.raw_text
                           LIMIT 25;
                           """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", target.SnapshotId);
        cmd.Parameters.AddWithValue("file_id", target.FileId);

        var unresolved = new List<UnresolvedCallsiteInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            unresolved.Add(new UnresolvedCallsiteInfo(
                RawText: reader.GetString(reader.GetOrdinal("raw_text")),
                RawLine: reader.GetInt32(reader.GetOrdinal("raw_line"))));
        }

        return unresolved;
    }

    private async Task<List<CandidateHeaderMatch>> LoadCandidateHeadersBySymbolIdAsync(
        DbConnection connection,
        long snapshotId,
        long symbolId,
        string querySource,
        CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT
                               s.symbol_id,
                               COALESCE(NULLIF(s.qualified_name, ''), s.name) AS symbol_name,
                               COALESCE(f.real_path, f.path) AS header_path,
                               COUNT(sd.decl_id)::BIGINT AS declaration_count,
                               COUNT(*) FILTER (WHERE sd.role = 'def')::BIGINT AS definition_count,
                               COALESCE(MAX(p.confidence), 0.500)::NUMERIC(4,3) AS max_confidence,
                               MIN(
                                   CASE
                                       WHEN f.language = 'header' THEN 0
                                       WHEN lower(f.path) LIKE '%.h'
                                         OR lower(f.path) LIKE '%.hh'
                                         OR lower(f.path) LIKE '%.hpp'
                                         OR lower(f.path) LIKE '%.hxx'
                                         OR lower(f.path) LIKE '%.inc'
                                         OR lower(f.path) LIKE '%.ipp' THEN 1
                                       WHEN f.is_external = TRUE THEN 2
                                       ELSE 3
                                   END
                               ) AS header_rank
                           FROM symbol s
                           JOIN symbol_decl sd
                             ON sd.snapshot_id = s.snapshot_id
                            AND sd.symbol_id = s.symbol_id
                           JOIN file f
                             ON f.snapshot_id = s.snapshot_id
                            AND f.file_id = sd.file_id
                           LEFT JOIN provenance p
                             ON p.provenance_id = sd.provenance_id
                           WHERE s.snapshot_id = @snapshot_id
                               AND s.symbol_id = @symbol_id
                           GROUP BY s.symbol_id, COALESCE(NULLIF(s.qualified_name, ''), s.name), COALESCE(f.real_path, f.path)
                           ORDER BY header_rank, declaration_count DESC, header_path
                           LIMIT 20;
                           """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", snapshotId);
        cmd.Parameters.AddWithValue("symbol_id", symbolId);

        var matches = new List<CandidateHeaderMatch>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var resolvedSymbolId = GetNullableInt64(reader, "symbol_id");
            matches.Add(new CandidateHeaderMatch(
                SymbolId: resolvedSymbolId,
                SymbolName: reader.GetString(reader.GetOrdinal("symbol_name")),
                HeaderPath: reader.GetString(reader.GetOrdinal("header_path")),
                DeclarationCount: reader.GetInt64(reader.GetOrdinal("declaration_count")),
                DefinitionCount: reader.GetInt64(reader.GetOrdinal("definition_count")),
                Confidence: ReadDecimalFlexible(reader, "max_confidence"),
                MatchKind: "symbol_id",
                MatchRank: 0,
                HeaderRank: reader.GetInt32(reader.GetOrdinal("header_rank")),
                Query: symbolId.ToString(),
                QuerySource: querySource));
        }

        return matches;
    }

    private async Task<List<CandidateHeaderMatch>> LoadCandidateHeadersByIdentifierAsync(
        DbConnection connection,
        long snapshotId,
        string identifier,
        string querySource,
        CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT
                               s.symbol_id,
                               COALESCE(NULLIF(s.qualified_name, ''), s.name) AS symbol_name,
                               COALESCE(f.real_path, f.path) AS header_path,
                               COUNT(sd.decl_id)::BIGINT AS declaration_count,
                               COUNT(*) FILTER (WHERE sd.role = 'def')::BIGINT AS definition_count,
                               COALESCE(MAX(p.confidence), 0.500)::NUMERIC(4,3) AS max_confidence,
                               MIN(
                                   CASE
                                       WHEN lower(COALESCE(NULLIF(s.qualified_name, ''), s.name)) = lower(@identifier) THEN 0
                                       WHEN lower(s.name) = lower(@identifier) THEN 1
                                       WHEN lower(s.qualified_name) LIKE '%::' || lower(@identifier) THEN 2
                                       ELSE 3
                                   END
                               ) AS match_rank,
                               MIN(
                                   CASE
                                       WHEN f.language = 'header' THEN 0
                                       WHEN lower(f.path) LIKE '%.h'
                                         OR lower(f.path) LIKE '%.hh'
                                         OR lower(f.path) LIKE '%.hpp'
                                         OR lower(f.path) LIKE '%.hxx'
                                         OR lower(f.path) LIKE '%.inc'
                                         OR lower(f.path) LIKE '%.ipp' THEN 1
                                       WHEN f.is_external = TRUE THEN 2
                                       ELSE 3
                                   END
                               ) AS header_rank
                           FROM symbol s
                           JOIN symbol_decl sd
                             ON sd.snapshot_id = s.snapshot_id
                            AND sd.symbol_id = s.symbol_id
                           JOIN file f
                             ON f.snapshot_id = s.snapshot_id
                            AND f.file_id = sd.file_id
                           LEFT JOIN provenance p
                             ON p.provenance_id = sd.provenance_id
                           WHERE s.snapshot_id = @snapshot_id
                               AND (
                                   lower(s.name) = lower(@identifier)
                                   OR lower(s.qualified_name) = lower(@identifier)
                                   OR lower(s.qualified_name) LIKE '%::' || lower(@identifier)
                               )
                           GROUP BY s.symbol_id, COALESCE(NULLIF(s.qualified_name, ''), s.name), COALESCE(f.real_path, f.path)
                           ORDER BY match_rank, header_rank, declaration_count DESC, header_path
                           LIMIT 30;
                           """;

        await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
        cmd.Parameters.AddWithValue("snapshot_id", snapshotId);
        cmd.Parameters.AddWithValue("identifier", identifier);

        var matches = new List<CandidateHeaderMatch>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var matchRank = reader.GetInt32(reader.GetOrdinal("match_rank"));
            var matchKind = matchRank switch
            {
                0 => "qualified_exact",
                1 => "name_exact",
                2 => "qualified_suffix",
                _ => "name_related"
            };

            matches.Add(new CandidateHeaderMatch(
                SymbolId: GetNullableInt64(reader, "symbol_id"),
                SymbolName: reader.GetString(reader.GetOrdinal("symbol_name")),
                HeaderPath: reader.GetString(reader.GetOrdinal("header_path")),
                DeclarationCount: reader.GetInt64(reader.GetOrdinal("declaration_count")),
                DefinitionCount: reader.GetInt64(reader.GetOrdinal("definition_count")),
                Confidence: ReadDecimalFlexible(reader, "max_confidence"),
                MatchKind: matchKind,
                MatchRank: matchRank,
                HeaderRank: reader.GetInt32(reader.GetOrdinal("header_rank")),
                Query: identifier,
                QuerySource: querySource));
        }

        return matches;
    }

    private async Task<IncludeCleanerProbeResult> TryProbeIncludeCleanerAsync(
        string normalizedFile,
        IReadOnlyList<IncludeUsageHeader> includes,
        CancellationToken cancellationToken)
    {
        var result = new IncludeCleanerProbeResult
        {
            Attempted = true,
            Method = "textDocument/prepareCallHierarchy",
            Available = false
        };

        try
        {
            var fileUri = MakeFileUri(normalizedFile);
            await EnsureFileOpenAsync(fileUri, cancellationToken);

            foreach (var include in includes.Where(x => x.IncludeLine >= 0).Take(12))
            {
                var response = await _clangd.SendRequestAsync(
                    "textDocument/prepareCallHierarchy",
                    new
                    {
                        textDocument = new { uri = fileUri },
                        position = new { line = include.IncludeLine, character = 0 }
                    },
                    cancellationToken);

                if (response == null ||
                    !response.Value.TryGetProperty("result", out var responseResult) ||
                    responseResult.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var itemCount = responseResult.GetArrayLength();
                if (itemCount <= 0)
                {
                    continue;
                }

                result.Available = true;
                result.Hints.Add(new IncludeCleanerHint(
                    HeaderPath: include.HeaderPath,
                    IncludeLine: include.IncludeLine + 1,
                    ItemCount: itemCount,
                    Rationale: $"clangd returned {itemCount} call-hierarchy item(s) at the include line, which is include-cleaner-style evidence that symbols resolve through this include."));
            }

            if (!result.Available)
            {
                result.Notes.Add("No include-level hints from clangd; fallback classification used database file_dependency + symbol usage joins.");
            }
        }
        catch (Exception ex)
        {
            result.Notes.Add($"clangd include-cleaner probe failed: {ex.Message}");
        }

        return result;
    }

    private static string BuildUsedHeaderRationale(IncludeUsageHeader header)
    {
        var sample = header.SampleSymbols.Count > 0
            ? $" Example symbols: {string.Join(", ", header.SampleSymbols.Take(4))}."
            : string.Empty;
        return $"Direct {header.DirectiveKind} is active and maps to {header.UsedSymbolCount} referenced symbol(s) with confidence {header.MaxConfidence:F3}.{sample}";
    }

    private static string BuildInactiveHeaderRationale(IncludeUsageHeader header)
    {
        return $"Direct {header.DirectiveKind} has no matched referenced symbols in current snapshot facts (confidence {header.MaxConfidence:F3}), so it is removable unless it is needed for macros, side effects, or transitive policies.";
    }

    private static string BuildMissingHeaderRationale(MissingHeaderRecommendation missing)
    {
        var baseRationale = missing.Source switch
        {
            "referenced_symbol_no_direct_include" =>
                $"Symbol '{missing.SymbolName}' is referenced in this file, but declarations were not found in any direct include. Candidate headers declare the symbol.",
            "unresolved_callsite" =>
                $"Unresolved call expression '{missing.RawText}' indicates a missing declaration context. Candidate headers are ranked from matching symbol declarations.",
            _ =>
                $"Header candidates are suggested from declaration matches for symbol '{missing.SymbolName}'."
        };

        return $"{baseRationale} (confidence={missing.Confidence:F3})";
    }

    private static string BuildCandidateHeaderRationale(CandidateHeaderMatch candidate)
    {
        var definitionText = candidate.DefinitionCount > 0
            ? $", including {candidate.DefinitionCount} definition(s)"
            : string.Empty;
        return $"Matched via {candidate.MatchKind} for query '{candidate.Query}' with {candidate.DeclarationCount} declaration(s){definitionText} in this header (confidence={candidate.Confidence:F3}).";
    }

    private static string FormatFlowPortName(string portKind, string label, int ordinal)
    {
        var trimmedLabel = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        return portKind switch
        {
            "this" => "this",
            "param" => $"param {ordinal}",
            "return" => "return",
            "field" => string.IsNullOrWhiteSpace(trimmedLabel) ? "field" : $"field {trimmedLabel}",
            "global" => string.IsNullOrWhiteSpace(trimmedLabel) ? "global" : $"global {trimmedLabel}",
            "capture" => string.IsNullOrWhiteSpace(trimmedLabel) ? "capture" : $"capture {trimmedLabel}",
            _ => trimmedLabel ?? portKind
        };
    }

    private static string NormalizeFlowKind(string flowKind)
    {
        return flowKind.Equals("ownership_transfer", StringComparison.OrdinalIgnoreCase)
            ? "ownership"
            : flowKind;
    }

    private static string? TryGetOptionalStringArgument(JsonElement args, params string[] names)
    {
        foreach (var name in names)
        {
            if (!args.TryGetProperty(name, out var valueElement) || valueElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = valueElement.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static long? TryGetOptionalInt64Argument(JsonElement args, params string[] names)
    {
        foreach (var name in names)
        {
            if (!args.TryGetProperty(name, out var valueElement))
            {
                continue;
            }

            if (valueElement.ValueKind == JsonValueKind.Number && valueElement.TryGetInt64(out var numericValue))
            {
                return numericValue;
            }

            if (valueElement.ValueKind == JsonValueKind.String &&
                long.TryParse(valueElement.GetString(), out var parsedValue))
            {
                return parsedValue;
            }
        }

        return null;
    }

    private static string? ExtractIdentifierFromRawText(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        var matches = UnresolvedIdentifierRegex.Matches(rawText);
        if (matches.Count == 0)
        {
            return null;
        }

        return matches[matches.Count - 1].Value;
    }

    private static List<MissingHeaderRecommendation> DeduplicateMissingHeaders(IEnumerable<MissingHeaderRecommendation> missingHeaders)
    {
        var merged = new Dictionary<string, MissingHeaderRecommendation>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in missingHeaders)
        {
            var key = $"{entry.Source}|{entry.SymbolId?.ToString() ?? ""}|{entry.SymbolName}|{entry.RawText ?? ""}";
            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = entry with
                {
                    CandidateHeaders = DistinctNonEmpty(entry.CandidateHeaders),
                    Confidence = Math.Max(entry.Confidence, 0m)
                };
                continue;
            }

            merged[key] = existing with
            {
                CandidateHeaders = DistinctNonEmpty(existing.CandidateHeaders.Concat(entry.CandidateHeaders)),
                Confidence = Math.Max(existing.Confidence, entry.Confidence)
            };
        }

        return merged.Values
            .OrderBy(x => x.SymbolName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Source, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<BuildExplainInfo?> TryGetBuildExplainFromPostgresAsync(string normalizedFile, CancellationToken cancellationToken)
    {
        if (!_connectionFactory.IsConfigured)
        {
            return null;
        }

        try
        {
            await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

            const string sql = """
                               SELECT
                                   s.snapshot_id,
                                   f.path AS file_path,
                                   f.file_id,
                                   sf.path AS source_file_path,
                                   bc.build_config_id,
                                   bc.command_origin,
                                   bc.compiler,
                                   bc.language_standard,
                                   bc.target_triple,
                                   bc.sysroot,
                                   bc.working_directory,
                                   bc.command_text,
                                   bc.argv_json,
                                   bc.output_path,
                                   pc.context_kind,
                                   pc.confidence,
                                   pc.borrowed_from_build_config_id,
                                   pc.parse_errors_json
                               FROM file f
                               JOIN snapshot s ON s.snapshot_id = f.snapshot_id
                               LEFT JOIN parse_context pc
                                   ON pc.snapshot_id = f.snapshot_id
                                   AND pc.file_id = f.file_id
                               LEFT JOIN build_config bc
                                   ON bc.snapshot_id = pc.snapshot_id
                                   AND bc.build_config_id = pc.build_config_id
                               LEFT JOIN file sf
                                   ON sf.snapshot_id = bc.snapshot_id
                                   AND sf.file_id = bc.source_file_id
                               WHERE s.snapshot_id = (
                                   SELECT snapshot_id
                                   FROM snapshot
                                   WHERE repo_root = @repo_root
                                     AND is_archived = FALSE
                                   ORDER BY created_at DESC, snapshot_id DESC
                                   LIMIT 1
                               )
                                   AND (f.path = @file_path OR f.real_path = @file_path)
                               ORDER BY
                                   CASE
                                       WHEN bc.build_config_id IS NULL THEN 3
                                       WHEN f.file_id = bc.source_file_id THEN 0
                                       ELSE 1
                                   END,
                                   pc.confidence DESC NULLS LAST,
                                   pc.parse_context_id DESC NULLS LAST
                               LIMIT 1;
                               """;

            await using var cmd = connection.CreateDbCommand(_sqlBuilder, sql);
            cmd.Parameters.AddWithValue("file_path", normalizedFile);
            cmd.Parameters.AddWithValue("repo_root", NormalizePath(_clangd.WorkspaceRoot));

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var argv = ParseJsonStringArray(ReadJsonAsString(reader, "argv_json"));
            var commandText = GetNullableString(reader, "command_text");
            if (argv.Count == 0 && !string.IsNullOrWhiteSpace(commandText))
            {
                argv = SplitCommand(commandText).ToList();
            }

            if (argv.Count == 0)
            {
                return null;
            }

            var parsePayload = ReadJsonAsString(reader, "parse_errors_json");
            var (parseWarnings, parseErrors) = ExtractParseMessages(parsePayload);

            var filePath = FirstNonEmpty(GetNullableString(reader, "file_path"), normalizedFile) ?? normalizedFile;
            var sourcePath = GetNullableString(reader, "source_file_path");
            var parseContextKind = GetNullableString(reader, "context_kind");
            var borrowedBuildConfigId = GetNullableInt64(reader, "borrowed_from_build_config_id");

            var commandOrigin = FirstNonEmpty(GetNullableString(reader, "command_origin"), "exact") ?? "exact";
            var shouldMarkBorrowed = borrowedBuildConfigId.HasValue
                                     || !string.IsNullOrWhiteSpace(sourcePath) && !string.Equals(filePath, sourcePath, StringComparison.Ordinal)
                                     || string.Equals(parseContextKind, "header_view", StringComparison.Ordinal);
            if (shouldMarkBorrowed)
            {
                commandOrigin = "borrowed";
            }

            return new BuildExplainInfo
            {
                FilePath = filePath,
                SourceFile = sourcePath,
                ResolutionSource = "postgres",
                CommandOrigin = commandOrigin,
                WorkingDirectory = FirstNonEmpty(GetNullableString(reader, "working_directory"), _clangd.WorkspaceRoot) ?? _clangd.WorkspaceRoot,
                Argv = argv,
                CommandText = commandText,
                Compiler = GetNullableString(reader, "compiler"),
                LanguageStandard = GetNullableString(reader, "language_standard"),
                TargetTriple = GetNullableString(reader, "target_triple"),
                Sysroot = GetNullableString(reader, "sysroot"),
                OutputPath = GetNullableString(reader, "output_path"),
                SnapshotId = GetNullableInt64(reader, "snapshot_id"),
                BuildConfigId = GetNullableInt64(reader, "build_config_id"),
                ParseContextKind = parseContextKind,
                ParseContextConfidence = GetNullableDecimal(reader, "confidence"),
                ParseWarnings = parseWarnings,
                ParseErrors = parseErrors
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed querying build context from database for {FilePath}", normalizedFile);
            return null;
        }
    }

    private async Task<BuildExplainInfo?> TryGetBuildExplainFromCompileCommandsAsync(string normalizedFile, CancellationToken cancellationToken)
    {
        var compileCommandsPath = _clangd.CompileCommandsPath;
        if (string.IsNullOrWhiteSpace(compileCommandsPath) || !File.Exists(compileCommandsPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(compileCommandsPath);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var commands = new List<CompileCommandCandidate>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("file", out var fileElement) || fileElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var directory = item.TryGetProperty("directory", out var dirElement) && dirElement.ValueKind == JsonValueKind.String
                    ? dirElement.GetString()
                    : null;
                var workingDirectory = NormalizePath(FirstNonEmpty(directory, _clangd.WorkspaceRoot) ?? _clangd.WorkspaceRoot);

                var sourceFile = fileElement.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(sourceFile))
                {
                    continue;
                }

                if (!Path.IsPathRooted(sourceFile))
                {
                    sourceFile = Path.Combine(workingDirectory, sourceFile);
                }

                var normalizedSourceFile = NormalizePath(sourceFile);
                var output = item.TryGetProperty("output", out var outputElement) && outputElement.ValueKind == JsonValueKind.String
                    ? outputElement.GetString()
                    : null;

                var argv = new List<string>();
                string? commandText = null;

                if (item.TryGetProperty("arguments", out var argumentsElement) && argumentsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var arg in argumentsElement.EnumerateArray())
                    {
                        if (arg.ValueKind == JsonValueKind.String)
                        {
                            var value = arg.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                argv.Add(value);
                            }
                        }
                    }

                    commandText = string.Join(" ", argv);
                }
                else if (item.TryGetProperty("command", out var commandElement) && commandElement.ValueKind == JsonValueKind.String)
                {
                    commandText = commandElement.GetString();
                    argv.AddRange(SplitCommand(commandText ?? string.Empty));
                }

                if (argv.Count == 0)
                {
                    argv.Add("clang++");
                    argv.Add("-c");
                    argv.Add(normalizedSourceFile);
                    commandText = string.Join(" ", argv);
                }

                commands.Add(new CompileCommandCandidate(
                    SourceFile: normalizedSourceFile,
                    WorkingDirectory: workingDirectory,
                    OutputPath: output,
                    Argv: argv,
                    CommandText: commandText));
            }

            if (commands.Count == 0)
            {
                return null;
            }

            var exact = commands.FirstOrDefault(x => string.Equals(x.SourceFile, normalizedFile, StringComparison.Ordinal));
            if (exact is not null)
            {
                return BuildExplainFromCompileCommand(normalizedFile, exact, commandOrigin: "imported");
            }

            var borrowed = SelectBorrowedCompileCommand(normalizedFile, commands);
            if (borrowed is not null)
            {
                return BuildExplainFromCompileCommand(normalizedFile, borrowed, commandOrigin: "borrowed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed loading compile_commands fallback for {FilePath}", normalizedFile);
        }

        return null;
    }

    private BuildExplainInfo BuildInferredExplain(string normalizedFile)
    {
        var workingDirectory = NormalizePath(Path.GetDirectoryName(normalizedFile) ?? _clangd.WorkspaceRoot);
        var argv = new List<string> { "clang++", "-c", normalizedFile };
        return new BuildExplainInfo
        {
            FilePath = normalizedFile,
            SourceFile = normalizedFile,
            ResolutionSource = "inferred",
            CommandOrigin = "inferred",
            WorkingDirectory = workingDirectory,
            Argv = argv,
            CommandText = string.Join(" ", argv),
            Compiler = "clang++",
            ParseWarnings = new List<string> { "No build_config row and no compile_commands entry found; command inferred." }
        };
    }

    private BuildExplainInfo BuildExplainFromCompileCommand(string requestedFile, CompileCommandCandidate command, string commandOrigin)
    {
        return new BuildExplainInfo
        {
            FilePath = requestedFile,
            SourceFile = command.SourceFile,
            ResolutionSource = "clangd.compile_commands",
            CommandOrigin = commandOrigin,
            WorkingDirectory = command.WorkingDirectory,
            Argv = command.Argv.ToList(),
            CommandText = command.CommandText,
            Compiler = command.Argv.Count > 0 ? command.Argv[0] : null,
            LanguageStandard = ParseArgValue(command.Argv, "-std", "--std"),
            TargetTriple = ParseArgValue(command.Argv, "-target", "--target"),
            Sysroot = ParseArgValue(command.Argv, "--sysroot", "-isysroot"),
            OutputPath = command.OutputPath,
            ParseWarnings = new List<string> { "Using compile_commands fallback because no matching database build_config context was found." }
        };
    }

    private static CompileCommandCandidate? SelectBorrowedCompileCommand(string requestedFile, IReadOnlyList<CompileCommandCandidate> commands)
    {
        var requestedDirectory = NormalizePath(Path.GetDirectoryName(requestedFile) ?? ".");
        var requestedName = Path.GetFileNameWithoutExtension(requestedFile);

        var scored = commands
            .Select(command =>
            {
                var score = 0;
                var sourceDirectory = NormalizePath(Path.GetDirectoryName(command.SourceFile) ?? ".");
                if (string.Equals(sourceDirectory, requestedDirectory, StringComparison.Ordinal))
                {
                    score += 100;
                }

                if (string.Equals(Path.GetFileNameWithoutExtension(command.SourceFile), requestedName, StringComparison.Ordinal))
                {
                    score += 80;
                }

                if (command.SourceFile.StartsWith(requestedDirectory + "/", StringComparison.Ordinal))
                {
                    score += 20;
                }

                return new { command, score };
            })
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.command.SourceFile.Length)
            .FirstOrDefault();

        return scored?.command;
    }

    private string NormalizeInputPath(string filePath)
    {
        var path = filePath;
        if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            path = uri.LocalPath;
        }

        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(_clangd.WorkspaceRoot, path);
        }

        return NormalizePath(path);
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).Replace('\\', '/');
    }

    private static string? ParseArgValue(IReadOnlyList<string> argv, params string[] argNames)
    {
        for (var i = 0; i < argv.Count; i++)
        {
            var arg = argv[i];
            foreach (var argName in argNames)
            {
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

        return null;
    }

    private static string? NormalizeLanguageStandard(string? standard)
    {
        if (string.IsNullOrWhiteSpace(standard))
        {
            return null;
        }

        return standard.StartsWith("-std=", StringComparison.Ordinal)
            ? standard[5..]
            : standard;
    }

    private static List<string> ExtractDefines(IReadOnlyList<string> argv)
    {
        var defines = new List<string>();
        for (var i = 0; i < argv.Count; i++)
        {
            var arg = argv[i];
            if (arg.Equals("-D", StringComparison.Ordinal) && i + 1 < argv.Count)
            {
                defines.Add(argv[i + 1]);
                i++;
                continue;
            }

            if (arg.StartsWith("-D", StringComparison.Ordinal) && arg.Length > 2)
            {
                defines.Add(arg[2..]);
            }
        }

        return DistinctNonEmpty(defines);
    }

    private static List<string> ExtractIncludePaths(IReadOnlyList<string> argv, string workingDirectory)
    {
        var includePaths = new List<string>();
        for (var i = 0; i < argv.Count; i++)
        {
            var arg = argv[i];
            string? path = null;

            if (arg.Equals("-I", StringComparison.Ordinal) && i + 1 < argv.Count)
            {
                path = argv[i + 1];
                i++;
            }
            else if (arg.StartsWith("-I", StringComparison.Ordinal) && arg.Length > 2)
            {
                path = arg[2..];
            }
            else if (arg.Equals("-isystem", StringComparison.Ordinal) && i + 1 < argv.Count)
            {
                path = argv[i + 1];
                i++;
            }
            else if (arg.StartsWith("-isystem", StringComparison.Ordinal) && arg.Length > "-isystem".Length)
            {
                path = arg["-isystem".Length..];
            }
            else if (arg.Equals("-iquote", StringComparison.Ordinal) && i + 1 < argv.Count)
            {
                path = argv[i + 1];
                i++;
            }
            else if (arg.Equals("-idirafter", StringComparison.Ordinal) && i + 1 < argv.Count)
            {
                path = argv[i + 1];
                i++;
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                includePaths.Add(NormalizeMaybeRelativePath(path, workingDirectory));
            }
        }

        return DistinctNonEmpty(includePaths);
    }

    private static List<string> ExtractKeyFlags(IReadOnlyList<string> argv)
    {
        var keyFlags = new List<string>();
        for (var i = 0; i < argv.Count; i++)
        {
            var arg = argv[i];
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            if (arg is "-D" or "-I" or "-isystem" or "-iquote" or "-idirafter")
            {
                i += i + 1 < argv.Count ? 1 : 0;
                continue;
            }

            if (arg.StartsWith("-D", StringComparison.Ordinal) ||
                arg.StartsWith("-I", StringComparison.Ordinal) ||
                arg.StartsWith("-isystem", StringComparison.Ordinal))
            {
                continue;
            }

            if (arg.Equals("-x", StringComparison.Ordinal) ||
                arg.Equals("-target", StringComparison.Ordinal) ||
                arg.Equals("--target", StringComparison.Ordinal) ||
                arg.Equals("--sysroot", StringComparison.Ordinal) ||
                arg.Equals("-isysroot", StringComparison.Ordinal) ||
                arg.Equals("-stdlib", StringComparison.Ordinal))
            {
                if (i + 1 < argv.Count)
                {
                    keyFlags.Add($"{arg} {argv[i + 1]}");
                    i++;
                }

                continue;
            }

            if (arg.StartsWith("-std", StringComparison.Ordinal) ||
                arg.StartsWith("-target", StringComparison.Ordinal) ||
                arg.StartsWith("--target", StringComparison.Ordinal) ||
                arg.StartsWith("--sysroot", StringComparison.Ordinal) ||
                arg.StartsWith("-isysroot", StringComparison.Ordinal) ||
                arg.StartsWith("-stdlib", StringComparison.Ordinal) ||
                arg.StartsWith("-f", StringComparison.Ordinal) ||
                arg.StartsWith("-m", StringComparison.Ordinal) ||
                arg.StartsWith("-W", StringComparison.Ordinal) ||
                arg.StartsWith("-O", StringComparison.Ordinal) ||
                arg.StartsWith("-g", StringComparison.Ordinal) ||
                arg is "-c" or "-pthread" or "-fPIC")
            {
                keyFlags.Add(arg);
            }
        }

        return DistinctNonEmpty(keyFlags);
    }

    private static string NormalizeMaybeRelativePath(string candidatePath, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return candidatePath;
        }

        if (Path.IsPathRooted(candidatePath))
        {
            return NormalizePath(candidatePath);
        }

        return NormalizePath(Path.Combine(workingDirectory, candidatePath));
    }

    private static (List<string> Warnings, List<string> Errors) ExtractParseMessages(string? parsePayload)
    {
        var warnings = new List<string>();
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(parsePayload))
        {
            return (warnings, errors);
        }

        try
        {
            using var doc = JsonDocument.Parse(parsePayload);
            CollectDiagnostics(doc.RootElement, severityHint: null, warnings, errors);
        }
        catch (Exception ex)
        {
            warnings.Add($"Unable to parse parse_errors_json payload: {ex.Message}");
        }

        return (DistinctNonEmpty(warnings), DistinctNonEmpty(errors));
    }

    private static void CollectDiagnostics(
        JsonElement element,
        string? severityHint,
        List<string> warnings,
        List<string> errors)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectDiagnostics(item, severityHint, warnings, errors);
                }

                break;
            case JsonValueKind.Object:
                var severity = severityHint;
                if (TryGetString(element, "severity", out var severityValue))
                {
                    severity = severityValue;
                }
                else if (TryGetString(element, "level", out var levelValue))
                {
                    severity = levelValue;
                }

                if (TryGetString(element, "message", out var messageValue) ||
                    TryGetString(element, "text", out messageValue) ||
                    TryGetString(element, "detail", out messageValue))
                {
                    AddDiagnostic(messageValue, severity, warnings, errors);
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("message") ||
                        property.NameEquals("text") ||
                        property.NameEquals("detail") ||
                        property.NameEquals("severity") ||
                        property.NameEquals("level"))
                    {
                        continue;
                    }

                    var nestedSeverity = severity;
                    if (property.Name.Contains("warn", StringComparison.OrdinalIgnoreCase))
                    {
                        nestedSeverity = "warning";
                    }
                    else if (property.Name.Contains("error", StringComparison.OrdinalIgnoreCase))
                    {
                        nestedSeverity = "error";
                    }

                    CollectDiagnostics(property.Value, nestedSeverity, warnings, errors);
                }

                break;
            case JsonValueKind.String:
                AddDiagnostic(element.GetString(), severityHint, warnings, errors);
                break;
        }
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = null;
        return false;
    }

    private static void AddDiagnostic(string? message, string? severity, List<string> warnings, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(severity) && severity.Contains("warn", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(message);
            return;
        }

        if (!string.IsNullOrWhiteSpace(severity) && severity.Contains("note", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(message);
            return;
        }

        errors.Add(message);
    }

    private static string? GetNullableString(DbDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static long? GetNullableInt64(DbDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : ConvertToInt64(reader.GetValue(ordinal));
    }

    private static decimal? GetNullableDecimal(DbDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : ConvertToDecimal(reader.GetValue(ordinal));
    }

    private static decimal ReadDecimalFlexible(DbDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? 0m : ConvertToDecimal(reader.GetValue(ordinal));
    }

    private static bool ReadBooleanFlexible(DbDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return false;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            bool flag => flag,
            byte b => b != 0,
            short s => s != 0,
            int i => i != 0,
            long l => l != 0,
            decimal d => d != 0m,
            double dbl => Math.Abs(dbl) > double.Epsilon,
            string text when bool.TryParse(text, out var parsed) => parsed,
            string text when long.TryParse(text, out var parsedLong) => parsedLong != 0,
            _ => Convert.ToBoolean(value)
        };
    }

    private static DateTime ReadDateTimeFlexible(DbDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return DateTime.MinValue;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTime dateTime => dateTime,
            DateTimeOffset dto => dto.UtcDateTime,
            string text when DateTime.TryParse(text, out var parsed) => parsed,
            _ => Convert.ToDateTime(value)
        };
    }

    private static long ConvertToInt64(object value)
    {
        return value switch
        {
            long l => l,
            int i => i,
            short s => s,
            byte b => b,
            decimal d => (long)d,
            double dbl => (long)dbl,
            float f => (long)f,
            string text when long.TryParse(text, out var parsed) => parsed,
            _ => Convert.ToInt64(value)
        };
    }

    private static decimal ConvertToDecimal(object value)
    {
        return value switch
        {
            decimal d => d,
            double dbl => Convert.ToDecimal(dbl),
            float f => Convert.ToDecimal(f),
            long l => l,
            int i => i,
            short s => s,
            byte b => b,
            string text when decimal.TryParse(text, out var parsed) => parsed,
            _ => Convert.ToDecimal(value)
        };
    }

    private static JsonElement ParseJsonElementOrDefault(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return JsonSerializer.SerializeToElement(new { });
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { raw = rawJson });
        }
    }

    private static string? ReadJsonAsString(DbDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var raw = reader.GetValue(ordinal);
        return raw switch
        {
            string s => s,
            JsonDocument doc => doc.RootElement.GetRawText(),
            JsonElement element => element.GetRawText(),
            _ => raw.ToString()
        };
    }

    private static List<string> ReadTextArray(DbDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return new List<string>();
        }

        var raw = reader.GetValue(ordinal);
        return raw switch
        {
            string[] values => DistinctNonEmpty(values),
            Array array => DistinctNonEmpty(array.OfType<string>()),
            string text => DistinctNonEmpty(ParseJsonStringArray(text)),
            _ => new List<string>()
        };
    }

    private static List<string> ParseJsonStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<string>();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new List<string>();
            }

            var result = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        result.Add(value);
                    }
                }
            }

            return result;
        }
        catch
        {
            return new List<string>();
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

    private static List<string> DistinctNonEmpty(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (seen.Add(value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private sealed record SnapshotFileRef(long SnapshotId, long FileId, string FilePath);

    private sealed record IncludeUsageHeader(
        long ToFileId,
        string HeaderPath,
        string? LiteralText,
        string DirectiveKind,
        long SupportCount,
        int IncludeLine,
        decimal MaxConfidence,
        long UsedSymbolCount,
        List<string> SampleSymbols);

    private sealed record MissingHeaderRecommendation(
        long? SymbolId,
        string SymbolName,
        string Source,
        string? RawText,
        int? RawLine,
        decimal Confidence,
        List<string> CandidateHeaders);

    private sealed record UnresolvedCallsiteInfo(string RawText, int RawLine);

    private sealed record CandidateHeaderMatch(
        long? SymbolId,
        string SymbolName,
        string HeaderPath,
        long DeclarationCount,
        long DefinitionCount,
        decimal Confidence,
        string MatchKind,
        int MatchRank,
        int HeaderRank,
        string Query,
        string QuerySource);

    private sealed class SnapshotStatusInfo
    {
        public required long SnapshotId { get; init; }
        public required string RepoRoot { get; init; }
        public required string WorkspaceHash { get; init; }
        public string? VcsCommit { get; init; }
        public long? ParentSnapshotId { get; init; }
        public required string Kind { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required DateTime LastUpdatedAt { get; init; }
        public required string IndexStatus { get; init; }
        public required long TotalSymbols { get; init; }
        public required long TotalRefs { get; init; }
        public required long TotalFiles { get; init; }
        public required long ParsedFiles { get; init; }
        public required long ParseContextCount { get; init; }
        public required long ParseErrorContexts { get; init; }
        public required long BorrowedContexts { get; init; }
        public required decimal AvgParseConfidence { get; init; }
    }

    private sealed class IncludeCleanerProbeResult
    {
        public bool Attempted { get; init; }
        public required string Method { get; init; }
        public bool Available { get; set; }
        public List<IncludeCleanerHint> Hints { get; } = new();
        public List<string> Notes { get; } = new();
    }

    private sealed record IncludeCleanerHint(
        string HeaderPath,
        int IncludeLine,
        int ItemCount,
        string Rationale);

    private sealed class IncludeExplainInfo
    {
        public required string FilePath { get; init; }
        public required long SnapshotId { get; init; }
        public required long FileId { get; init; }
        public required long ReferencedSymbolCount { get; init; }
        public required List<IncludeUsageHeader> DirectIncludes { get; init; }
        public required List<MissingHeaderRecommendation> MissingHeaders { get; init; }
        public required List<CandidateHeaderMatch> CandidateHeaders { get; init; }
        public required List<UnresolvedCallsiteInfo> UnresolvedCallsites { get; init; }
    }

    private sealed class ContextPackSymbolCard
    {
        public required long SnapshotId { get; init; }
        public required long SymbolId { get; init; }
        public required string Kind { get; init; }
        public required string Name { get; init; }
        public required string QualifiedName { get; init; }
        public required string Visibility { get; init; }
        public required string TemplateKind { get; init; }
        public required bool IsExported { get; init; }
        public long? OwnerSymbolId { get; init; }
        public string? OwnerQualifiedName { get; init; }
        public string? SignatureText { get; init; }
        public required long DeclCount { get; init; }
        public required long DefCount { get; init; }
        public required long FwdDeclCount { get; init; }
        public required JsonElement TopCallers { get; init; }
        public required JsonElement TopCallees { get; init; }
        public required JsonElement TopRefs { get; init; }
        public required List<string> KeyEffects { get; init; }
        public required long MacroHazardCount { get; init; }
        public required long MacroExpandCount { get; init; }
        public long? PrimaryFileId { get; init; }
        public string? PrimaryFilePath { get; init; }
    }

    private sealed class ContextPackCallEdge
    {
        public required long FromSymbolId { get; init; }
        public required string FromSymbolName { get; init; }
        public required long ToSymbolId { get; init; }
        public required string ToSymbolName { get; init; }
        public required int Depth { get; init; }
        public required long CallsiteCount { get; init; }
        public decimal? MaxConfidence { get; init; }
    }

    private sealed class ContextPackRelatedType
    {
        public required long OwnerSymbolId { get; init; }
        public required string OwnerSymbolName { get; init; }
        public required long TargetSymbolId { get; init; }
        public required string TargetSymbolName { get; init; }
        public required string EdgeKind { get; init; }
        public required long EdgeCount { get; init; }
    }

    private sealed class ContextPackIncludeContext
    {
        public required long SnapshotId { get; init; }
        public required long FileId { get; init; }
        public required string FilePath { get; init; }
        public required long IncludeCount { get; init; }
        public required long ImportCount { get; init; }
        public required long DefinedSymbolCount { get; init; }
        public required long ReferencedSymbolCount { get; init; }
        public required JsonElement TopExternalDependencies { get; init; }
        public required decimal MacroDensity { get; init; }
        public required decimal ParseCoverage { get; init; }
        public required List<string> RelevantHeaders { get; init; }
    }

    private sealed class FlowSummaryRow
    {
        public required long SnapshotId { get; init; }
        public required long CallableSymbolId { get; init; }
        public required string CallableSymbolName { get; init; }
        public required string FlowKind { get; init; }
        public required string ConditionKind { get; init; }
        public required string Engine { get; init; }
        public required long FromPortId { get; init; }
        public required long ToPortId { get; init; }
        public required string FromPortKind { get; init; }
        public required string FromLabel { get; init; }
        public required int FromOrdinal { get; init; }
        public required int FromPointeeDepth { get; init; }
        public string? FromTypeName { get; init; }
        public required string ToPortKind { get; init; }
        public required string ToLabel { get; init; }
        public required int ToOrdinal { get; init; }
        public required int ToPointeeDepth { get; init; }
        public string? ToTypeName { get; init; }
        public required long ProvenanceId { get; init; }
        public required string Extractor { get; init; }
        public required string Method { get; init; }
        public required string Exactness { get; init; }
        public required decimal Confidence { get; init; }
        public required JsonElement EvidenceJson { get; init; }
    }

    private sealed class BuildExplainInfo
    {
        public required string FilePath { get; init; }
        public string? SourceFile { get; init; }
        public required string ResolutionSource { get; init; }
        public required string CommandOrigin { get; init; }
        public required string WorkingDirectory { get; init; }
        public required List<string> Argv { get; init; }
        public string? CommandText { get; init; }
        public string? Compiler { get; init; }
        public string? LanguageStandard { get; init; }
        public string? TargetTriple { get; init; }
        public string? Sysroot { get; init; }
        public string? OutputPath { get; init; }
        public long? SnapshotId { get; init; }
        public long? BuildConfigId { get; init; }
        public string? ParseContextKind { get; init; }
        public decimal? ParseContextConfidence { get; init; }
        public List<string> ParseWarnings { get; init; } = new();
        public List<string> ParseErrors { get; init; } = new();
    }

    private sealed record CompileCommandCandidate(
        string SourceFile,
        string WorkingDirectory,
        string? OutputPath,
        IReadOnlyList<string> Argv,
        string? CommandText);

    private async Task<string> SymbolResolveAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var query = args.GetProperty("query").GetString()!;
        var limit = args.TryGetProperty("limit", out var limitProp) ? limitProp.GetInt32() : 20;

        var lspParams = new { query };
        var response = await _clangd.SendRequestAsync("workspace/symbol", lspParams, cancellationToken);

        if (response == null)
            return "No response from clangd";

        if (!response.Value.TryGetProperty("result", out var result))
        {
            var errorMsg = response.Value.TryGetProperty("error", out var err) 
                ? err.GetProperty("message").GetString() 
                : "Unknown error";
            return $"Error: {errorMsg}";
        }

        var symbols = result.EnumerateArray().Take(limit).ToList();
        if (symbols.Count == 0)
            return $"No symbols found matching '{query}'";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {symbols.Count} symbol(s) matching '{query}':\n");

        foreach (var sym in symbols)
        {
            var name = sym.GetProperty("name").GetString();
            var kind = GetSymbolKind(sym.GetProperty("kind").GetInt32());
            var location = sym.GetProperty("location");
            var uri = location.GetProperty("uri").GetString();
            var range = location.GetProperty("range");
            var startLine = range.GetProperty("start").GetProperty("line").GetInt32() + 1;
            var containerName = sym.TryGetProperty("containerName", out var cn) && cn.ValueKind != JsonValueKind.Null 
                ? cn.GetString() 
                : null;

            sb.AppendLine($"  {name} ({kind})");
            if (containerName != null)
                sb.AppendLine($"    in: {containerName}");
            sb.AppendLine($"    at: {uri}:{startLine}");
            sb.AppendLine("    confidence: 1.000");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private async Task<string> SymbolRefsAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var file = args.GetProperty("file").GetString()!;
        var line = args.GetProperty("line").GetInt32();
        var column = args.GetProperty("column").GetInt32();

        var fileUri = MakeFileUri(file);
        
        // Ensure file is open in clangd
        await EnsureFileOpenAsync(fileUri, cancellationToken);

        var lspParams = new
        {
            textDocument = new { uri = fileUri },
            position = new { line = line - 1, character = column - 1 },
            context = new { includeDeclaration = true }
        };

        var response = await _clangd.SendRequestAsync("textDocument/references", lspParams, cancellationToken);

        _logger.LogInformation("LSP references response: {Response}", response?.ToString());

        if (response == null)
            return "No response from clangd";

        if (!response.Value.TryGetProperty("result", out var result))
        {
            _logger.LogWarning("No 'result' in response. Keys: {Keys}", string.Join(", ", response.Value.EnumerateObject().Select(p => p.Name)));
            var errorMsg = response.Value.TryGetProperty("error", out var err) 
                ? err.GetProperty("message").GetString() 
                : "Unknown error";
            return $"Error: {errorMsg}";
        }
        
        _logger.LogInformation("Result kind: {Kind}, value: {Value}", result.ValueKind, result.ToString()?[..Math.Min(200, result.ToString()?.Length ?? 0)]);

        if (result.ValueKind == JsonValueKind.Null)
            return "No references found";

        var refs = result.EnumerateArray().ToList();
        if (refs.Count == 0)
            return "No references found";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {refs.Count} reference(s):\n");

        foreach (var r in refs)
        {
            var uri = r.GetProperty("uri").GetString();
            var range = r.GetProperty("range");
            var startLine = range.GetProperty("start").GetProperty("line").GetInt32() + 1;
            var startChar = range.GetProperty("start").GetProperty("character").GetInt32() + 1;
            sb.AppendLine($"  {uri}:{startLine}:{startChar} (confidence=1.000)");
        }

        return sb.ToString();
    }

    private async Task<string> SymbolCallersAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var file = args.GetProperty("file").GetString()!;
        var line = args.GetProperty("line").GetInt32();
        var column = args.GetProperty("column").GetInt32();
        var depth = Math.Min(args.TryGetProperty("depth", out var d) ? d.GetInt32() : 1, 3);

        var fileUri = MakeFileUri(file);
        
        // Ensure file is open in clangd
        await EnsureFileOpenAsync(fileUri, cancellationToken);

        // Prepare call hierarchy
        var prepareParams = new
        {
            textDocument = new { uri = fileUri },
            position = new { line = line - 1, character = column - 1 }
        };

        var prepareResponse = await _clangd.SendRequestAsync("textDocument/prepareCallHierarchy", prepareParams, cancellationToken);

        if (prepareResponse == null)
            return "No response from clangd";

        if (!prepareResponse.Value.TryGetProperty("result", out var prepareResult))
        {
            var errorMsg = prepareResponse.Value.TryGetProperty("error", out var err) 
                ? err.GetProperty("message").GetString() 
                : "Unknown error";
            return $"Error: {errorMsg}";
        }

        var items = prepareResult.EnumerateArray().ToList();
        if (items.Count == 0)
            return "No call hierarchy item at this position";

        var sb = new StringBuilder();
        sb.AppendLine("Callers:\n");

        foreach (var item in items)
        {
            await CollectCallersAsync(item, sb, depth, 0, cancellationToken);
        }

        return sb.ToString();
    }

    private async Task CollectCallersAsync(JsonElement item, StringBuilder sb, int maxDepth, int currentDepth, CancellationToken cancellationToken)
    {
        if (currentDepth >= maxDepth) return;

        var itemUri = item.GetProperty("uri").GetString();
        var range = item.GetProperty("range");
        var startLine = range.GetProperty("start").GetProperty("line").GetInt32() + 1;
        var name = item.GetProperty("name").GetString();
        var kind = GetSymbolKind(item.GetProperty("kind").GetInt32());

        var indent = new string(' ', currentDepth * 2);

        // Get incoming calls
        var incomingParams = new { item };
        var incomingResponse = await _clangd.SendRequestAsync("callHierarchy/incomingCalls", incomingParams, cancellationToken);

        if (incomingResponse == null || !incomingResponse.Value.TryGetProperty("result", out var incomingResult))
            return;

        var calls = incomingResult.EnumerateArray().ToList();

        foreach (var call in calls)
        {
            var from = call.GetProperty("from");
            var fromName = from.GetProperty("name").GetString();
            var fromKind = GetSymbolKind(from.GetProperty("kind").GetInt32());
            var fromUri = from.GetProperty("uri").GetString();
            var fromRange = from.GetProperty("range");
            var fromLine = fromRange.GetProperty("start").GetProperty("line").GetInt32() + 1;

            sb.AppendLine($"{indent}{fromName} ({fromKind}) at {fromUri}:{fromLine} (confidence=1.000)");

            // Recurse
            await CollectCallersAsync(from, sb, maxDepth, currentDepth + 1, cancellationToken);
        }
    }

    private async Task<string> SymbolCalleesAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var file = args.GetProperty("file").GetString()!;
        var line = args.GetProperty("line").GetInt32();
        var column = args.GetProperty("column").GetInt32();
        var depth = Math.Min(args.TryGetProperty("depth", out var d) ? d.GetInt32() : 1, 3);

        var fileUri = MakeFileUri(file);
        
        // Ensure file is open in clangd
        await EnsureFileOpenAsync(fileUri, cancellationToken);

        var prepareParams = new
        {
            textDocument = new { uri = fileUri },
            position = new { line = line - 1, character = column - 1 }
        };

        var prepareResponse = await _clangd.SendRequestAsync("textDocument/prepareCallHierarchy", prepareParams, cancellationToken);

        if (prepareResponse == null)
            return "No response from clangd";

        if (!prepareResponse.Value.TryGetProperty("result", out var prepareResult))
        {
            var errorMsg = prepareResponse.Value.TryGetProperty("error", out var err) 
                ? err.GetProperty("message").GetString() 
                : "Unknown error";
            return $"Error: {errorMsg}";
        }

        var items = prepareResult.EnumerateArray().ToList();
        if (items.Count == 0)
            return "No call hierarchy item at this position";

        var sb = new StringBuilder();
        sb.AppendLine("Callees:\n");

        foreach (var item in items)
        {
            await CollectCalleesAsync(item, sb, depth, 0, cancellationToken);
        }

        return sb.ToString();
    }

    private async Task CollectCalleesAsync(JsonElement item, StringBuilder sb, int maxDepth, int currentDepth, CancellationToken cancellationToken)
    {
        if (currentDepth >= maxDepth) return;

        var indent = new string(' ', currentDepth * 2);

        var outgoingParams = new { item };
        var outgoingResponse = await _clangd.SendRequestAsync("callHierarchy/outgoingCalls", outgoingParams, cancellationToken);

        if (outgoingResponse == null || !outgoingResponse.Value.TryGetProperty("result", out var outgoingResult))
            return;

        var calls = outgoingResult.EnumerateArray().ToList();

        foreach (var call in calls)
        {
            var to = call.GetProperty("to");
            var toName = to.GetProperty("name").GetString();
            var toKind = GetSymbolKind(to.GetProperty("kind").GetInt32());
            var toUri = to.GetProperty("uri").GetString();
            var toRange = to.GetProperty("range");
            var toLine = toRange.GetProperty("start").GetProperty("line").GetInt32() + 1;

            sb.AppendLine($"{indent}{toName} ({toKind}) at {toUri}:{toLine} (confidence=1.000)");

            // Recurse
            await CollectCalleesAsync(to, sb, maxDepth, currentDepth + 1, cancellationToken);
        }
    }

    private async Task<string> SymbolCardAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var file = args.GetProperty("file").GetString()!;
        var line = args.GetProperty("line").GetInt32();
        var column = args.GetProperty("column").GetInt32();

        var fileUri = MakeFileUri(file);
        var position = new { line = line - 1, character = column - 1 };
        
        // Ensure file is open in clangd
        await EnsureFileOpenAsync(fileUri, cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine($"Symbol Card: {file}:{line}:{column}\n");

        // Get hover info
        var hoverParams = new
        {
            textDocument = new { uri = fileUri },
            position
        };
        var hoverResponse = await _clangd.SendRequestAsync("textDocument/hover", hoverParams, cancellationToken);
        if (hoverResponse != null && hoverResponse.Value.TryGetProperty("result", out var hoverResult) && hoverResult.ValueKind != JsonValueKind.Null)
        {
            if (hoverResult.TryGetProperty("contents", out var contents))
            {
                sb.AppendLine("Signature/Docs:");
                if (contents.ValueKind == JsonValueKind.String)
                {
                    sb.AppendLine($"  {contents.GetString()}");
                }
                else if (contents.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in contents.EnumerateArray())
                    {
                        if (c.ValueKind == JsonValueKind.String)
                            sb.AppendLine($"  {c.GetString()}");
                        else if (c.TryGetProperty("value", out var v))
                            sb.AppendLine($"  {v.GetString()}");
                    }
                }
                sb.AppendLine();
            }
        }

        // Get definition
        var defParams = new
        {
            textDocument = new { uri = fileUri },
            position
        };
        var defResponse = await _clangd.SendRequestAsync("textDocument/definition", defParams, cancellationToken);
        if (defResponse != null && defResponse.Value.TryGetProperty("result", out var defResult) && defResult.ValueKind != JsonValueKind.Null)
        {
            sb.AppendLine("Definition:");
            var locs = defResult.ValueKind == JsonValueKind.Array ? defResult.EnumerateArray().ToList() : new List<JsonElement> { defResult };
            foreach (var loc in locs)
            {
                if (loc.TryGetProperty("targetUri", out var targetUri))
                {
                    // LocationLink
                    sb.AppendLine($"  {targetUri.GetString()}");
                }
                else if (loc.TryGetProperty("uri", out var uri))
                {
                    var r = loc.GetProperty("range");
                    var l = r.GetProperty("start").GetProperty("line").GetInt32() + 1;
                    sb.AppendLine($"  {uri.GetString()}:{l}");
                }
            }
            sb.AppendLine();
        }

        // Get refs count
        var refsParams = new
        {
            textDocument = new { uri = fileUri },
            position,
            context = new { includeDeclaration = true }
        };
        var refsResponse = await _clangd.SendRequestAsync("textDocument/references", refsParams, cancellationToken);
        if (refsResponse != null && refsResponse.Value.TryGetProperty("result", out var refsResult))
        {
            var refCount = refsResult.EnumerateArray().Count();
            sb.AppendLine($"References: {refCount}");
        }

        // Get callers count
        var prepareParams = new
        {
            textDocument = new { uri = fileUri },
            position
        };
        var prepareResponse = await _clangd.SendRequestAsync("textDocument/prepareCallHierarchy", prepareParams, cancellationToken);
        if (prepareResponse != null && prepareResponse.Value.TryGetProperty("result", out var prepareResult))
        {
            var items = prepareResult.EnumerateArray().ToList();
            if (items.Count > 0)
            {
                var incomingParams = new { item = items[0] };
                var incomingResponse = await _clangd.SendRequestAsync("callHierarchy/incomingCalls", incomingParams, cancellationToken);
                if (incomingResponse != null && incomingResponse.Value.TryGetProperty("result", out var incomingResult))
                {
                    var callerCount = incomingResult.EnumerateArray().Count();
                    sb.AppendLine($"Direct Callers: {callerCount}");
                }
            }
        }

        sb.AppendLine("Confidence: 1.000 (live clangd query)");

        return sb.ToString();
    }

    private async Task<string> ChangeImpactAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var file = args.GetProperty("file").GetString()!;
        var line = args.GetProperty("line").GetInt32();
        var column = args.GetProperty("column").GetInt32();
        var depth = Math.Min(args.TryGetProperty("depth", out var d) ? d.GetInt32() : 2, 5);

        var fileUri = MakeFileUri(file);
        var position = new { line = line - 1, character = column - 1 };
        
        // Ensure file is open in clangd
        await EnsureFileOpenAsync(fileUri, cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine($"Change Impact Analysis: {file}:{line}:{column} (depth={depth})\n");

        // Collect all impacted files
        var impactedFiles = new HashSet<string>();
        var visitedSymbols = new HashSet<string>();

        // Get refs
        var refsParams = new
        {
            textDocument = new { uri = fileUri },
            position,
            context = new { includeDeclaration = true }
        };
        var refsResponse = await _clangd.SendRequestAsync("textDocument/references", refsParams, cancellationToken);
        if (refsResponse != null && refsResponse.Value.TryGetProperty("result", out var refsResult))
        {
            foreach (var r in refsResult.EnumerateArray())
            {
                var uri = r.GetProperty("uri").GetString();
                if (uri != null) impactedFiles.Add(uri);
            }
        }

        // Get callers transitively
        var prepareParams = new
        {
            textDocument = new { uri = fileUri },
            position
        };
        var prepareResponse = await _clangd.SendRequestAsync("textDocument/prepareCallHierarchy", prepareParams, cancellationToken);
        if (prepareResponse != null && prepareResponse.Value.TryGetProperty("result", out var prepareResult))
        {
            foreach (var item in prepareResult.EnumerateArray())
            {
                await CollectCallerFilesAsync(item, impactedFiles, visitedSymbols, depth, 0, cancellationToken);
            }
        }

        sb.AppendLine($"Impacted Files ({impactedFiles.Count}, confidence=1.000):\n");
        foreach (var f in impactedFiles.OrderBy(x => x))
        {
            sb.AppendLine($"  {f}");
        }

        return sb.ToString();
    }

    private async Task CollectCallerFilesAsync(JsonElement item, HashSet<string> files, HashSet<string> visited, int maxDepth, int currentDepth, CancellationToken cancellationToken)
    {
        if (currentDepth >= maxDepth) return;

        var itemUri = item.GetProperty("uri").GetString();
        if (itemUri != null) files.Add(itemUri);

        var itemKey = $"{item.GetProperty("uri").GetString()}:{item.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32()}";
        if (visited.Contains(itemKey)) return;
        visited.Add(itemKey);

        var incomingParams = new { item };
        var incomingResponse = await _clangd.SendRequestAsync("callHierarchy/incomingCalls", incomingParams, cancellationToken);

        if (incomingResponse == null || !incomingResponse.Value.TryGetProperty("result", out var incomingResult))
            return;

        foreach (var call in incomingResult.EnumerateArray())
        {
            var from = call.GetProperty("from");
            await CollectCallerFilesAsync(from, files, visited, maxDepth, currentDepth + 1, cancellationToken);
        }
    }

    // Helpers

    private async Task EnsureFileOpenAsync(string fileUri, CancellationToken cancellationToken)
    {
        // Convert file:// URI to actual path
        var path = fileUri.StartsWith("file://") ? fileUri[7..] : fileUri;
        
        if (!File.Exists(path))
            return;
        
        var content = await File.ReadAllTextAsync(path, cancellationToken);
        
        // Send textDocument/didOpen notification
        var openParams = new
        {
            textDocument = new
            {
                uri = fileUri,
                languageId = "cpp",
                version = 1,
                text = content
            }
        };
        
        await _clangd.SendNotificationAsync("textDocument/didOpen", openParams, cancellationToken);
        
        // Delay to let clangd parse the file
        await Task.Delay(500, cancellationToken);
    }

    private string MakeFileUri(string path)
    {
        if (path.StartsWith("file://"))
            return path;

        // If relative, make absolute using workspace root
        if (!Path.IsPathRooted(path))
            path = Path.Combine(_clangd.WorkspaceRoot, path);

        return $"file://{path}";
    }

    private static string GetSymbolKind(int kind)
    {
        return kind switch
        {
            1 => "File",
            2 => "Module",
            3 => "Namespace",
            4 => "Package",
            5 => "Class",
            6 => "Method",
            7 => "Property",
            8 => "Field",
            9 => "Constructor",
            10 => "Enum",
            11 => "Interface",
            12 => "Function",
            13 => "Variable",
            14 => "Constant",
            15 => "String",
            16 => "Number",
            17 => "Boolean",
            18 => "Array",
            22 => "Struct",
            23 => "Event",
            24 => "Operator",
            25 => "TypeParameter",
            _ => $"Kind{kind}"
        };
    }

    private JsonElement? CreateResponse(int? id, object result)
    {
        var response = new
        {
            jsonrpc = "2.0",
            id,
            result
        };
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(response));
    }

    private JsonElement? CreateErrorResponse(int? id, string message)
    {
        var response = new
        {
            jsonrpc = "2.0",
            id,
            error = new
            {
                code = -32600,
                message
            }
        };
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(response));
    }
}
