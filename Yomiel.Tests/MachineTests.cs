using System;
using Xunit;

namespace Yomiel.Tests;

public class MachineTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public void RefusesUnsupportedVersions(int version)
    {
        var story = new byte[1024];
        story[0] = (byte)version;
        var e = Assert.Throws<ArgumentException>(() => new ZMachine(story));
        Assert.Contains($"version {version}", e.Message);
    }

    [Fact]
    public void ReportsTheScreenInTheHeader()
    {
        var machine = new ZMachine(Stories.Load("czech.z5"), new ZMachineOptions { ScreenColumns = 106, ScreenRows = 34 });
        var s = new Session(machine);
        s.RunToInput();
        Assert.Contains("Screen size: 106x34; in 1x1 units: 106x34", s.Text);
        Assert.Contains("interpreter 6 Y (IBM PC)", s.Text);
        Assert.Contains("standard 1.1", s.Text);
    }

    [Fact]
    public void StandardColoursConvertToRgb()
    {
        Assert.Equal(0x000000, ZColor.FromIndex(2).ToRgb24());
        Assert.Equal(0xFFFFFF, ZColor.FromIndex(9).ToRgb24());
        Assert.Equal(-1, ZColor.Default.ToRgb24());
        Assert.Equal(4, ZColor.FromRgb15(0x0340).Index);
        Assert.Equal(0, ZColor.FromRgb15(0x1234).Index);
    }

    [Fact]
    public void SubmittingWhenNotWaitingThrows()
    {
        var machine = new ZMachine(Stories.Load("czech.z5"));
        Assert.Throws<InvalidOperationException>(() => machine.SubmitLine("x"));
        Assert.Throws<InvalidOperationException>(() => machine.SubmitKey('x'));
        Assert.Throws<InvalidOperationException>(() => machine.CompleteSave(true));
    }

    [Fact]
    public void RunsInSmallSteps()
    {
        var machine = new ZMachine(Stories.Load("czech.z5"));
        int calls = 0;
        while (machine.Run(100) == ZStatus.Running)
            calls++;
        Assert.Equal(ZStatus.Halted, machine.Status);
        Assert.True(calls > 10);
    }
}
