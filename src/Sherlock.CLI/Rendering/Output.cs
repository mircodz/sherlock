using System;
using Spectre.Console;

namespace Sherlock.CLI.Rendering;

public static class Output
{
    public static void Info(IAnsiConsole console, FormattableString message) => Status(console, "i", Theme.Section, message);
    public static void Success(IAnsiConsole console, FormattableString message) => Status(console, "+", Theme.Success, message);
    public static void Warning(IAnsiConsole console, FormattableString message) => Status(console, "!", Theme.Attention, message);
    public static void Error(IAnsiConsole console, FormattableString message) => Status(console, "x", Theme.Error, message);

    private static void Status(IAnsiConsole console, string marker, string color, FormattableString message)
    {
        console.Markup($"[bold {color}][[{marker}]][/]");
        console.Write(" ");
        console.Write(Markup.FromInterpolated(message));
        console.WriteLine();
    }
}
