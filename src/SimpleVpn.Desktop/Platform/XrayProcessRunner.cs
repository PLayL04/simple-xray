using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using SimpleVpn.Core.Abstractions;

namespace SimpleVpn.Desktop.Platform;

/// <summary>Runs core\xray.exe as a hidden child process bound to a kill-on-close job object.</summary>
internal sealed partial class XrayProcessRunner : ICoreRunner, IDisposable
{
    private const int RecentLineCount = 30;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly string _exePath;
    private readonly ILogger _logger;
    private readonly JobObject _job = new();
    private readonly Queue<string> _recent = new();
    private readonly Lock _lock = new();
    private Process? _process;

    public XrayProcessRunner(string exePath, ILoggerFactory loggerFactory)
    {
        _exePath = exePath;
        _logger = loggerFactory.CreateLogger("Xray");
    }

    public event EventHandler<CoreExitedEventArgs>? Exited;

    public bool IsRunning
    {
        get
        {
            var process = _process;
            return process is not null && !process.HasExited;
        }
    }

    public string RecentOutput
    {
        get
        {
            lock (_lock)
            {
                return string.Join(Environment.NewLine, _recent);
            }
        }
    }

    public async Task StartAsync(CoreStartInfo startInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!File.Exists(_exePath))
        {
            throw new FileNotFoundException("Xray core not found.", _exePath);
        }

        await StopAsync().ConfigureAwait(false);
        lock (_lock)
        {
            _recent.Clear();
        }

        var psi = new ProcessStartInfo(_exePath, ["run", "-c", startInfo.ConfigPath])
        {
            WorkingDirectory = startInfo.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["XRAY_LOCATION_ASSET"] = startInfo.AssetDirectory;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += OnOutput;
        process.ErrorDataReceived += OnOutput;
        process.Exited += OnProcessExited;

        process.Start();
        _process = process;
        try
        {
            _job.Assign(process);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Not fatal: StopAsync still kills it; only crash cleanup is affected.
            LogJobAssignFailed(_logger, ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        LogStarted(_logger, process.Id);
    }

    public async Task StopAsync()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null)
        {
            return;
        }

        process.Exited -= OnProcessExited;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(StopTimeout);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // Stopping must never throw.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStopFailed(_logger, ex);
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _job.Dispose();
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data))
        {
            return;
        }

        lock (_lock)
        {
            _recent.Enqueue(e.Data);
            while (_recent.Count > RecentLineCount)
            {
                _recent.Dequeue();
            }
        }

        LogLine(_logger, e.Data);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is not Process process || !ReferenceEquals(process, _process))
        {
            return;
        }

        // Let the async output readers flush the last lines (usually the error message).
        process.WaitForExit();
        LogExited(_logger, process.ExitCode);
        Exited?.Invoke(this, new CoreExitedEventArgs(process.ExitCode, RecentOutput));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "xray started, pid {Pid}")]
    private static partial void LogStarted(ILogger logger, int pid);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Line}")]
    private static partial void LogLine(ILogger logger, string line);

    [LoggerMessage(Level = LogLevel.Warning, Message = "xray exited with code {ExitCode}")]
    private static partial void LogExited(ILogger logger, int exitCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to assign xray to job object")]
    private static partial void LogJobAssignFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to stop xray")]
    private static partial void LogStopFailed(ILogger logger, Exception exception);
}
