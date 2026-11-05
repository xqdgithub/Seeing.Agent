using System.Text;

namespace Seeing.Agent.Desktop.Services;

/// <summary>
/// 极简文件日志提供方：只追加到文件，<b>绝不写标准输出/错误</b>。
/// <para>
/// 桌面形态没有控制台窗口（<c>OutputType=WinExe</c>），默认的 Console 日志
/// 会随控制台一起消失。这里把日志落盘，保留排障能力。
/// </para>
/// <para>
/// 与 <c>Seeing.Agent.Tui.Logging.TuiFileLoggerProvider</c> 同构，但刻意不复用：
/// TUI 那个类在 TUI 程序集内，桌面宿主不应为了一个 60 行的旁路组件
/// 去引用整个 TUI 宿主。
/// </para>
/// </summary>
public sealed class DesktopFileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly LogLevel _minLevel;
    private readonly object _gate = new();

    /// <param name="logDirectory">日志目录；不存在时自动创建。</param>
    /// <param name="minLevel">最低记录级别。</param>
    public DesktopFileLoggerProvider(string logDirectory, LogLevel minLevel = LogLevel.Information)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        Directory.CreateDirectory(logDirectory);
        _filePath = Path.Combine(logDirectory, $"seeing-desktop-{DateTime.Now:yyyyMMdd}.log");
        _minLevel = minLevel;
    }

    /// <summary>当前日志文件路径（供启动日志提示用户）。</summary>
    public string FilePath => _filePath;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new DesktopFileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <summary>日志目录：与 TUI 共用 <c>~/.seeing/logs</c>，便于统一排查。</summary>
    public static string ResolveLogDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".seeing",
            "logs");

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
            // 文件日志属旁路能力，写失败不得影响应用运行。
        }
    }

    private sealed class DesktopFileLogger : ILogger
    {
        private readonly DesktopFileLoggerProvider _provider;
        private readonly string _category;

        public DesktopFileLogger(DesktopFileLoggerProvider provider, string category)
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
