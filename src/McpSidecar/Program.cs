using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using McpSidecar.Services.Database;
using McpSidecar.Services;

// Parse CLI args
var isExtractOnly = args.Contains("--extract") || args.Contains("-e");
var isForce = args.Contains("--force");
var isValidateOnly = args.Contains("--validate-compile-commands") || args.Contains("--validate");
var showHelp = args.Contains("--help") || args.Contains("-h");

// Parse explicit paths
var explicitWorkspace = args.SkipWhile(a => a != "--workspace" && a != "-w")
                            .Skip(1)
                            .FirstOrDefault();
var explicitCompileCommands = args.SkipWhile(a => a != "--compile-commands" && a != "-c")
                                   .Skip(1)
                                   .FirstOrDefault();

// Help output
if (showHelp)
{
    Console.WriteLine("mcp-sidecar - Build-aware code fact extraction");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  mcp-sidecar [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --extract, -e               Run extraction and exit (standalone mode)");
    Console.WriteLine("  --force                     Force full re-extraction (ignore file hashes)");
    Console.WriteLine("  --validate-compile-commands Validate compile_commands.json and show report");
    Console.WriteLine("  --workspace, -w <path>      Explicit workspace root directory");
    Console.WriteLine("  --compile-commands, -c <path>  Explicit compile_commands.json path");
    Console.WriteLine("  --help, -h                  Show this help message");
    Console.WriteLine();
    Console.WriteLine("Environment variables:");
    Console.WriteLine("  MCP_WORKSPACE_ROOT          Workspace root directory (fallback)");
    Console.WriteLine("  MCP_COMPILE_COMMANDS        Path to compile_commands.json (fallback)");
    Console.WriteLine();
    Console.WriteLine("Auto-detection:");
    Console.WriteLine("  Workspace root: Walks up from current directory looking for .git, CMakeLists.txt, etc.");
    Console.WriteLine("  compile_commands.json: Searches workspace root, build/, cmake-build-*/, out/build/");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  mcp-sidecar --extract                    # Extract from current directory");
    Console.WriteLine("  mcp-sidecar --validate-compile-commands  # Check compile_commands.json health");
    Console.WriteLine("  mcp-sidecar --extract --force            # Force full re-extraction");
    Console.WriteLine();
    return;
}

// Create logger for workspace detection
var loggerFactory = LoggerFactory.Create(builder => 
{
    builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Warning);
    builder.SetMinimumLevel(LogLevel.Information);
});
var detectionLogger = loggerFactory.CreateLogger<WorkspaceDetectionService>();

// Detect workspace and compile_commands.json
var workspaceService = new WorkspaceDetectionService(detectionLogger);
var workspaceRoot = workspaceService.DetectWorkspaceRoot(explicitWorkspace);
var compileCommandsPath = workspaceService.DetectCompileCommands(workspaceRoot, explicitCompileCommands);

// Show detected paths in extract mode
if (isExtractOnly)
{
    Console.WriteLine($"Workspace: {workspaceRoot}");
    Console.WriteLine($"Compile commands: {compileCommandsPath ?? "Not found"}");
    Console.WriteLine();
    
    // Validate compile_commands.json if found
    if (compileCommandsPath != null)
    {
        var (total, valid, missing, ageDays) = workspaceService.ValidateCompileCommands(compileCommandsPath);
        
        if (ageDays > 30)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"⚠ compile_commands.json is {ageDays} days old");
            Console.WriteLine($"  Consider regenerating with: cmake -DCMAKE_EXPORT_COMPILE_COMMANDS=ON");
            Console.ResetColor();
            Console.WriteLine();
        }
        
        if (missing.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"⚠ {missing.Count} of {total} files in compile_commands.json are missing:");
            foreach (var file in missing.Take(5))
            {
                Console.WriteLine($"  - {file}");
            }
            if (missing.Count > 5)
            {
                Console.WriteLine($"  ... and {missing.Count - 5} more");
            }
            Console.ResetColor();
            Console.WriteLine();
        }
    }
}

// --validate-compile-commands mode: validate and exit
if (isValidateOnly)
{
    if (compileCommandsPath == null)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("✗ compile_commands.json not found");
        Console.ResetColor();
        return;
    }
    
    Console.WriteLine($"Validating: {compileCommandsPath}");
    Console.WriteLine();
    
    var (total, valid, missing, ageDays) = workspaceService.ValidateCompileCommands(compileCommandsPath);
    
    Console.WriteLine($"Total entries: {total}");
    Console.WriteLine($"Valid files:   {valid}");
    Console.WriteLine($"Missing files: {missing.Count}");
    Console.WriteLine($"Age:           {ageDays} days");
    Console.WriteLine();
    
    if (missing.Count > 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Missing files:");
        foreach (var file in missing)
        {
            Console.WriteLine($"  - {file}");
        }
        Console.ResetColor();
        Console.WriteLine();
    }
    
    if (ageDays > 30)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"⚠ compile_commands.json is {ageDays} days old");
        Console.WriteLine("  Consider regenerating with:");
        Console.WriteLine("    cmake -DCMAKE_EXPORT_COMPILE_COMMANDS=ON");
        Console.ResetColor();
        Console.WriteLine();
    }
    
    // Summary
    if (missing.Count == 0 && ageDays <= 30)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("✓ compile_commands.json looks healthy");
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("⚠ compile_commands.json may need regeneration");
        Console.ResetColor();
    }
    
    return;
}

// --extract mode: run extraction standalone with progress, then exit
if (isExtractOnly)
{
    await RunExtractionAsync(workspaceRoot, compileCommandsPath, isForce);
    return;
}

// Normal MCP server mode
var builder = Host.CreateApplicationBuilder(args);

// Configure logging - send to stderr only (stdout is for MCP protocol)
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => 
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Register workspace and compile_commands paths
builder.Services.AddSingleton(new WorkspaceOptions { Root = workspaceRoot });
builder.Services.AddSingleton(new CompileCommandsOptions { Path = compileCommandsPath });

// Register services
builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
builder.Services.AddSingleton<ISqlBuilder, SqlBuilder>();
builder.Services.AddSingleton<ClangdService>();
builder.Services.AddSingleton<ExtractionService>();
builder.Services.AddSingleton<McpServer>();
builder.Services.AddHostedService<McpSidecarWorker>();

var host = builder.Build();
await host.RunAsync();

// Standalone extraction with progress display
static async Task RunExtractionAsync(string workspaceRoot, string? compileCommandsPath, bool isForce)
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    
    // Build a minimal host with just the services we need
    var builder = Host.CreateApplicationBuilder();
    
    // Configure logging
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Warning);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    
    // Add configuration
    builder.Services.AddSingleton(new WorkspaceOptions { Root = workspaceRoot });
    builder.Services.AddSingleton(new CompileCommandsOptions { Path = compileCommandsPath });
    
    // Register services (same as MCP mode)
    builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
    builder.Services.AddSingleton<ISqlBuilder, SqlBuilder>();
    builder.Services.AddSingleton<ClangdService>();
    builder.Services.AddSingleton<ExtractionService>();
    
    var app = builder.Build();
    
    var clangd = app.Services.GetRequiredService<ClangdService>();
    var extraction = app.Services.GetRequiredService<ExtractionService>();
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    
    // Set force flag if specified
    extraction.ForceExtraction = isForce;
    
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (s, e) => 
    {
        e.Cancel = true;
        cts.Cancel();
        Console.WriteLine("\nCancellation requested...");
    };
    
    Console.WriteLine();
    
    var startTime = DateTime.Now;
    var filesProcessed = 0;
    var symbolsExtracted = 0;
    var totalFiles = 0;
    
    // Progress bar update
    void UpdateProgress(int files, int symbols, int total, string? phase = null)
    {
        filesProcessed = files;
        symbolsExtracted = symbols;
        if (total > 0) totalFiles = total;
        
        var elapsed = DateTime.Now - startTime;
        var elapsedStr = elapsed.ToString(@"mm\:ss");
        
        var percent = totalFiles > 0 ? (files * 100 / totalFiles) : 0;
        
        // Build progress bar
        var barWidth = 30;
        var filled = (int)(barWidth * percent / 100.0);
        var bar = new string('█', filled) + new string('░', barWidth - filled);
        
        var phaseText = !string.IsNullOrEmpty(phase) ? $" | {phase}" : "";
        
        // Clear line and redraw
        Console.Write($"\r{bar} {percent,3}% | Files: {files:N0}/{totalFiles:N0} | Symbols: {symbols:N0} | {elapsedStr}{phaseText}   ");
    }
    
    try
    {
        // Start clangd
        Console.Write("Starting clangd...");
        await clangd.StartAsync(workspaceRoot, compileCommandsPath, cts.Token);
        Console.WriteLine(" ✓");
        
        // Get file count for progress (extraction service handles this)
        // We'll just show phase-based progress
        Console.WriteLine("Starting extraction...");
        Console.WriteLine();
        
        // Run extraction with progress
        var snapshotId = await extraction.RunInitialExtractionAsync(
            cts.Token,
            progress: new Progress<ExtractionProgress>(p =>
            {
                UpdateProgress(p.FilesProcessed, p.SymbolsExtracted, p.TotalFiles, p.CurrentPhase);
            }));
        
        var finalElapsed = DateTime.Now - startTime;
        Console.WriteLine();
        Console.WriteLine($"✓ Extraction complete: snapshot_id={snapshotId}");
        Console.WriteLine($"  Symbols: {symbolsExtracted:N0}");
        Console.WriteLine($"  Files: {filesProcessed:N0}");
        Console.WriteLine($"  Time: {finalElapsed.ToString(@"mm\:ss")}");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine();
        Console.WriteLine("✗ Extraction cancelled");
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.WriteLine($"✗ Extraction failed: {ex.Message}");
        logger.LogError(ex, "Extraction failed");
    }
    finally
    {
        await clangd.StopAsync(CancellationToken.None);
    }
}

// Simple options class for workspace configuration
public class WorkspaceOptions
{
    public string Root { get; set; } = string.Empty;
}

// Options for compile_commands.json path
public class CompileCommandsOptions
{
    public string? Path { get; set; }
}

// Progress reporting structure
public class ExtractionProgress
{
    public int FilesProcessed { get; set; }
    public int TotalFiles { get; set; }
    public int SymbolsExtracted { get; set; }
    public string? CurrentPhase { get; set; }
}
