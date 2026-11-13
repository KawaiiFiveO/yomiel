using Xunit;

namespace Yomiel.Tests;

public class TestSuiteTests
{
    [Fact]
    public void CzechPassesEveryTest()
    {
        var s = new Session("czech.z5");
        Assert.Equal(ZStatus.Halted, s.RunToInput());
        Assert.Contains("Failed: 0,", s.Text);
        Assert.Contains("Didn't crash: hooray!", s.Text);
    }

    [Fact]
    public void PraxixPassesEveryTest()
    {
        var s = new Session("praxix.z5");
        s.RunToInput();
        string result = s.Send("all");
        Assert.Contains("All tests passed.", result);
    }

    [Fact]
    public void StrictzHandlesObjectZero()
    {
        var s = new Session("strictz.z5");
        s.RunToInput();
        string result = s.Send("n");
        for (int i = 0; i < 20 && s.Machine.Status != ZStatus.Halted && !result.Contains("Test completed!"); i++)
            result += s.Send("");
        Assert.Contains("Test completed!", result);
        Assert.DoesNotContain("incorrect", result);
    }
}

public class TangleTests
{
    private static Session Start()
    {
        var s = new Session("Tangle.z5");
        Assert.Equal(ZStatus.WaitingForLine, s.RunToInput());
        Assert.Contains("End of Alley", s.Text);
        return s;
    }

    [Fact]
    public void ParsesCommands()
    {
        Session s = Start();
        Assert.Contains("You are carrying nothing", s.Send("inventory"));
        Assert.Contains("Not through a brick wall", s.Send("w"));
        Assert.Contains("Mouth of Alley", s.Send("s"));
    }

    [Fact]
    public void ShowsTheStatusLine()
    {
        Session s = Start();
        Assert.Contains("End of Alley", s.UpperText);
    }

    [Fact]
    public void UndoesATurn()
    {
        Session s = Start();
        Assert.Contains("Mouth of Alley", s.Send("s"));
        string undone = s.Send("undo");
        Assert.Contains("[Previous turn undone.]", undone);
        Assert.Contains("End of Alley", undone);
    }

    [Fact]
    public void SavesAndRestores()
    {
        Session s = Start();
        s.Send("save");
        Assert.Single(s.Saves);
        s.Send("s");
        s.RestoreData = s.Saves[0];
        string restored = s.Send("restore");
        Assert.Contains("Ok.", restored);
        Assert.Contains("End of Alley", s.Send("look"));
    }

    [Fact]
    public void RestoreOfOtherDataFails()
    {
        Session s = Start();
        s.RestoreData = new byte[] { 1, 2, 3 };
        s.Send("restore");
        Assert.NotNull(s.Machine.RestoreError);
        Assert.Equal(ZStatus.WaitingForLine, s.Machine.Status);
    }

    [Fact]
    public void AGameSaveResumesInANewMachine()
    {
        Session s = Start();
        s.Send("s");
        s.Send("save");
        var resumed = new Session(ZMachine.Resume(Stories.Load("Tangle.z5"), s.Saves[0]));
        resumed.RunToInput();
        Assert.Contains("Ok.", resumed.Text);
        Assert.Contains("Mouth of Alley", resumed.Send("look"));
    }

    [Fact]
    public void SuspendedStateCarriesOn()
    {
        Session s = Start();
        s.Send("s");
        byte[] state = s.Machine.SaveState();

        var resumed = new Session(ZMachine.Resume(Stories.Load("Tangle.z5"), state, new ZMachineOptions { RandomSeed = 1, ScreenColumns = 100 }));
        Assert.Equal(ZStatus.WaitingForLine, resumed.RunToInput());
        Assert.Equal(s.Send("n"), resumed.Send("n"));
        Assert.Equal(s.Send("look"), resumed.Send("look"));
    }

    [Fact]
    public void QuitHalts()
    {
        Session s = Start();
        s.Send("quit");
        s.Send("y");
        Assert.Equal(ZStatus.Halted, s.Machine.Status);
    }
}

public class NamelessTests
{
    private static Session Start()
    {
        var s = new Session("nameless.z8");
        s.RunToInput();
        for (int i = 0; i < 20 && s.Machine.Status == ZStatus.WaitingForKey; i++)
            s.Send("");
        Assert.Equal(ZStatus.WaitingForLine, s.Machine.Status);
        return s;
    }

    [Fact]
    public void ReachesTheTavern()
    {
        Session s = Start();
        Assert.Contains("Nameless Tavern", s.Text);
        string inventory = s.Send("inventory");
        Assert.Contains("sixty gold", inventory);
    }

    [Fact]
    public void UsesColours()
    {
        var machine = new ZMachine(Stories.Load("nameless.z8"), new ZMachineOptions { RandomSeed = 1 });
        bool colour = false;
        machine.Run();
        while (machine.TryGetOutput(out ZOutput item))
            colour |= item.Kind == ZOutputKind.SetColor && !item.Foreground.IsDefault;
        Assert.True(colour);
    }

    [Fact]
    public void SavesAndRestores()
    {
        Session s = Start();
        s.Send("save");
        Assert.Single(s.Saves);
        s.Send("drop cloak");
        s.RestoreData = s.Saves[0];
        s.Send("restore");
        Assert.Contains("a brown cloak (being worn)", s.Send("inventory"));
    }
}

public class IZeroTests
{
    // I-0's parser can leave the dictionary word "smell" where an object belongs; Frotz stops with
    // "Illegal object". Yomiel treats it as no object and warns.
    [Fact]
    public void ANonObjectWarnsAndPlayContinues()
    {
        var machine = new ZMachine(Stories.Load("I-0.z5"), new ZMachineOptions { RandomSeed = 1 });
        var warnings = new System.Collections.Generic.List<string>();
        var s = new Session(machine);
        s.RunToInput();
        foreach (string command in new[] { "shout smell", "asshole texxxon", "look" })
        {
            machine.SubmitLine(command);
            for (ZStatus status = machine.Run(); ; status = machine.Run())
            {
                while (machine.TryGetOutput(out ZOutput item))
                    if (item.Kind == ZOutputKind.Warning)
                        warnings.Add(item.Text);
                if (status != ZStatus.Running)
                    break;
            }
            Assert.Equal(ZStatus.WaitingForLine, machine.Status);
        }
        Assert.Contains(warnings, w => w.StartsWith("object 24839 doesn't exist"));
    }
}

public class ShrapnelTests
{
    // Shrapnel paces its text with read_char timeouts whose routine returns true, ending the wait.
    [Fact]
    public void TimedKeyRoutinesEndTheWait()
    {
        var s = new Session("shrapnel.z5");
        ZMachine m = s.Machine;
        int interrupts = 0;
        for (int i = 0; i < 50 && !(m.Status == ZStatus.WaitingForLine && s.Text.Contains("West of the house")); i++)
        {
            ZStatus status = s.RunToInput();
            if (status == ZStatus.WaitingForKey && m.KeyRequest.TimeoutTenths > 0)
            {
                m.Interrupt();
                interrupts++;
            }
            else if (status == ZStatus.WaitingForKey)
            {
                m.SubmitKey(ZKey.Enter);
            }
            else if (status == ZStatus.WaitingForLine && !s.Text.Contains("West of the house"))
            {
                m.SubmitLine("");
            }
        }
        Assert.True(interrupts > 0);
        Assert.Equal(ZStatus.WaitingForLine, m.Status);
        Assert.Contains("West of the house", s.Text);
        Assert.Contains("open field", s.Send("look"));
    }
}
