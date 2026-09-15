using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Contracts;

/// <summary>
/// Tails a single session's append-only <c>events.jsonl</c> file under
/// <c>%USERPROFILE%\.copilot\session-state\&lt;sessionId&gt;\</c>.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are strictly read-only and must never modify the
/// underlying file. This is a stateless reader: all positioning state (the
/// byte offset) is passed in and returned by the caller, so it can be
/// persisted (e.g. via <see cref="IAppStateStore"/>) and survive app
/// restarts.
/// </para>
/// <para>
/// Lines that fail to parse as JSON, or whose <c>type</c> field is not
/// recognized, must not throw — they should be surfaced as a
/// <see cref="SessionEventRecord"/> with <see cref="SessionEventKind.Unknown"/>
/// (or skipped entirely, for lines that are not valid JSON at all) so that
/// new/changed event shapes in future Copilot CLI versions degrade
/// gracefully instead of crashing the detection loop.
/// </para>
/// </remarks>
public interface ISessionEventStreamReader
{
    /// <summary>
    /// Reads any events appended to the session's <c>events.jsonl</c> since
    /// <paramref name="fromByteOffset"/>.
    /// </summary>
    /// <param name="sessionId">The session whose event stream to read.</param>
    /// <param name="fromByteOffset">
    /// Byte offset to resume from (0 to read from the start of the file).
    /// Use <see cref="SessionEventReadResult.NextByteOffset"/> from the
    /// previous call.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the read.</param>
    /// <returns>
    /// The newly observed events (empty if none, or if the session's
    /// <c>events.jsonl</c> does not exist) and the offset to resume from
    /// next time.
    /// </returns>
    Task<SessionEventReadResult> ReadNewEventsAsync(
        string sessionId,
        long fromByteOffset,
        CancellationToken cancellationToken = default);
}
