using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using McpSidecar.Services.Database;
using McpSidecar.Services;

// Parse CLI args
var isExtractOnly = args.Contains("--extract") || args.Contains("-e");
var isForce = args.Contains("--force");
var workspaceRoot = args.SkipWhile(a => a != "--workspace" && a != "-w")
                        .Skip(1)
                        .FirstOrDefault()
                   ?? Environment.GetEnvironmentVariable("MCP_WORKSPACE_ROOT")
                   ?? Directory.GetCurrentDirectory();

// Normalize to absolute path
workspaceRoot = Path.GetFullPath(workspaceRoot);

// --extract mode: run extraction standalone with progress, then exit
if (isExtractOnly)
{
    await RunExtractionAsync(workspaceRoot, isForce);
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

// Register workspace root as a singleton
builder.Services.AddSingleton(new WorkspaceOptions { Root = workspaceRoot });

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
static async Task RunExtractionAsync(string workspaceRoot, bool isForce)
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
    
    Console.WriteLine($"Extracting: {workspaceRoot}");
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
        await clangd.StartAsync(workspaceRoot, cts.Token);
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

// Progress reporting structure
public class ExtractionProgress
{
    public int FilesProcessed { get; set; }
    public int TotalFiles { get; set; }
    public int SymbolsExtracted { get; set; }
    public string? CurrentPhase { get; set; }
}
