using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Yomiel.Tests;

/// <summary>
/// Finds story files in the folder named by YOMIEL_STORIES, or the nearest "stories" folder above
/// the test binaries. Tests skip when their story is missing.
/// </summary>
internal static class Stories
{
    public static byte[] Load(string name)
    {
        string dir = Environment.GetEnvironmentVariable("YOMIEL_STORIES");
        if (string.IsNullOrEmpty(dir))
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                if (Directory.Exists(Path.Combine(d.FullName, "stories")))
                {
                    dir = Path.Combine(d.FullName, "stories");
                    break;
                }
            }
        }
        string path = dir == null ? null : Path.Combine(dir, name);
        if (path == null || !File.Exists(path))
            Assert.Skip($"{name} not found; put it in a stories folder or set YOMIEL_STORIES");
        return File.ReadAllBytes(path);
    }
}

/// <summary>Runs a machine against scripted input, keeping saves in memory.</summary>
internal sealed class Session
{
    private readonly StringBuilder lower = new();
    private readonly StringBuilder upper = new();
    private readonly List<byte[]> saves = new();

    public Session(ZMachine machine) => Machine = machine;

    public Session(string story, int seed = 1)
        : this(new ZMachine(Stories.Load(story), new ZMachineOptions { RandomSeed = seed, ScreenColumns = 100 })) { }

    public ZMachine Machine { get; }

    /// <summary>All lower-window text and input echoes so far.</summary>
    public string Text => lower.ToString();

    public string UpperText => upper.ToString();

    public IReadOnlyList<byte[]> Saves => saves;

    /// <summary>Data given to the next restore; null fails it.</summary>
    public byte[] RestoreData { get; set; }

    /// <summary>Runs until the machine waits for input, quits or fails, answering saves and restores.</summary>
    public ZStatus RunToInput()
    {
        for (int budget = 0; budget < 10_000; budget++)
        {
            ZStatus status = Machine.Run();
            Drain();
            switch (status)
            {
                case ZStatus.Running:
                    continue;
                case ZStatus.WaitingForSave:
                    saves.Add(Machine.SaveRequest.Data);
                    Machine.CompleteSave(true);
                    continue;
                case ZStatus.WaitingForRestore:
                    Machine.CompleteRestore(RestoreData);
                    continue;
                case ZStatus.Error:
                    Assert.Fail(Machine.ErrorMessage + "\n\nOutput:\n" + Tail());
                    break;
            }
            return status;
        }
        throw new TimeoutException("the story didn't stop for input");
    }

    /// <summary>Answers the waiting line or key, then runs to the next input. Returns the new text.</summary>
    public string Send(string input)
    {
        int start = lower.Length;
        if (Machine.Status == ZStatus.WaitingForKey)
        {
            if (input.Length == 0)
                Machine.SubmitKey(ZKey.Enter);
            else
                Machine.SubmitKey(input[0]);
        }
        else
        {
            Assert.Equal(ZStatus.WaitingForLine, Machine.Status);
            Machine.SubmitLine(input);
        }
        RunToInput();
        return lower.ToString(start, lower.Length - start);
    }

    private void Drain()
    {
        while (Machine.TryGetOutput(out ZOutput item))
        {
            if (item.Kind == ZOutputKind.Text && item.Window == 1)
                upper.Append(item.Text);
            else if (item.Kind is ZOutputKind.Text or ZOutputKind.InputEcho)
                lower.Append(item.Text);
        }
    }

    private string Tail() => lower.Length > 2000 ? lower.ToString(lower.Length - 2000, 2000) : lower.ToString();
}
