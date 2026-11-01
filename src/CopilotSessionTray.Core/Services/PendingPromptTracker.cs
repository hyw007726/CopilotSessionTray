using System.Text.Json;
using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// Follows one session's event stream to tell whether it is blocked on the user. A permission
/// request is pending from its <c>permission.requested</c> event until the <c>permission.completed</c>
/// carrying the same request id; a question (<c>ask_user</c>) or a plan approval
/// (<c>exit_plan_mode</c>) from its tool call's start until that call completes. An abort, a
/// shutdown, a resume or a start drops whatever was pending, since the prompt is gone with it.
/// </summary>
internal sealed class PendingPromptTracker
{
    private readonly Dictionary<string, DateTimeOffset?> _permissions = new();
    private readonly Dictionary<string, DateTimeOffset?> _questions = new();

    public PendingPrompt Current
    {
        get
        {
            if (_permissions.Count == 0 && _questions.Count == 0)
            {
                return default;
            }

            var kind = _permissions.Count > 0 ? PendingPromptKind.Permission : PendingPromptKind.Question;
            return new PendingPrompt(kind, Oldest());
        }
    }

    public void Observe(SessionEventRecord record)
    {
        switch (record.RawType)
        {
            case "permission.requested":
                Add(_permissions, record, "requestId");
                break;
            case "permission.completed":
                Remove(_permissions, record, "requestId");
                break;
            case "tool.execution_start" when MentionsQuestionTool(record.RawJson):
                AddQuestion(record);
                break;
            case "tool.execution_complete" when _questions.Count > 0:
                Remove(_questions, record, "toolCallId");
                break;
            case "abort":
            case "session.shutdown":
            case "session.resume":
            case "session.start":
                _permissions.Clear();
                _questions.Clear();
                break;
        }
    }

    private static bool MentionsQuestionTool(string rawJson) =>
        rawJson.Contains("\"ask_user\"", StringComparison.Ordinal) ||
        rawJson.Contains("\"exit_plan_mode\"", StringComparison.Ordinal);

    private void AddQuestion(SessionEventRecord record)
    {
        using var document = Parse(record.RawJson);
        if (!TryGetData(document, out var data) ||
            ReadString(data, "toolName") is not ("ask_user" or "exit_plan_mode") ||
            ReadString(data, "toolCallId") is not { } callId)
        {
            return;
        }

        _questions[callId] = record.TimestampUtc;
    }

    private static void Add(Dictionary<string, DateTimeOffset?> pending, SessionEventRecord record, string idProperty)
    {
        using var document = Parse(record.RawJson);
        if (TryGetData(document, out var data) && ReadString(data, idProperty) is { } id)
        {
            pending[id] = record.TimestampUtc;
        }
    }

    private static void Remove(Dictionary<string, DateTimeOffset?> pending, SessionEventRecord record, string idProperty)
    {
        using var document = Parse(record.RawJson);
        if (TryGetData(document, out var data) && ReadString(data, idProperty) is { } id)
        {
            pending.Remove(id);
        }
    }

    private DateTimeOffset? Oldest()
    {
        DateTimeOffset? oldest = null;
        foreach (var since in _permissions.Values.Concat(_questions.Values))
        {
            if (since is { } value && (oldest is null || value < oldest))
            {
                oldest = value;
            }
        }

        return oldest;
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

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
