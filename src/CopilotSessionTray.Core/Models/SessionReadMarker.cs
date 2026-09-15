namespace CopilotSessionTray.Core.Models;

/// <summary>
/// Our own record of what the user has already seen for a session,
/// persisted by <see cref="Contracts.IAppStateStore"/> — Copilot CLI has no
/// native "read/unread" concept, so this app owns the bookkeeping entirely.
/// </summary>
/// <param name="SessionId">The session this marker applies to.</param>
/// <param name="LastAcknowledgedEventOffset">
/// The <c>events.jsonl</c> byte offset (see <see cref="SessionEventReadResult"/>)
/// up to which the user has acknowledged completion events.
/// </param>
/// <param name="LastAcknowledgedUtc">When the user last acknowledged this session.</param>
public sealed record SessionReadMarker(
    string SessionId,
    long LastAcknowledgedEventOffset,
    DateTimeOffset LastAcknowledgedUtc);
