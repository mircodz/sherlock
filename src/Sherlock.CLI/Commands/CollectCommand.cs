using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Collection;
using Sherlock.Core.Store;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sherlock.CLI.Commands;

/// <summary>
/// Collects a memory dump from a live .NET process via the diagnostics IPC
/// channel, optionally opening it in the analyzer afterwards.
/// </summary>
public sealed class CollectCommand : Command<CollectCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--pid <PID>")]
        [Description("Process id to dump.")]
        public int? Pid { get; init; }

        [CommandOption("-n|--name <NAME>")]
        [Description("Dump the process whose name matches (must be unambiguous).")]
        public string? Name { get; init; }

        [CommandOption("-l|--list")]
        [Description("List dumpable .NET processes and exit.")]
        public bool List { get; init; }

        [CommandOption("-t|--type <TYPE>")]
        [Description("Dump type: mini | heap | triage | full. Default: heap.")]
        [DefaultValue("heap")]
        public string Type { get; init; } = "heap";

        [CommandOption("-o|--output <FILE>")]
        [Description("Output dump path. Default: a temp file.")]
        public string? Output { get; init; }

        [CommandOption("-a|--analyze")]
        [Description("Open the collected dump in the interactive analyzer.")]
        public bool Analyze { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        IAnsiConsole console = AnsiConsole.Console;

        if (settings.List)
        {
            return ListProcesses(console);
        }

        if (!TryResolvePid(console, settings, out int pid))
        {
            return 1;
        }

        if (!TryParseKind(settings.Type, out DumpKind kind))
        {
            console.MarkupLineInterpolated($"[red]error:[/] unknown dump type '{settings.Type}' (use mini|heap|triage|full).");
            return 1;
        }

        string? sourceName = NameOf(pid);

        string path;
        try
        {
            path = console.Status().Start($"Collecting {kind} dump from pid {pid}…",
                _ => DumpCollector.Collect(pid, kind, settings.Output));
        }
        catch (DumpAnalysisException ex)
        {
            console.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 1;
        }

        // Catalog it in the library. Own (move in) temp dumps; reference a user-chosen path.
        using Workspace workspace = ReplHost.CreateWorkspace();
        (Session session, SnapshotEntry entry) = workspace.Store.RegisterStandalone(
            SessionKind.Collect,
            sourcePath: path,
            moveIntoStore: settings.Output is null,
            sourceProcess: sourceName,
            sourcePid: pid);

        console.MarkupLineInterpolated($"[green]✓[/] saved [bold]{entry.Id}[/] [grey]({ByteSize.Format(entry.SizeBytes)})[/]");

        if (settings.Analyze)
        {
            console.WriteLine();
            workspace.Load(session, entry);
            ReplHost.RunInteractive(console, workspace);
            return 0;
        }

        console.MarkupLineInterpolated($"[grey]Open the library with[/] sl [grey]then[/] load {entry.Id}[grey].[/]");
        return 0;
    }

    private static string? NameOf(int pid)
    {
        try { return System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
        catch { return null; }
    }

    private static int ListProcesses(IAnsiConsole console)
    {
        IReadOnlyList<DotnetProcess> processes = ProcessLocator.List();
        if (processes.Count == 0)
        {
            console.MarkupLine("[yellow]No dumpable .NET processes found.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Square);
        table.AddColumn(new TableColumn("[bold]PID[/]").RightAligned());
        table.AddColumn("[bold]Process[/]");
        foreach (DotnetProcess process in processes)
            table.AddRow(process.Pid.ToString(), Markup.Escape(process.Name));

        console.Write(table);
        return 0;
    }

    private static bool TryResolvePid(IAnsiConsole console, Settings settings, out int pid)
    {
        pid = 0;

        if (settings.Pid is int explicitPid)
        {
            pid = explicitPid;
            return true;
        }

        if (settings.Name is null)
        {
            console.MarkupLine("[red]error:[/] specify [bold]--pid[/] or [bold]--name[/] (or [bold]--list[/] to see processes).");
            return false;
        }

        IReadOnlyList<DotnetProcess> matches = ProcessLocator.FindByName(settings.Name);
        switch (matches.Count)
        {
            case 0:
                console.MarkupLineInterpolated($"[red]error:[/] no .NET process matches '{settings.Name}'.");
                return false;
            case 1:
                pid = matches[0].Pid;
                console.MarkupLineInterpolated($"[grey]Matched[/] {matches[0].Name} [grey](pid {pid}).[/]");
                return true;
            default:
                console.MarkupLineInterpolated($"[red]error:[/] '{settings.Name}' is ambiguous ({matches.Count} matches). Use --pid:");
                foreach (DotnetProcess process in matches)
                    console.MarkupLineInterpolated($"  [grey]{process.Pid}[/]  {process.Name}");
                return false;
        }
    }

    private static bool TryParseKind(string text, out DumpKind kind)
    {
        switch (text.ToLowerInvariant())
        {
            case "mini": kind = DumpKind.Mini; return true;
            case "heap": kind = DumpKind.Heap; return true;
            case "triage": kind = DumpKind.Triage; return true;
            case "full": kind = DumpKind.Full; return true;
            default: kind = DumpKind.Heap; return false;
        }
    }
}
