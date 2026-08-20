using System.Text.Json;

namespace Veyro.Desktop.Core.Logging;

public sealed class SanitizedFileLogger : IDisposable
{
    private readonly object sync = new();
    private readonly StreamWriter writer;

    public SanitizedFileLogger(string logDirectory, TimeProvider? timeProvider = null)
    {
        TimeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(logDirectory);
        var fileName = $"veyro-{TimeProvider.GetUtcNow():yyyyMMdd}.jsonl";
        writer = new StreamWriter(
            new FileStream(Path.Combine(logDirectory, fileName), FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    private TimeProvider TimeProvider { get; }

    public void Write(
        LogLevel level,
        string eventName,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        var safeProperties = properties?.ToDictionary(
            pair => LogSanitizer.EventName(pair.Key),
            pair => LogSanitizer.Property(pair.Key, pair.Value));

        var record = new
        {
            timestamp = TimeProvider.GetUtcNow(),
            level = level.ToString(),
            event_name = LogSanitizer.EventName(eventName),
            properties = safeProperties
        };

        lock (sync)
        {
            writer.WriteLine(JsonSerializer.Serialize(record));
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            writer.Dispose();
        }
    }
}
