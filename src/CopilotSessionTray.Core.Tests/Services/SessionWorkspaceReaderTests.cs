using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

public sealed class SessionWorkspaceReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"workspace-tests-{Guid.NewGuid():N}");

    public SessionWorkspaceReaderTests()
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

    private static SessionWorkspaceInfo ParseOk(string yaml) => SessionWorkspaceReader.Parse(yaml);

    [Fact]
    public void Parse_ReadsAPlainName_AndUserNamedFlag()
    {
        var info = ParseOk("id: abc\ncwd: C:\\Git\nname: Fix the tray icon\nuser_named: false\nsummary_count: 0\n");

        Assert.Equal("Fix the tray icon", info.Name);
        Assert.False(info.UserNamed);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData("maybe", false)]
    public void Parse_ReadsUserNamed(string value, bool expected)
    {
        var info = ParseOk($"name: Something\nuser_named: {value}\n");

        Assert.Equal(expected, info.UserNamed);
    }

    [Fact]
    public void Parse_ReportsNoName_WhenTheKeyIsAbsent()
    {
        var info = ParseOk("id: abc\nuser_named: false\n");

        Assert.Null(info.Name);
    }

    [Theory]
    [InlineData("name: ''")]
    [InlineData("name: \"\"")]
    [InlineData("name:")]
    [InlineData("name: null")]
    [InlineData("name: ~")]
    [InlineData("name:    ")]
    public void Parse_TreatsEmptyOrNullNamesAsNoName(string line)
    {
        var info = ParseOk(line + "\nuser_named: false\n");

        Assert.Null(info.Name);
    }

    [Fact]
    public void Parse_DecodesSingleQuotedNames()
    {
        var info = ParseOk("name: 'It''s: a title # with colon'\nuser_named: true\n");

        Assert.Equal("It's: a title # with colon", info.Name);
        Assert.True(info.UserNamed);
    }

    [Fact]
    public void Parse_DecodesDoubleQuotedNamesWithEscapes()
    {
        var info = ParseOk("name: \"Say \\\"hi\\\" in C:\\\\Git \\u00e9\\ttab\"\n");

        Assert.Equal("Say \"hi\" in C:\\Git \u00e9\ttab", info.Name);
    }

    [Fact]
    public void Parse_DecodesLineBreakEscapesInDoubleQuotedNames()
    {
        var info = ParseOk("name: \"first\\nsecond\"\n");

        Assert.Equal("first\nsecond", info.Name);
    }

    [Fact]
    public void Parse_DecodesAQuotedNameThatContinuesOnTheNextLine()
    {
        var info = ParseOk("name: 'first part\n  second part'\nuser_named: false\n");

        Assert.Equal("first part second part", info.Name);
        Assert.False(info.UserNamed);
    }

    [Fact]
    public void Parse_FoldsAPlainNameThatContinuesOnTheNextLine()
    {
        var info = ParseOk("name: first part\n  second part\nuser_named: false\n");

        Assert.Equal("first part second part", info.Name);
    }

    [Fact]
    public void Parse_IgnoresATrailingCommentOnAPlainName()
    {
        var info = ParseOk("name: A title # note to self\n");

        Assert.Equal("A title", info.Name);
    }

    [Theory]
    [InlineData("|")]
    [InlineData("|-")]
    [InlineData("|+")]
    [InlineData(">")]
    [InlineData(">-")]
    public void Parse_ReadsBlockScalarNames_AsMultiLineText(string indicator)
    {
        var info = ParseOk($"id: abc\nname: {indicator}\n  line one\n  line two\n\n  line three\nuser_named: false\nsummary_count: 0\n");

        Assert.Equal(new[] { "line one", "line two", "", "line three" }, info.Name!.Split('\n'));
        Assert.False(info.UserNamed);
    }

    [Fact]
    public void Parse_IgnoresNestedKeysThatMerelyLookLikeTopLevelOnes()
    {
        var info = ParseOk("owner:\n  name: nested\n  user_named: true\nname: real\nuser_named: false\n");

        Assert.Equal("real", info.Name);
        Assert.False(info.UserNamed);
    }

    [Fact]
    public void Parse_IgnoresNestedKeys_ThatComeAfterTheRealOnes()
    {
        var info = ParseOk("name: real\nuser_named: false\nowner:\n  name: nested\n  user_named: true\n");

        Assert.Equal("real", info.Name);
        Assert.False(info.UserNamed);
    }

    [Fact]
    public void Parse_ToleratesWindowsLineEndingsAndABom()
    {
        var info = ParseOk("\uFEFFid: abc\r\nname: Windows title\r\nuser_named: true\r\n");

        Assert.Equal("Windows title", info.Name);
        Assert.True(info.UserNamed);
    }

    [Fact]
    public void Parse_ReadsANameOnTheFirstLine_AfterABom()
    {
        var info = ParseOk("\uFEFFname: First line title\nuser_named: true\n");

        Assert.Equal("First line title", info.Name);
        Assert.True(info.UserNamed);
    }

    [Fact]
    public void Parse_KeepsMultiLineNamesFreeOfCarriageReturns()
    {
        var info = ParseOk("name: |\r\n  line one\r\n  line two\r\nuser_named: false\r\n");

        Assert.Equal(new[] { "line one", "line two" }, info.Name!.Split('\n'));
    }

    [Fact]
    public void Parse_ReturnsNothingUsable_ForGarbage()
    {
        var info = ParseOk("\0\0 not yaml at all { [ ]\n");

        Assert.Null(info.Name);
        Assert.False(info.UserNamed);
    }

    [Fact]
    public void Read_ReturnsNull_WhenTheSessionFolderOrFileIsMissing()
    {
        var reader = new SessionWorkspaceReader(_root);
        Directory.CreateDirectory(Path.Combine(_root, "empty-session"));

        Assert.Null(reader.Read("no-such-session"));
        Assert.Null(reader.Read("empty-session"));
    }

    [Fact]
    public void Read_ReturnsTheParsedFile()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "session-1"));
        File.WriteAllText(Path.Combine(directory.FullName, "workspace.yaml"), "name: My own name\nuser_named: true\n");
        var reader = new SessionWorkspaceReader(_root);

        var info = reader.Read("session-1");

        Assert.Equal(new SessionWorkspaceInfo("My own name", true), info);
    }

    [Fact]
    public void Read_StillWorks_WhileAnotherProcessHasTheFileOpenForWriting()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "session-1"));
        var path = Path.Combine(directory.FullName, "workspace.yaml");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var bytes = System.Text.Encoding.UTF8.GetBytes("name: Being written\nuser_named: false\n");
        writer.Write(bytes);
        writer.Flush();
        var reader = new SessionWorkspaceReader(_root);

        var info = reader.Read("session-1");

        Assert.Equal("Being written", info!.Name);
    }

    [Theory]
    [InlineData("..\\..\\outside")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("..\\sibling")]
    [InlineData("../sibling")]
    public void Read_ReturnsNull_ForIdsThatAreNotAPlainFolderName(string sessionId)
    {
        var stateRoot = Directory.CreateDirectory(Path.Combine(_root, "state")).FullName;
        File.WriteAllText(Path.Combine(_root, "workspace.yaml"), "name: Outside\nuser_named: true\n");
        var sibling = Directory.CreateDirectory(Path.Combine(_root, "sibling"));
        File.WriteAllText(Path.Combine(sibling.FullName, "workspace.yaml"), "name: Outside\nuser_named: true\n");

        Assert.Null(new SessionWorkspaceReader(stateRoot).Read(sessionId));
    }
}
