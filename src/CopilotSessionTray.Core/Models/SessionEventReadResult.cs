namespace CopilotSessionTray.Core.Models;

/// <summary>
/// Result of reading new lines from a session's <c>events.jsonl</c> file via
/// <see cref="Contracts.ISessionEventStreamReader"/>. Callers persist
/// <see cref="NextByteOffset"/> and pass it back in as <c>fromByteOffset</c>
/// on the next call to resume tailing without re-reading previously seen
/// lines.
/// </summary>
/// <param name="Events">Newly observed events, in file order.</param>
/// <param name="NextByteOffset">Byte offset to resume reading from on the next call.</param>
public sealed record SessionEventReadResult(
    IReadOnlyList<SessionEventRecord> Events,
    long NextByteOffset);
