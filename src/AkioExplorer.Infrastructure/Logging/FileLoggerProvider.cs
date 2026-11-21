using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace AkioExplorer.Infrastructure.Logging;

/// <summary>Minimal non-blocking rolling file logger (one file per day, 7 days kept).</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _folder;
    private readonly LogLevel _minLevel;
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly Task _writer;

    public FileLoggerProvider(string folder, LogLevel minLevel = LogLevel.Information)
    {
        _folder = folder;
        _minLevel = minLevel;
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    private void Enqueue(string line) => _channel.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            Cleanup();
            var builder = new StringBuilder();
            while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                builder.Clear();
                while (_channel.Reader.TryRead(out var line)) builder.AppendLine(line);
                string file = Path.Combine(_folder, $"akio-{DateTime.Now:yyyyMMdd}.log");
                await File.AppendAllTextAsync(file, builder.ToString()).ConfigureAwait(false);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    private void Cleanup()
    {
        foreach (var file in Directory.EnumerateFiles(_folder, "akio-*.log"))
        {
            if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-7))
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(string category, FileLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner._minLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel.ToString()[..3].ToUpperInvariant()}] {category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            owner.Enqueue(line);
        }
    }
}
