using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Inkpact.Api.Tests.Testing;

/// <summary>A logger that keeps what it was told, to check that something was said at the right level.</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => [.. _entries];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue((logLevel, formatter(state, exception) + (exception is null ? "" : $" ({exception.GetType().Name}: {exception.Message})")));
}
