using System.Linq;
using System.Text;
using Xunit;

namespace Yomiel.Tests;

public class ScreenTests
{
    private static string Row(ZCell[] cells, int columns, int row) =>
        new string(cells.Skip(row * columns).Take(columns).Select(c => c.Char).ToArray()).TrimEnd();

    private static string[] Rows(ZScreen screen, int scrollBack = 0)
    {
        ZCell[] cells = screen.Render(scrollBack);
        return Enumerable.Range(0, screen.Rows).Select(r => Row(cells, screen.Columns, r)).ToArray();
    }

    private static void Pump(ZMachine machine, ZScreen screen)
    {
        machine.Run();
        while (machine.TryGetOutput(out ZOutput item))
            screen.Apply(item);
    }

    [Fact]
    public void WrapsAtSpaces()
    {
        var screen = new ZScreen(10, 4);
        screen.Write("one two three four");
        Assert.Equal(new[] { "one two", "three four", "", "" }, Rows(screen));
    }

    [Fact]
    public void BreaksWordsLongerThanALine()
    {
        var screen = new ZScreen(5, 3);
        screen.Write("abcdefgh");
        Assert.Equal(new[] { "abcde", "fgh", "" }, Rows(screen));
    }

    [Fact]
    public void ScrollsAndKeepsScrollback()
    {
        var screen = new ZScreen(10, 3);
        screen.Write("1\n2\n3\n4\n5");
        Assert.Equal(new[] { "3", "4", "5" }, Rows(screen));
        Assert.Equal(2, screen.ScrollbackAvailable);
        Assert.Equal(new[] { "1", "2", "3" }, Rows(screen, 2));
        Assert.Equal(4, screen.LowerLineCount);
    }

    [Fact]
    public void ClearingTheLowerWindowStartsAtTheTop()
    {
        var screen = new ZScreen(10, 4);
        screen.Write("a\nb\nc\nd\ne");
        screen.Apply(Item(ZOutputKind.EraseWindow, -1));
        screen.Write("top");
        Assert.Equal(new[] { "top", "", "", "" }, Rows(screen));
    }

    [Fact]
    public void ShowsInputWithTheCursor()
    {
        var screen = new ZScreen(10, 3);
        screen.Write(">");
        screen.Input = "look";
        screen.InputCursor = 2;
        Assert.Equal(">look", Rows(screen)[0]);
        Assert.Equal(3, screen.CursorColumn);
        Assert.Equal(0, screen.CursorRow);
    }

    [Fact]
    public void LongInputWrapsOntoTheNextLine()
    {
        var screen = new ZScreen(5, 3);
        screen.Write(">");
        screen.Input = "abcdefg";
        screen.InputCursor = 7;
        Assert.Equal(new[] { ">abcd", "efg", "" }, Rows(screen));
        Assert.Equal(1, screen.CursorRow);
        Assert.Equal(3, screen.CursorColumn);
    }

    [Fact]
    public void RevealsLinesAndCharacters()
    {
        var screen = new ZScreen(10, 3);
        screen.Write("one two three\nfour fi");
        screen.ShownLines = 1;
        screen.ShownColumns = 3;
        Assert.Equal(new[] { "one two", "thr", "" }, Rows(screen));
        Assert.Equal(5, screen.LineLength(1));
        Assert.Equal(7, screen.LineLength(2));
        Assert.Equal(5, screen.LineLength(2, wholeWords: true));
        screen.ShownLines = 3;
        screen.ShownColumns = 0;
        Assert.Equal(new[] { "one two", "three", "four fi" }, Rows(screen));
    }

    [Fact]
    public void LoadedContentsShowWhetherRevealedOrNot()
    {
        var screen = new ZScreen(10, 4);
        screen.Write("one\ntwo\nthree\n>");
        var copy = new ZScreen(10, 4);
        Assert.True(copy.LoadContents(screen.SaveContents()));
        Assert.Equal(new[] { "one", "two", "three", ">" }, Rows(copy));
        copy.ShownLines = copy.LowerLineCount + 1;
        Assert.Equal(new[] { "one", "two", "three", ">" }, Rows(copy));
        copy.ShownLines = long.MaxValue;
        Assert.Equal(new[] { "one", "two", "three", ">" }, Rows(copy));
    }

    [Fact]
    public void TakesBackPreloadedInput()
    {
        var screen = new ZScreen(20, 2);
        screen.Write("Preload> Given");
        Assert.True(screen.TakeBack("given"));
        Assert.Equal("Preload>", Rows(screen)[0]);
        Assert.False(screen.TakeBack("xyz"));
    }

    [Fact]
    public void DrawsTangleStatusLineInTheUpperWindow()
    {
        var machine = new ZMachine(Stories.Load("Tangle.z5"), new ZMachineOptions { RandomSeed = 1, ScreenColumns = 80, ScreenRows = 25 });
        var screen = new ZScreen(80, 25);
        Pump(machine, screen);
        string[] rows = Rows(screen);
        Assert.Equal(1, screen.UpperHeight);
        Assert.Contains("End of Alley", rows[0]);
        Assert.Equal(ZTextStyle.Reverse, screen.Render()[0].Style & ZTextStyle.Reverse);
        Assert.Contains(rows, r => r.Contains("oppressively"));
        Assert.All(rows, r => Assert.True(r.Length <= 80));
        Assert.Equal(">", rows[screen.CursorRow].Substring(0, 1));
    }

    [Fact]
    public void KeepsWordsWhole()
    {
        var machine = new ZMachine(Stories.Load("Tangle.z5"), new ZMachineOptions { RandomSeed = 1, ScreenColumns = 37, ScreenRows = 60 });
        var screen = new ZScreen(37, 60);
        Pump(machine, screen);
        var text = new StringBuilder();
        foreach (string row in Rows(screen).Skip(1))
            text.Append(row).Append(' ');
        Assert.Contains("walls rising oppressively high in three directions", text.ToString().Replace("  ", " "));
    }

    [Fact]
    public void ContentsSurviveSaving()
    {
        var machine = new ZMachine(Stories.Load("nameless.z8"), new ZMachineOptions { RandomSeed = 1, ScreenColumns = 100, ScreenRows = 30 });
        var screen = new ZScreen(100, 30);
        Pump(machine, screen);
        string[] before = Rows(screen);
        ZCell[] cellsBefore = (ZCell[])screen.Render().Clone();

        var copy = new ZScreen(100, 30);
        Assert.True(copy.LoadContents(screen.SaveContents()));
        Assert.Equal(before, Rows(copy));
        ZCell[] cellsAfter = copy.Render();
        for (int i = 0; i < cellsBefore.Length; i++)
            Assert.True(cellsBefore[i].Foreground == cellsAfter[i].Foreground && cellsBefore[i].Style == cellsAfter[i].Style, $"cell {i}");
        Assert.False(new ZScreen(99, 30).LoadContents(screen.SaveContents()));
    }

    private static ZOutput Item(ZOutputKind kind, int value) => new(kind, 0) { Value = value };
}
