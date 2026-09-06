using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using Sherlock.CLI.Rendering;
using Sherlock.CLI.Repl;
using Sherlock.CLI.Repl.Commands;
using Sherlock.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sherlock.CLI.Commands;

/// <summary>Opens a dump for interactive or scripted analysis.</summary>
public sealed class AnalyzeCommand : Command<AnalyzeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[dump]")]
        [Description("Optional dump file to open. Omit to start in the snapshot library.")]
        public string? DumpPath { get; init; }

        [CommandOption("-x|--exec <COMMAND>")]
        [Description("Run a command non-interactively, then exit. Repeatable.")]
        public string[] Exec { get; init; } = [];

        [CommandOption("-s|--script <FILE>")]
        [Description("Run commands from a script file, then exit.")]
        public string? Script { get; init; }

        [CommandOption("-i|--interactive")]
        [Description("After running --exec/--script, drop into the REPL instead of exiting (gdb-style).")]
        public bool Interactive { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        IAnsiConsole console = AnsiConsole.Console;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using Workspace workspace = ReplHost.CreateWorkspace();
            return Run(console, workspace, settings, cancellation);
        }
        catch (OperationCanceledException)
        {
            Output.Warning(console, $"Analysis cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            Output.Error(console, $"{ex.Message}");
            return 1;
        }
    }

    internal static int Run(IAnsiConsole console, Workspace workspace, Settings settings, CancellationToken cancellation = default)
    {
        if (cancellation.IsCancellationRequested)
        {
            return 1;
        }

        if (!string.IsNullOrEmpty(settings.DumpPath))
        {
            try
            {
                workspace.LoadTransient(settings.DumpPath);
            }
            catch (FileNotFoundException ex)
            {
                Output.Error(console, $"Dump file not found: {ex.FileName}");
                return 1;
            }
            catch (DumpAnalysisException ex)
            {
                Output.Error(console, $"{ex.Message}");
                return 1;
            }
        }

        bool batched = settings.Exec.Length > 0 || settings.Script is not null;
        bool interactive = !batched || settings.Interactive;
        var history = new ReplHistory(interactive ? ReplHistory.DefaultPath : null);
        var repl = new Repl.Repl(ReplCommandRegistry.CreateDefault(history), history, console);
        var result = ReplResult.Success;

        if (settings.Script is not null)
        {
            if (!File.Exists(settings.Script))
            {
                Output.Error(console, $"Script not found: {settings.Script}");
                return 1;
            }
            result = repl.RunBatch(workspace, SourceReplCommand.ReadCommands(settings.Script, cancellation), cancellation);
        }
        else if (settings.Exec.Length > 0)
        {
            result = repl.RunBatch(workspace, settings.Exec, cancellation);
        }

        if (interactive && (result & (ReplResult.Quit | ReplResult.Cancelled)) == 0)
        {
            result |= repl.RunInteractive(workspace, cancellation);
        }

        return (result & (ReplResult.Failure | ReplResult.Cancelled)) != 0 ? 1 : 0;
    }
}
