using Xunit;

namespace Yomiel.Tests;

/// <summary>TerpEtude's input tests, which need a person at the keyboard, driven by script.</summary>
public class EtudeTests
{
    private static Session Menu(string option)
    {
        var s = new Session("etude.z5");
        s.RunToInput();
        s.Send(option);
        return s;
    }

    [Fact]
    public void TimedLineInputCallsTheRoutineAndKeepsTheLine()
    {
        Session s = Menu("11");
        s.Send("x"); // any key begins the test
        ZMachine m = s.Machine;
        Assert.Equal(ZStatus.WaitingForLine, m.Status);
        Assert.Equal(10, m.LineRequest.TimeoutTenths);

        for (int i = 1; i <= 3; i++)
        {
            m.Interrupt("ab");
            Assert.Equal(ZStatus.WaitingForLine, s.RunToInput());
            Assert.Equal(i == 3, m.InterruptPrinted);
        }
        Assert.Contains("[Every three seconds....]", s.Text);

        Assert.Contains("You just typed \"abc\".", s.Send("abc"));
        string end = s.Send(".");
        Assert.Contains("Test terminated.", end);
        Assert.Contains("with no arguments", end);
        Assert.Contains("behave according to spec", end);
    }

    [Fact]
    public void TimedKeyInputCallsTheRoutine()
    {
        Session s = Menu("10");
        s.Send("x");
        ZMachine m = s.Machine;
        Assert.Equal(ZStatus.WaitingForKey, m.Status);
        m.Interrupt();
        Assert.Equal(ZStatus.WaitingForKey, s.RunToInput());
        Assert.True(m.InterruptPrinted);
        Assert.EndsWith("* ", s.Text);
    }

    [Fact]
    public void PreloadedInputIsOfferedNotEchoed()
    {
        Session s = Menu("12");
        ZMachine m = s.Machine;
        Assert.Equal("Given", m.LineRequest.InitialText);
        string result = s.Send("Given more");
        Assert.StartsWith(" more\n", result);
        Assert.Contains("You just typed \"given more\".", result);
    }

    [Fact]
    public void MultipleUndoWorks()
    {
        Session s = Menu("13");
        for (int i = 0; i < 5 && s.Machine.Status == ZStatus.WaitingForKey; i++)
            s.Send("x");
        Assert.Contains("Undo succeeded (undid first move).", s.Text);
        Assert.Contains("Undo succeeded (undid second move).", s.Text);
        Assert.DoesNotContain("ERROR", s.Text);
        Assert.DoesNotContain("Undo failed", s.Text);
    }
}
