using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace McpSidecar.Services;

/// <summary>
/// Manages the clangd subprocess and LSP communication.
/// </summary>
public class ClangdService : IDisposable
{
    private readonly ILogger<ClangdService> _logger;
    private Process? _clangdProcess;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private int _requestId = 0;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private string _workspaceRoot = "";
    private string? _compileCommandsPath;

    public bool IsRunning => _clangdProcess?.HasExited == false;
    public string WorkspaceRoot => _workspaceRoot;
    public string? CompileCommandsPath => _compileCommandsPath;

    public ClangdService(ILogger<ClangdService> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(string? workspaceRoot = null, CancellationToken cancellationToken = default)
    {
        _workspaceRoot = workspaceRoot ?? Directory.GetCurrentDirectory();
        
        // Find compile_commands.json
        _compileCommandsPath = FindCompileCommands(_workspaceRoot);
        if (_compileCommandsPath != null)
        {
            _logger.LogInformation("Found compile_commands.json at: {Path}", _compileCommandsPath);
        }
        else
        {
            _logger.LogWarning("No compile_commands.json found in {Root}, clangd may have limited functionality", _workspaceRoot);
        }

        // Build clangd arguments
        var args = "--background-index --header-insertion=never";
        if (_compileCommandsPath != null)
        {
            var compileCommandsDir = Path.GetDirectoryName(_compileCommandsPath);
            args += $" --compile-commands-dir={compileCommandsDir}";
        }

        _clangdProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "clangd",
                Arguments = args,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _workspaceRoot
            }
        };

        _clangdProcess.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _logger.LogWarning("clangd stderr: {Data}", e.Data);
        };

        _clangdProcess.Start();
        _clangdProcess.BeginErrorReadLine();

        _stdin = _clangdProcess.StandardInput;
        _stdout = _clangdProcess.StandardOutput;

        _logger.LogInformation("Started clangd (PID: {Pid}) with args: {Args}", _clangdProcess.Id, args);

        // Initialize LSP connection
        await InitializeAsync(cancellationToken);
    }

    private static string? FindCompileCommands(string root)
    {
        // Check common locations
        var candidates = new[]
        {
            Path.Combine(root, "compile_commands.json"),
            Path.Combine(root, "build", "compile_commands.json"),
            Path.Combine(root, "cmake-build-debug", "compile_commands.json"),
            Path.Combine(root, "cmake-build", "compile_commands.json"),
            Path.Combine(root, "out", "build", "compile_commands.json"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // Search one level deep for build directories
        foreach (var dir in Directory.GetDirectories(root))
        {
            var cc = Path.Combine(dir, "compile_commands.json");
            if (File.Exists(cc))
                return cc;
        }

        return null;
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var initParams = new
        {
            processId = Environment.ProcessId,
            rootUri = $"file://{_workspaceRoot}",
            capabilities = new
            {
                textDocument = new
                {
                    definition = new { linkSupport = true },
                    references = new { },
                    callHierarchy = new { dynamicRegistration = false }
                },
                workspace = new
                {
                    symbol = new { }
                }
            }
        };

        var response = await SendRequestAsync("initialize", initParams, cancellationToken);
        if (response == null)
        {
            throw new InvalidOperationException("Failed to initialize LSP connection - no response");
        }

        _logger.LogInformation("LSP initialized successfully");

        // Send initialized notification
        await SendNotificationAsync("initialized", new { }, cancellationToken);
    }

    public async Task<JsonElement?> SendRequestAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        var request = new
        {
            jsonrpc = "2.0",
            id = ++_requestId,
            method,
            @params = parameters
        };

        var json = JsonSerializer.Serialize(request);
        var content = $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}";

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _stdin!.WriteAsync(content);
            await _stdin.FlushAsync(cancellationToken);
            _logger.LogDebug("Sent LSP request: {Method} (id={Id})", method, _requestId);
        }
        finally
        {
            _writeLock.Release();
        }

        // Read response
        return await ReadMessageAsync(cancellationToken);
    }

    public async Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        var notification = new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
        };

        var json = JsonSerializer.Serialize(notification);
        var content = $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}";

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _stdin!.WriteAsync(content);
            await _stdin.FlushAsync(cancellationToken);
            _logger.LogDebug("Sent LSP notification: {Method}", method);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<JsonElement?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        await _readLock.WaitAsync(cancellationToken);
        try
        {
            var stdoutStream = _stdout?.BaseStream
                ?? throw new InvalidOperationException("clangd stdout stream is not available");

            while (true)
            {
                // Read headers
                int contentLength = 0;
                while (true)
                {
                    var line = await ReadHeaderLineAsync(stdoutStream, cancellationToken);
                    if (line == null)
                    {
                        return null;
                    }

                    if (line.Length == 0)
                    {
                        break;
                    }

                    const string contentLengthHeader = "Content-Length:";
                    if (line.StartsWith(contentLengthHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        var lengthValue = line[contentLengthHeader.Length..].Trim();
                        if (!int.TryParse(lengthValue, out contentLength) || contentLength < 0)
                        {
                            throw new InvalidDataException($"Invalid Content-Length header from clangd: '{line}'");
                        }
                    }
                }

                if (contentLength <= 0)
                {
                    return null;
                }

                // Read content as exact bytes (LSP Content-Length is in bytes, not chars)
                var payload = new byte[contentLength];
                if (!await ReadExactlyAsync(stdoutStream, payload, cancellationToken))
                {
                    return null;
                }

                var content = Encoding.UTF8.GetString(payload);

                var msg = JsonSerializer.Deserialize<JsonElement>(content);

                // Skip notifications (they don't have an id), only return responses
                if (msg.TryGetProperty("method", out _))
                {
                    _logger.LogDebug("Skipping notification: {Method}", msg.GetProperty("method").GetString());
                    continue;
                }

                _logger.LogDebug("Received LSP response: {Content}", content.Length > 200 ? content[..200] + "..." : content);
                return msg;
            }
        }
        finally
        {
            _readLock.Release();
        }
    }

    private static async Task<string?> ReadHeaderLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(64);

        while (true)
        {
            var singleByte = new byte[1];
            var read = await stream.ReadAsync(singleByte.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                if (bytes.Count == 0)
                {
                    return null;
                }

                break;
            }

            var b = singleByte[0];
            if (b == (byte)'\n')
            {
                break;
            }

            if (b != (byte)'\r')
            {
                bytes.Add(b);
            }
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken);
            if (read == 0)
            {
                return false;
            }

            totalRead += read;
        }

        return true;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_clangdProcess != null && !_clangdProcess.HasExited)
        {
            try
            {
                // Send shutdown request
                await SendRequestAsync("shutdown", null, cancellationToken);
                await SendNotificationAsync("exit", null, cancellationToken);

                // Give it a moment to exit gracefully
                await Task.Delay(500, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during graceful clangd shutdown");
            }

            if (!_clangdProcess.HasExited)
            {
                _logger.LogWarning("Killing clangd process");
                _clangdProcess.Kill();
            }

            _clangdProcess.Dispose();
            _clangdProcess = null;
        }
    }

    public void Dispose()
    {
        _stdin?.Dispose();
        _stdout?.Dispose();
        _writeLock.Dispose();
        _readLock.Dispose();
        _clangdProcess?.Dispose();
    }
}
