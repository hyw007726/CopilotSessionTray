namespace CopilotSessionTray.Core.Models;

/// <summary>
/// Our own record of what the user has already seen (and done with) a
/// session, persisted by <see cref="Contracts.IAppStateStore"/> — Copilot
/// CLI has no native "read/unread" or "dismissed" concept, so this app owns
/// the bookkeeping entirely. This never deletes or otherwise affects the
/// underlying Copilot CLI session — <see cref="IsDismissed"/> only controls
/// whether this app's own tray list still shows it.
/// </summary>
/// <param name="SessionId">The session this marker applies to.</param>
/// <param name="LastAcknowledgedEventOffset">
/// The <c>events.jsonl</c> byte offset (see <see cref="SessionEventReadResult"/>)
/// up to which the user has acknowledged completion events.
/// </param>
/// <param name="LastAcknowledgedUtc">When the user last acknowledged this session.</param>
/// <param name="IsDismissed">
/// Whether the user has explicitly removed this session from the tray's
/// visible list (e.g. via a "remove" action). Independent of read/unread:
/// a session can be dismissed without ever having been marked read, and a
/// dismissed session that changes state again (e.g. re-opens) should
/// reappear rather than staying hidden forever — callers are expected to
/// clear this the next time <see cref="OpenSessionEntry.SessionId"/>
/// reappears with a new <see cref="OpenSessionEntry.OpenedAtUtc"/>.
/// </param>
public sealed record SessionReadMarker(
    string SessionId,
    long LastAcknowledgedEventOffset,
    DateTimeOffset LastAcknowledgedUtc,
    bool IsDismissed = false);
