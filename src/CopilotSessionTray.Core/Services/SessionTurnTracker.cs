using System.Text.Json;
using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// Follows one session's event stream across polls to remember what it last reported and when its
/// current piece of work began, so a "finished" transition can carry both.
/// </summary>
internal sealed class SessionTurnTracker
{
    private string? _taskSummary;
    private string? _lastMessage;
    private DateTimeOffset? _workStartedUtc;
    private DateTimeOffset? _lastEventUtc;

    public string? Outcome => _taskSummary ?? _lastMessage;

    public TimeSpan? WorkedFor =>
        _workStartedUtc is { } start && _lastEventUtc is { } end && end >= start ? end - start : null;

    public void Observe(IReadOnlyList<SessionEventRecord> events)
    {
        foreach (var record in events)
        {
            if (record.TimestampUtc is { } timestamp)
            {
                _lastEventUtc = timestamp;
            }

            switch (record.RawType)
            {
                case "user.message":
                    ObserveUserMessage(record);
                    break;
                case "assistant.message":
                    ObserveAssistantMessage(record);
                    break;
                case "session.task_complete":
                    ObserveTaskComplete(record);
                    break;
                case "abort":
                    _taskSummary = null;
                    _lastMessage = null;
                    break;
            }
        }
    }

    private void ObserveUserMessage(SessionEventRecord record)
    {
        using var document = Parse(record.RawJson);
        if (!TryGetData(document, out var data) || HasValue(data, "source"))
        {
            return;
        }

        _taskSummary = null;
        _lastMessage = null;
        _workStartedUtc = record.TimestampUtc ?? _workStartedUtc;
    }

    private void ObserveAssistantMessage(SessionEventRecord record)
    {
        using var document = Parse(record.RawJson);
        if (!TryGetData(document, out var data) || HasValue(data, "parentToolCallId"))
        {
            return;
        }

        if (GetString(data, "content") is { } content)
        {
            _lastMessage = content;
        }
    }

    private void ObserveTaskComplete(SessionEventRecord record)
    {
        using var document = Parse(record.RawJson);
        if (!TryGetData(document, out var data))
        {
            return;
        }

        if (GetString(data, "summary") is { } summary)
        {
            _taskSummary = summary;
        }
        else if (GetString(data, "outcome") == "blocked" && GetString(data, "reason") is { } reason)
        {
            _taskSummary = $"Blocked: {reason}";
        }
    }

    private static JsonDocument? Parse(string rawJson)
    {
        try
        {
            return JsonDocument.Parse(rawJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetData(JsonDocument? document, out JsonElement data)
    {
        data = default;
        return document is not null &&
            document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("data", out data) &&
            data.ValueKind == JsonValueKind.Object;
    }

    private static bool HasValue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind != JsonValueKind.Null &&
        !(value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()));

    private static string? GetString(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        return null;
    }
}
