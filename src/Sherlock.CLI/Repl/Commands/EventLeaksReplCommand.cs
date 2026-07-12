using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>
/// Finds delegates whose invocation list has grown large - suspected event-handler leaks, where a
/// long-lived event keeps every subscriber alive because they never unsubscribed (-=).
/// </summary>
public sealed class EventLeaksReplCommand : IReplCommand
{
    private const int DefaultMin = 16;

    public string Name => "eventleaks";
    public IReadOnlyList<string> Aliases => ["events"];
    public string Summary => "Delegates with large invocation lists (suspected event-handler leaks).";
    public string Usage => "eventleaks [min-subscribers]";

    public void Execute(ReplContext context, string[] args)
    {
        int min = Args.Limit(args, 0, DefaultMin);

        IReadOnlyList<EventSubscription> leaks = context.Console.Status()
            .Start("Scanning delegates…", _ => context.Snapshot.EventHandlerLeaks(min));

        if (leaks.Count == 0)
        {
            context.Console.MarkupLineInterpolated(
                $"[green]No suspicious event subscriptions[/] [grey](no delegate has ≥ {min} subscribers).[/]");
            return;
        }

        var table = Theme.Table(expand: true);
        table.AddColumn("[bold]Address[/]");
        table.AddColumn(new TableColumn("[bold]Subs[/]").RightAligned());
        table.AddColumn("[bold]Delegate[/]");
        table.AddColumn("[bold]Top subscribers[/]");

        foreach (EventSubscription leak in leaks)
        {
            string subscribers = string.Join(", ",
                leak.Targets.Take(3).Select(t => $"{Markup.Escape(TypeNames.Short(t.TypeName))} ×{t.Count}"));
            table.AddRow(
                $"[grey]0x{leak.DelegateAddress:x}[/]",
                $"[bold]{Counts.Compact(leak.SubscriberCount)}[/]",
                $"[aqua]{Markup.Escape(TypeNames.Short(leak.DelegateType))}[/]",
                subscribers);
        }

        context.Console.Write(table);
        context.Console.MarkupLine(
            "[grey]Each subscriber is pinned until it unsubscribes (-=).[/] gcroot <address> [grey]to find the publisher that owns the event.[/]");
    }
}
