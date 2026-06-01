using Sherlock.CLI.Repl;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI;

/// <summary>Shared entry points for opening a dump and driving the REPL.</summary>
internal static class ReplHost
{
    /// <summary>Opens a dump and runs the interactive REPL. Returns a process exit code.</summary>
    public static int OpenAndRun(IAnsiConsole console, string dumpPath)
    {
        DumpSession session;
        try
        {
            session = DumpSession.Open(dumpPath);
        }
        catch (FileNotFoundException ex)
        {
            console.MarkupLineInterpolated($"[red]error:[/] dump file not found: {ex.FileName}");
            return 1;
        }
        catch (DumpAnalysisException ex)
        {
            console.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 1;
        }

        using (session)
            RunInteractive(console, session);

        return 0;
    }

    /// <summary>Runs the interactive REPL against an already-open session.</summary>
    public static void RunInteractive(IAnsiConsole console, DumpSession session)
    {
        var history = new ReplHistory(ReplHistory.DefaultPath);
        var repl = new Repl.Repl(ReplCommandRegistry.CreateDefault(history), history, console);
        repl.RunInteractive(session);
    }
}
