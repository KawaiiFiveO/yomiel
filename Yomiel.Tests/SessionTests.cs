using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Yomiel.Tests;

public class SessionTests
{
    private const double Frame = 1 / 60.0;

    private static ZSession Start(int columns = 80, int rows = 25, ZSessionOptions options = null)
    {
        var machine = new ZMachine(Stories.Load("Tangle.z5"), new ZMachineOptions { RandomSeed = 1, ScreenColumns = columns, ScreenRows = rows });
        return new ZSession(machine, new ZScreen(columns, rows), options ?? Options(new MemoryStorage()));
    }

    // Tangle's opening is more than a screenful, so tests that don't test [MORE] turn it off.
    private static ZSessionOptions Options(IZStorage storage)
    {
        return new ZSessionOptions { Storage = storage, MorePrompts = false };
    }

    /// <summary>Updates until the session waits for the player, shows [MORE] or ends.</summary>
    private static void Settle(ZSession session)
    {
        for (int i = 0; i < 10_000; i++)
        {
            session.Update(Frame);
            if (session.AwaitingPlayer || session.ShowingMore || session.Ended)
                return;
        }
        throw new TimeoutException("the session didn't settle");
    }

    private static void Type(ZSession session, string text)
    {
        foreach (char c in text)
            session.KeyPressed(c == '\n' ? new ZKeyPress(ZPlayerKey.Enter) : ZKeyPress.Character(c));
        Settle(session);
    }

    private static void Press(ZSession session, ZPlayerKey key) => session.KeyPressed(new ZKeyPress(key));

    private static string Text(ZSession session)
    {
        ZCell[] cells = session.Screen.Render(session.Scroll);
        int columns = session.Screen.Columns;
        return string.Join("\n", Enumerable.Range(0, session.Screen.Rows)
            .Select(r => new string(cells.Skip(r * columns).Take(columns).Select(c => c.Char).ToArray()).TrimEnd()));
    }

    private static string LastRow(ZSession session) => Text(session).Split('\n').Last(r => r.Length > 0);

    [Fact]
    public void EditsAndSubmitsALine()
    {
        ZSession session = Start();
        Settle(session);
        Assert.Equal(ZStatus.WaitingForLine, session.Machine.Status);
        Type(session, "lok");
        Press(session, ZPlayerKey.Left);
        Type(session, "o");
        Assert.Equal(">look", LastRow(session));
        Assert.Equal(4, session.Screen.CursorColumn);
        Type(session, "\n");
        Assert.Equal(ZStatus.WaitingForLine, session.Machine.Status);
        Assert.Contains(">look\n", Text(session));
        Assert.Equal(2, Text(session).Split(new[] { "oppressively" }, StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void RecallsEarlierCommands()
    {
        ZSession session = Start();
        Settle(session);
        Type(session, "look\n");
        Type(session, "inventory\n");
        Press(session, ZPlayerKey.Up);
        Assert.Equal(">inventory", LastRow(session));
        Press(session, ZPlayerKey.Up);
        Assert.Equal(">look", LastRow(session));
        Press(session, ZPlayerKey.Down);
        Press(session, ZPlayerKey.Down);
        Assert.Equal(">", LastRow(session));
        Type(session, "xyzzy");
        Press(session, ZPlayerKey.Escape);
        Assert.Equal(">", LastRow(session));
    }

    [Fact]
    public void PausesAtMore()
    {
        ZSession session = Start(columns: 40, rows: 8, new ZSessionOptions());
        Settle(session);
        Assert.True(session.ShowingMore);
        Assert.False(session.AwaitingPlayer);
        Press(session, ZPlayerKey.Enter);
        Assert.False(session.ShowingMore);
        while (session.ShowingMore || !session.AwaitingPlayer)
        {
            if (session.ShowingMore)
                Press(session, ZPlayerKey.Enter);
            Settle(session);
        }
        Assert.Equal(ZStatus.WaitingForLine, session.Machine.Status);
    }

    [Fact]
    public void WithoutMorePromptsShowsEverything()
    {
        ZSession session = Start(columns: 40, rows: 8, Options(null));
        Settle(session);
        Assert.False(session.ShowingMore);
        Assert.True(session.AwaitingPlayer);
    }

    [Fact]
    public void KeysTypedWhileBusyAreKept()
    {
        ZSession session = Start();
        foreach (char c in "look\n")
            session.KeyPressed(c == '\n' ? new ZKeyPress(ZPlayerKey.Enter) : ZKeyPress.Character(c));
        Settle(session);
        Settle(session);
        Assert.Contains(">look\n", Text(session));
    }

    [Fact]
    public void ScrollsBack()
    {
        ZSession session = Start(columns: 80, rows: 10, Options(null));
        Settle(session);
        Type(session, "look\n");
        Press(session, ZPlayerKey.PageUp);
        Assert.True(session.Scroll > 0);
        Assert.False(session.AwaitingPlayer);
        Press(session, ZPlayerKey.PageDown);
        Assert.Equal(0, session.Scroll);
        Assert.True(session.AwaitingPlayer);
    }

    [Fact]
    public void SavesAndRestoresByName()
    {
        var storage = new MemoryStorage();
        ZSession session = Start(options: Options(storage));
        Settle(session);
        Type(session, "save\n");
        Assert.EndsWith("Save as [save]:", LastRow(session));
        Type(session, "first\n");
        Assert.Equal(new[] { "first" }, storage.SaveNames());
        Assert.Equal(ZStatus.WaitingForLine, session.Machine.Status);

        Type(session, "restore\n");
        Assert.Contains("Saved games: first", Text(session));
        Assert.EndsWith("Restore which [first]:", LastRow(session));
        Type(session, "\n");
        Assert.Equal(ZStatus.WaitingForLine, session.Machine.Status);
        Assert.Contains("Ok", Text(session));
    }

    [Fact]
    public void RestoringAMissingSaveSaysSo()
    {
        var storage = new MemoryStorage();
        ZSession session = Start(options: Options(storage));
        Settle(session);
        Type(session, "restore\n");
        Assert.Contains("[There are no saved games.]", Text(session));
        Assert.Equal(ZStatus.WaitingForLine, session.Machine.Status);
    }

    [Fact]
    public void SuspendsAndContinues()
    {
        var storage = new MemoryStorage();
        ZSession session = Start(options: Options(storage));
        Settle(session);
        Type(session, "look\n");
        Type(session, "inv");
        string before = Text(session).Replace(">inv", ">");
        session.Suspend();
        session.Close();
        ZSuspended suspended = storage.ReadSuspended();
        Assert.NotNull(suspended);

        var machine = ZMachine.Resume(Stories.Load("Tangle.z5"), suspended.State, new ZMachineOptions { ScreenColumns = 80, ScreenRows = 25 });
        var screen = new ZScreen(80, 25);
        Assert.True(screen.LoadContents(suspended.Screen));
        var resumed = new ZSession(machine, screen, Options(storage));
        Settle(resumed);
        Assert.Equal(before, Text(resumed));
        Type(resumed, "look\n");
        Assert.Contains(">look\n", Text(resumed));
    }

    [Fact]
    public void ContinuesAtAnotherSize()
    {
        var storage = new MemoryStorage();
        ZSession session = Start(options: Options(storage));
        Settle(session);
        Type(session, "look\n");
        session.Suspend();
        ZSuspended suspended = storage.ReadSuspended();

        var machine = ZMachine.Resume(Stories.Load("Tangle.z5"), suspended.State, new ZMachineOptions { ScreenColumns = 50, ScreenRows = 20 });
        ZScreen screen = ZScreen.FromContents(suspended.Screen);
        var resumed = new ZSession(machine, screen, Options(storage));
        resumed.Resize(50, 20);
        Assert.Equal(50, resumed.Screen.Columns);
        Settle(resumed);
        Assert.All(Text(resumed).Split('\n'), row => Assert.True(row.Length <= 50));
        Assert.Contains("high in three directions", Text(resumed).Replace("\n", " "));
        Type(resumed, "inventory\n");
        Assert.Contains("carrying nothing", Text(resumed).Replace("\n", " "));
    }

    [Fact]
    public void EndsAndIsDismissed()
    {
        var storage = new MemoryStorage();
        storage.WriteSuspended(new ZSuspended(new byte[] { 1 }, null));
        ZSession session = Start(options: new ZSessionOptions { Storage = storage, MorePrompts = false, EndText = "[Done.]" });
        Settle(session);
        Type(session, "quit\n");
        Type(session, "y\n");
        Assert.True(session.Ended);
        Assert.Equal("[Done.]", LastRow(session));
        Assert.Null(storage.ReadSuspended());
        Assert.False(session.Dismissed);
        Press(session, ZPlayerKey.Enter);
        Assert.True(session.Dismissed);
    }

    [Fact]
    public void PacerHoldsOutputAndInput()
    {
        var pacer = new GatePacer();
        ZSession session = Start(options: new ZSessionOptions { Pacer = pacer, MorePrompts = false });
        for (int i = 0; i < 100; i++)
            session.Update(Frame);
        Assert.Equal(ZStatus.WaitingForLine, session.Machine.Status);
        Assert.True(session.OutputPending);
        Assert.DoesNotContain("oppressively", Text(session));

        pacer.Open = true;
        pacer.Hold = true;
        session.Update(Frame);
        Assert.Contains("oppressively", Text(session));
        Assert.False(session.AwaitingPlayer);
        foreach (char c in "look")
            session.KeyPressed(ZKeyPress.Character(c));
        session.KeyPressed(new ZKeyPress(ZPlayerKey.Enter));
        for (int i = 0; i < 10; i++)
            session.Update(Frame);
        Assert.DoesNotContain(">look", Text(session));

        pacer.Hold = false;
        Settle(session);
        Settle(session);
        Assert.Contains(">look\n", Text(session));
    }

    [Fact]
    public void StoryFolderKeepsSavesAndTheSuspendedStory()
    {
        // Rewritten each run, under the test binaries.
        string folder = Path.Combine(AppContext.BaseDirectory, "story-folder-test", Guid.NewGuid().ToString("N").Substring(0, 8));
        var storage = new ZStoryFolder(folder);
        Assert.Empty(storage.SaveNames());
        Assert.False(storage.HasSuspended);
        Assert.Null(storage.ReadSave("x"));

        storage.WriteSave("one", new byte[] { 1 });
        storage.WriteSuspended(new ZSuspended(new byte[] { 2 }, new byte[] { 3 }));
        storage.WriteData("table", new byte[] { 4 });
        Assert.Equal(new[] { "one" }, storage.SaveNames());
        Assert.Equal(new byte[] { 1 }, storage.ReadSave("one"));
        Assert.Equal(new byte[] { 4 }, storage.ReadData("table"));
        Assert.True(storage.HasSuspended);
        Assert.Equal(new byte[] { 3 }, storage.ReadSuspended().Screen);

        storage.DeleteSuspended();
        Assert.Null(storage.ReadSuspended());
        Assert.Equal("ab-c_d.e x", ZStoryFolder.SafeName("a/b:-c_d.e? x"));
        Assert.Equal("save", ZStoryFolder.SafeName("..."));
    }

    private sealed class GatePacer : ZPacer
    {
        public bool Open;
        public bool Hold;
        public override bool MayReveal(ZSession session, ZOutput item) => Open;
        public override bool HoldInput(ZSession session) => Hold;
    }

    private sealed class MemoryStorage : IZStorage
    {
        private readonly List<(string Name, byte[] Data)> saves = new();
        private readonly Dictionary<string, byte[]> data = new();
        private ZSuspended suspended;

        public IList<string> SaveNames() => saves.Select(s => s.Name).Reverse().ToList();
        public byte[] ReadSave(string name) => saves.LastOrDefault(s => s.Name == name).Data;

        public void WriteSave(string name, byte[] bytes)
        {
            saves.RemoveAll(s => s.Name == name);
            saves.Add((name, bytes));
        }

        public byte[] ReadData(string name) => data.TryGetValue(name, out byte[] bytes) ? bytes : null;
        public void WriteData(string name, byte[] bytes) => data[name] = bytes;
        public TextWriter OpenTranscript() => new StringWriter();
        public TextWriter OpenCommandRecord() => new StringWriter();
        public IList<string> ReadCommands() => Array.Empty<string>();
        public ZSuspended ReadSuspended() => suspended;
        public void WriteSuspended(ZSuspended s) => suspended = s;
        public void DeleteSuspended() => suspended = null;
    }
}
