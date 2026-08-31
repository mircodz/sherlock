using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Delegates with oversized invocation lists: suspected event-handler leaks where a long-lived event pins every subscriber that never unsubscribed (-=).</summary>
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
                $"[#AFFF00]No suspicious event subscriptions[/] [#808791](no delegate has ≥ {min} subscribers).[/]");
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
                $"[#FFD75F]0x{leak.DelegateAddress:x}[/]",
                $"[bold]{Counts.Compact(leak.SubscriberCount)}[/]",
                $"[#00D7FF]{Markup.Escape(TypeNames.Short(leak.DelegateType))}[/]",
                subscribers);
        }

        context.Console.Write(table);
        context.Console.MarkupLine(
            "[#808791]Each subscriber is pinned until it unsubscribes (-=).[/] gcroot <address> [#808791]to find the publisher that owns the event.[/]");
    }
}
