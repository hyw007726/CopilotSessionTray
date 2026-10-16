using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

public sealed class SessionSummaryProviderTests : IDisposable
{
    private const string SessionId = "0ec123d7-753e-4fef-977f-4eef290ff398";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"provider-tests-{Guid.NewGuid():N}");
    private readonly FakeHistoryStore _store = new();

    public SessionSummaryProviderTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SessionSummaryProvider CreateProvider() => new(_store, new SessionWorkspaceReader(_root));

    private void WriteWorkspace(string yaml, string sessionId = SessionId)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, sessionId));
        File.WriteAllText(Path.Combine(directory.FullName, "workspace.yaml"), yaml);
    }

    private static SessionSummary Db(string? summary) =>
        new(SessionId, @"C:\Git", null, null, summary, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static SessionCheckpoint Checkpoint(int number, string? title, string? overview = "overview") =>
        new(SessionId, number, title, overview, DateTimeOffset.UnixEpoch);

    private static SessionTurn Turn(int index, string? user, string? assistant) =>
        new(SessionId, index, user, assistant, DateTimeOffset.UnixEpoch);

    [Fact]
    public void ResolveTitle_PrefersTheUsersOwnName_OverEverythingElse()
    {
        var title = SessionSummaryProvider.ResolveTitle(
            Db("database title"), new SessionWorkspaceInfo("my own name", UserNamed: true), [Checkpoint(1, "checkpoint title")]);

        Assert.Equal("my own name", title);
    }

    [Fact]
    public void ResolveTitle_PrefersTheLatestNonEmptyCheckpointTitle_OverTheDatabaseTitle()
    {
        var title = SessionSummaryProvider.ResolveTitle(
            Db("database title"), new SessionWorkspaceInfo("generated name", UserNamed: false),
            [Checkpoint(1, "first topic"), Checkpoint(2, "latest topic"), Checkpoint(3, "  ")]);

        Assert.Equal("latest topic", title);
    }

    [Fact]
    public void ResolveTitle_UsesTheDatabaseTitle_WhenThereIsNoCheckpointTitle()
    {
        var title = SessionSummaryProvider.ResolveTitle(Db("database title"), new SessionWorkspaceInfo("generated name", false), []);

        Assert.Equal("database title", title);
    }

    [Fact]
    public void ResolveTitle_IgnoresAUserNamedFlag_WithoutAName()
    {
        var title = SessionSummaryProvider.ResolveTitle(Db("database title"), new SessionWorkspaceInfo(null, UserNamed: true), null);

        Assert.Equal("database title", title);
    }

    [Fact]
    public void ResolveTitle_FallsBackToTheGeneratedWorkspaceName_WhenTheDatabaseHasNoTitle()
    {
        Assert.Equal("generated name", SessionSummaryProvider.ResolveTitle(Db(null), new SessionWorkspaceInfo("generated name", false), null));
        Assert.Equal("generated name", SessionSummaryProvider.ResolveTitle(null, new SessionWorkspaceInfo("generated name", false), null));
    }

    [Fact]
    public void ResolveTitle_IgnoresAMultiLineGeneratedName_BecauseItIsAPromptNotATitle()
    {
        var title = SessionSummaryProvider.ResolveTitle(Db(null), new SessionWorkspaceInfo("You are a senior\nengineer. Review this.", false), null);

        Assert.Null(title);
    }

    [Fact]
    public void ResolveTitle_ReturnsNull_WhenNothingIsKnown()
    {
        Assert.Null(SessionSummaryProvider.ResolveTitle(null, null, null));
        Assert.Null(SessionSummaryProvider.ResolveTitle(Db("  "), new SessionWorkspaceInfo(null, false), []));
    }

    [Fact]
    public async Task GetAsync_ReturnsTheResolvedTitle_AndPutsItInTheResumeHeader()
    {
        WriteWorkspace("name: Renamed by me\nuser_named: true\n");
        _store.Checkpoints.Add(Checkpoint(1, "checkpoint title", "the overview"));

        var details = await CreateProvider().GetAsync(SessionId, Db("database title"));

        Assert.Equal("Renamed by me", details.Title);
        Assert.Contains("titled \"Renamed by me\"", details.ResumeText);
        Assert.EndsWith("the overview", details.ResumeText);
    }

    [Fact]
    public async Task GetAsync_UsesTheCheckpointTitle_WhenTheUserDidNotRenameTheSession()
    {
        WriteWorkspace("name: Remove Unread Concept\nuser_named: false\n");
        _store.Checkpoints.Add(Checkpoint(1, "Colorful rising notification balloons"));

        var details = await CreateProvider().GetAsync(SessionId, Db("Remove Unread Concept"));

        Assert.Equal("Colorful rising notification balloons", details.Title);
    }

    [Fact]
    public async Task GetAsync_WorksWithoutAWorkspaceFile()
    {
        var details = await CreateProvider().GetAsync(SessionId, Db("database title"));

        Assert.Equal("database title", details.Title);
        Assert.EndsWith("database title", details.ResumeText);
    }

    [Fact]
    public async Task GetAsync_FindsTheOriginOfASeededSession()
    {
        var seed = ResumeSummaryBuilder.Build("11111111-aaaa-bbbb-cccc-222222222222", "Earlier session", [Checkpoint(1, null, "Earlier work")], null);
        _store.Turns.Add(Turn(0, seed, "Understood."));

        var details = await CreateProvider().GetAsync(SessionId, Db("Resumed session"));

        Assert.Equal("11111111-aaaa-bbbb-cccc-222222222222", details.OriginSessionId);
    }

    [Fact]
    public async Task GetAsync_HasNoOrigin_ForAnOrdinarySession()
    {
        _store.Turns.Add(Turn(0, "Fix the build", "Done."));

        var details = await CreateProvider().GetAsync(SessionId, Db("Fix the build"));

        Assert.Null(details.OriginSessionId);
    }

    [Fact]
    public async Task GetAsync_FallsThroughToTheTurnRecap_WhenTheCheckpointReadFails()
    {
        _store.CheckpointError = new InvalidOperationException("locked");
        _store.Turns.Add(Turn(0, "ask", "reply"));

        var details = await CreateProvider().GetAsync(SessionId, Db("Title"));

        Assert.Contains("Most recent outcome: reply", details.ResumeText);
        Assert.Equal("Title", details.Title);
    }

    [Fact]
    public async Task GetAsync_FallsThroughToTheTitle_WhenEveryReadFails()
    {
        _store.CheckpointError = new InvalidOperationException("locked");
        _store.TurnError = new InvalidOperationException("locked");

        var details = await CreateProvider().GetAsync(SessionId, Db("Title"));

        Assert.EndsWith(Environment.NewLine + Environment.NewLine + "Title", details.ResumeText);
    }

    [Fact]
    public async Task GetAsync_DoesNotSwallowCancellation()
    {
        _store.CheckpointError = new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateProvider().GetAsync(SessionId, Db("Title")));
    }

    [Fact]
    public async Task GetAsync_ReturnsACheckpointView_ThatMatchesTheResumeText()
    {
        _store.Checkpoints.Add(new SessionCheckpoint(SessionId, 2, "Checkpoint title", "the overview", new DateTimeOffset(2026, 10, 9, 11, 0, 0, TimeSpan.Zero), "- next"));
        _store.Turns.Add(Turn(0, "ignored when a checkpoint exists", "ignored"));

        var details = await CreateProvider().GetAsync(SessionId, Db("database title"));

        Assert.Equal(SessionSummarySource.Checkpoint, details.View.Source);
        Assert.Equal(2, details.View.CheckpointNumber);
        Assert.Equal(new[] { "the overview", "- next" }, details.View.Sections.Select(section => section.Text).ToArray());
        Assert.Contains("the overview", details.ResumeText);
        Assert.Contains("Next steps: - next", details.ResumeText);
    }

    [Fact]
    public async Task GetAsync_ReturnsARecapView_WhenOnlyTurnsExist()
    {
        _store.Turns.Add(Turn(0, "the ask", "the reply"));

        var details = await CreateProvider().GetAsync(SessionId, Db("Title"));

        Assert.Equal(SessionSummarySource.Recap, details.View.Source);
        Assert.Equal(new[] { "the ask", "the reply" }, details.View.Sections.Select(section => section.Text).ToArray());
    }

    [Fact]
    public async Task GetAsync_ReturnsATitleOnlyView_WhenEveryReadFails()
    {
        _store.CheckpointError = new InvalidOperationException("locked");
        _store.TurnError = new InvalidOperationException("locked");

        var details = await CreateProvider().GetAsync(SessionId, Db("Title"));

        Assert.Equal(SessionSummarySource.TitleOnly, details.View.Source);
        Assert.Equal("Title", Assert.Single(details.View.Sections).Text);
    }

    [Fact]
    public async Task GetAsync_ReturnsAnEmptyView_WhenNothingIsKnown()
    {
        var details = await CreateProvider().GetAsync(SessionId, null);

        Assert.Equal(SessionSummarySource.None, details.View.Source);
        Assert.Empty(details.View.Sections);
    }

    [Fact]
    public async Task GetAsync_TakesTheFolderAndRepositoryFromTheDatabaseRow_WhenThereIsOne()
    {
        WriteWorkspace("cwd: C:\\Other\nrepository: other-org/other\nname: Generated\nuser_named: false\n");
        var row = new SessionSummary(SessionId, @"C:\Git", "sample-org/inventory-service", null, "title", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        var details = await CreateProvider().GetAsync(SessionId, row);

        Assert.Equal(@"C:\Git", details.Cwd);
        Assert.Equal("sample-org/inventory-service", details.Repository);
    }

    [Fact]
    public async Task GetAsync_FallsBackToTheWorkspaceFileForTheFolderAndRepository_WhenThereIsNoDatabaseRow()
    {
        WriteWorkspace("cwd: C:\\Work\\sample-repo\nrepository: sample-org/inventory-service\nuser_named: false\n");

        var details = await CreateProvider().GetAsync(SessionId, null);

        Assert.Null(details.Title);
        Assert.Equal(@"C:\Work\sample-repo", details.Cwd);
        Assert.Equal("sample-org/inventory-service", details.Repository);
    }

    [Fact]
    public async Task GetAsync_FallsBackToTheWorkspaceFile_ForBlankDatabaseValues()
    {
        WriteWorkspace("cwd: C:\\Work\\sample-repo\nuser_named: false\n");
        var row = new SessionSummary(SessionId, "", "  ", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        var details = await CreateProvider().GetAsync(SessionId, row);

        Assert.Equal(@"C:\Work\sample-repo", details.Cwd);
        Assert.Null(details.Repository);
    }

    [Fact]
    public async Task GetAsync_ReportsNoFolderOrRepository_WhenNothingIsKnown()
    {
        var details = await CreateProvider().GetAsync(SessionId, null);

        Assert.Null(details.Cwd);
        Assert.Null(details.Repository);
    }

    [Fact]
    public async Task GetTitleAsync_ReadsTheDatabaseRow_WhenNoneIsGiven()
    {
        _store.Session = Db("database title");

        Assert.Equal("database title", await CreateProvider().GetTitleAsync(SessionId));
    }

    [Fact]
    public async Task GetTitleAsync_DoesNotReadTurns()
    {
        _store.TurnError = new InvalidOperationException("must not be called");

        var title = await CreateProvider().GetTitleAsync(SessionId, Db("database title"));

        Assert.Equal("database title", title);
    }

    [Fact]
    public async Task GetTitleAsync_ToleratesEveryReadFailing()
    {
        _store.SessionError = new InvalidOperationException("locked");
        _store.CheckpointError = new InvalidOperationException("locked");
        WriteWorkspace("name: Generated name\nuser_named: false\n");

        Assert.Equal("Generated name", await CreateProvider().GetTitleAsync(SessionId));
    }

    private sealed class FakeHistoryStore : ISessionHistoryStore
    {
        public List<SessionCheckpoint> Checkpoints { get; } = new();

        public List<SessionTurn> Turns { get; } = new();

        public SessionSummary? Session { get; set; }

        public Exception? CheckpointError { get; set; }

        public Exception? TurnError { get; set; }

        public Exception? SessionError { get; set; }

        public Task<IReadOnlyList<SessionCheckpoint>> GetCheckpointsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            CheckpointError is null
                ? Task.FromResult<IReadOnlyList<SessionCheckpoint>>(Checkpoints)
                : Task.FromException<IReadOnlyList<SessionCheckpoint>>(CheckpointError);

        public Task<IReadOnlyList<SessionTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            TurnError is null
                ? Task.FromResult<IReadOnlyList<SessionTurn>>(Turns)
                : Task.FromException<IReadOnlyList<SessionTurn>>(TurnError);

        public Task<SessionSummary?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            SessionError is null ? Task.FromResult(Session) : Task.FromException<SessionSummary?>(SessionError);

        public Task<IReadOnlyList<SessionSummary>> GetRecentSessionsAsync(int maxCount, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
