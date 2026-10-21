using System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Hosting;

/// <summary>
/// Sends the proxy host's logs to Jellyfin's log with credentials masked, so a URL carrying a login token
/// can never reach the log file.
/// </summary>
public sealed class RedactingLoggerProvider : ILoggerProvider
{
    private readonly ILoggerFactory _target;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedactingLoggerProvider"/> class.
    /// </summary>
    /// <param name="target">Jellyfin's logger factory.</param>
    public RedactingLoggerProvider(ILoggerFactory target)
    {
        _target = target;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new RedactingLogger(_target.CreateLogger(categoryName));

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class RedactingLogger : ILogger
    {
        private readonly ILogger _inner;

        public RedactingLogger(ILogger inner)
        {
            _inner = inner;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!_inner.IsEnabled(logLevel))
            {
                return;
            }

            // The exception is flattened into the masked text rather than passed on, because exception
            // messages can quote request URLs too.
            var message = CredentialRedactor.Redact(formatter(state, exception));
            if (exception is not null)
            {
                message += Environment.NewLine + CredentialRedactor.Redact(exception.ToString());
            }

            _inner.Log(logLevel, eventId, message, null, static (text, _) => text);
        }
    }
}
