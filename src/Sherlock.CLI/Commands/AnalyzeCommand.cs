using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using Sherlock.CLI.Repl;
using Sherlock.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sherlock.CLI.Commands;

/// <summary>
/// Opens a dump and either starts the interactive REPL or, when one or more
/// <c>--exec</c> commands are given, runs them non-interactively and exits.
/// </summary>
public sealed class AnalyzeCommand : Command<AnalyzeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[dump]")]
        [Description("Optional dump file to open. Omit to start in the snapshot library.")]
        public string? DumpPath { get; init; }

        [CommandOption("-x|--exec <COMMAND>")]
        [Description("Run a command non-interactively, then exit. Repeatable.")]
        public string[] Exec { get; init; } = Array.Empty<string>();

        [CommandOption("-s|--script <FILE>")]
        [Description("Run commands from a script file, then exit.")]
        public string? Script { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        IAnsiConsole console = AnsiConsole.Console;

        using Workspace workspace = ReplHost.CreateWorkspace();

        // A dump path is optional; when given, open it as the current target.
        if (!string.IsNullOrEmpty(settings.DumpPath))
        {
            try
            {
                workspace.LoadTransient(settings.DumpPath);
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
        }

        bool interactive = settings.Exec.Length == 0 && settings.Script is null;
        var history = new ReplHistory(interactive ? ReplHistory.DefaultPath : null);
        var repl = new Repl.Repl(ReplCommandRegistry.CreateDefault(history), history, console);

        if (settings.Script is not null)
        {
            if (!File.Exists(settings.Script))
            {
                console.MarkupLineInterpolated($"[red]error:[/] script not found: {settings.Script}");
                return 1;
            }
            repl.RunBatch(workspace, File.ReadLines(settings.Script).Where(IsCommandLine));
        }
        else if (settings.Exec.Length > 0)
        {
            repl.RunBatch(workspace, settings.Exec);
        }
        else
        {
            repl.RunInteractive(workspace);
        }

        return 0;
    }

    /// <summary>Skips blank lines and <c>#</c> comments when reading a script file.</summary>
    private static bool IsCommandLine(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length > 0 && !trimmed.StartsWith('#');
    }
}
