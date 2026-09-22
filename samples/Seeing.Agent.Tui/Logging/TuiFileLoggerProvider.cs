using System.Text;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Tui.Logging;

/// <summary>
/// 极简文件日志提供方：所有日志只追加到工作区日志文件，
/// <b>绝不写标准输出/错误</b>，避免污染终端活动区。
/// </summary>
public sealed class TuiFileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly LogLevel _minLevel;
    private readonly object _gate = new();

    public TuiFileLoggerProvider(string logDirectory, LogLevel minLevel = LogLevel.Information)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        Directory.CreateDirectory(logDirectory);
        _filePath = Path.Combine(logDirectory, $"seeing-tui-{DateTime.Now:yyyyMMdd}.log");
        _minLevel = minLevel;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new TuiFileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    internal bool IsEnabled(LogLevel level)
        => level != LogLevel.None && level >= _minLevel;

    internal void Write(string category, LogLevel level, EventId eventId, string? message, Exception? exception)
    {
        var builder = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
            .Append(" [").Append(level).Append("] ")
            .Append(category)
            .Append(" (").Append(eventId.Id).Append(") ")
            .Append(message);

        if (exception is not null)
            builder.AppendLine().Append(exception);

        try
        {
            lock (_gate)
                File.AppendAllText(_filePath, builder.AppendLine().ToString());
        }
        catch
        {
            // 文件日志属旁路能力，写失败不得影响 TUI 运行。
        }
    }

    private sealed class TuiFileLogger : ILogger
    {
        private readonly TuiFileLoggerProvider _provider;
        private readonly string _category;

        public TuiFileLogger(TuiFileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || formatter is null)
                return;

            _provider.Write(_category, logLevel, eventId, formatter(state, exception), exception);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
