using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UrToolClient.Services.Log;

public class LogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string LogLevel { get; set; }
    public string Category { get; set; } // 哪个类打印的日志
    public string Message { get; set; }

    // 根据级别自动返回背景/前景色，无需转换器
    public string LevelBackground => LogLevel switch
    {
        "Error" or "Critical" => "#FEE2E2",
        "Warning" => "#FEF9C3",
        "Information" => "#E1F5EE",
        _ => "#F4F4F5"
    };

    public string LevelForeground => LogLevel switch
    {
        "Error" or "Critical" => "#991B1B",
        "Warning" => "#854D0E",
        "Information" => "#065F46",
        _ => "#52525B"
    };
}


public class LogBroker
{
    public static LogBroker Instance { get; } = new LogBroker();

    private LogBroker() { }

    public event Action<LogEntry> LogReceived;

    public void PublishLog(LogEntry logEntry)
    {
        LogReceived?.Invoke(logEntry);
    }
}

public class WpfLogger : ILogger
{
    private readonly string _categoryName;

    public WpfLogger(string categoryName) => _categoryName = categoryName;

    public IDisposable BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        if (exception != null)
        {
            message += Environment.NewLine + exception.ToString();
        }

        // 构建日志模型并发送给 Broker
        LogBroker.Instance.PublishLog(new LogEntry
        {
            LogLevel = logLevel.ToString(),
            Category = _categoryName,
            Message = message
        });
    }
}

// 2. 自定义 Provider
public class WpfLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new WpfLogger(categoryName);
    public void Dispose() { }
}

// 3. 编写扩展方法方便后续链式调用
public static class WpfLoggerExtensions
{
    public static ILoggingBuilder AddWpfLogger(this ILoggingBuilder builder)
    {
        builder.AddProvider(new WpfLoggerProvider());
        return builder;
    }
}