using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Inkpact.Api.Tests.Testing;

/// <summary>
/// Keeps every log entry of every category, with its level: to check that something is not logged as an error when it is
/// not one. <see cref="CapturingLogger{T}"/> is the same for one logger handed to one class.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(string Category, LogLevel Level, string Message)> Entries => [.. _entries];

    /// <summary>The entries at warning or above, one line each, for an assertion that names them when it fails.</summary>
    public IReadOnlyList<string> WarningsAndErrors =>
        [.. Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => $"{e.Level} {e.Category}: {e.Message}")];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<(string, LogLevel, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((category, logLevel, formatter(state, exception)));
    }
}
