using System.Text.Json;
using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// Real implementation of <see cref="IOpenSessionsRegistryReader"/> — reads
/// the live <c>%USERPROFILE%\.copilot\open-sessions-state.json</c> registry.
/// See IMPLEMENTATION_PLAN.md §2.3 for the observed JSON shape this is
/// based on (verified unchanged against a real, currently-populated file
/// while implementing this).
/// </summary>
public sealed class OpenSessionsRegistryReader : IOpenSessionsRegistryReader
{
    private static readonly string DefaultRegistryFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "open-sessions-state.json");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _registryFilePath;

    /// <summary>
    /// Creates a reader against the real <c>open-sessions-state.json</c>. An explicit
    /// <paramref name="registryFilePath"/> override exists solely so xUnit tests can point this at
    /// a temp fixture file instead of the real <c>.copilot</c> folder — every real call site uses
    /// the parameterless default.
    /// </summary>
    public OpenSessionsRegistryReader(string? registryFilePath = null)
    {
        _registryFilePath = registryFilePath ?? DefaultRegistryFilePath;
    }

    public async Task<IReadOnlyDictionary<string, OpenSessionEntry>> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_registryFilePath))
        {
            return new Dictionary<string, OpenSessionEntry>();
        }

        // open-sessions-state.json is rewritten frequently by live Copilot CLI processes, so a
        // read can occasionally land mid-write; retry briefly rather than surface a transient
        // failure to the caller, per this interface's documented contract.
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await using var stream = File.Open(_registryFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var raw = await JsonSerializer.DeserializeAsync<Dictionary<string, RawEntry>>(stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);

                return raw?.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new OpenSessionEntry(kvp.Key, kvp.Value.SchemaVersion, kvp.Value.OpenedAt, kvp.Value.Working, kvp.Value.RefreshedAt))
                    ?? new Dictionary<string, OpenSessionEntry>();
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                lastError = ex;
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException($"Failed to read '{_registryFilePath}' after multiple retries.", lastError);
    }

    /// <summary>Shape of each value in the JSON object, matched to the real on-disk field names via camelCase policy.</summary>
    private sealed record RawEntry(int SchemaVersion, DateTimeOffset OpenedAt, bool Working, DateTimeOffset RefreshedAt);
}
