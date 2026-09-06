using System;
using Spectre.Console;

namespace Sherlock.CLI.Rendering;

public static class Output
{
    public static void Info(IAnsiConsole console, FormattableString message) => Status(console, "i", Theme.Section, message);
    public static void Success(IAnsiConsole console, FormattableString message) => Status(console, "+", Theme.Success, message);
    public static void Warning(IAnsiConsole console, FormattableString message) => Status(console, "!", Theme.Attention, message);
    public static void Error(IAnsiConsole console, FormattableString message) => Status(console, "x", Theme.Error, message);

    public static bool TriggeredCapture(IAnsiConsole console, TriggeredCaptureResult capture)
    {
        if (capture.Entry is not { } entry)
        {
            Error(console, $"[bold]{capture.Probe}[/] fired but capture failed: {capture.Error}");
            return false;
        }
        string contents = entry.HasAllocations ? "heap + allocations" : "heap only";
        Success(console, $"[bold]{capture.Probe}[/] fired · snapshot [bold]{entry.Id}[/] [#808791]({contents})[/]");
        if (capture.Error is null)
        {
            return true;
        }
        Warning(console, $"{capture.Error}");
        return false;
    }

    private static void Status(IAnsiConsole console, string marker, string color, FormattableString message)
    {
        console.Markup($"[bold {color}][[{marker}]][/]");
        console.Write(" ");
        console.Write(Markup.FromInterpolated(message));
        console.WriteLine();
    }
}
