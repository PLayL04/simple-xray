using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SimpleVpn.Desktop.Platform;

/// <summary>
/// Minimal file logger: logs\simplevpn.log, the previous run is kept as simplevpn.1.log.
/// Never log profile secrets (user id, keys).
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();

    public FileLoggerProvider(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "simplevpn.log");
        var mode = FileMode.Create;
        try
        {
            if (File.Exists(path))
            {
                File.Move(path, Path.Combine(directory, "simplevpn.1.log"), overwrite: true);
            }
        }
        catch (IOException)
        {
            // The running app holds the log (we are the XAML previewer): append instead of wiping it.
            mode = FileMode.Append;
        }

        _writer = new StreamWriter(new FileStream(path, mode, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortName(categoryName));

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Dispose();
        }
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {LevelName(level)} [{category}] {message}");
        lock (_lock)
        {
            _writer.WriteLine(line);
            if (exception is not null)
            {
                _writer.WriteLine(exception.ToString());
            }
        }
    }

    private static string ShortName(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---",
    };

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}
