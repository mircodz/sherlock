using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Reports delegates with oversized invocation lists.</summary>
public sealed class EventLeaksReplCommand : IReplCommand
{
    private const int DefaultMin = 16;

    public string Name => "eventleaks";
    public IReadOnlyList<string> Aliases => ["events"];
    public string Summary => "Delegates with large invocation lists (suspected event-handler leaks).";
    public string Usage => "eventleaks [min-subscribers]";
    public int MaxArgs => 1;

    public ReplResult Execute(ReplContext context, string[] args)
    {
        int min = Args.Count(args, 0, DefaultMin, Usage);

        IReadOnlyList<EventSubscription> leaks = context.Console.Status()
            .Start("Scanning delegates…", _ => context.Snapshot.EventHandlerLeaks(min, context.Cancellation));

        if (leaks.Count == 0)
        {
            context.Console.MarkupLineInterpolated(
                $"[{Palette.Hot}]No suspicious event subscriptions[/] [{Palette.Muted}](no delegate has ≥ {min} subscribers).[/]");
            return ReplResult.Success;
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
                $"[{Palette.Address}]0x{leak.DelegateAddress:x}[/]",
                $"[bold]{Counts.Compact(leak.SubscriberCount)}[/]",
                $"[{Palette.Name}]{Markup.Escape(TypeNames.Short(leak.DelegateType))}[/]",
                subscribers);
        }

        context.Console.Write(table);
        context.Console.MarkupLine(
            $"[{Palette.Muted}]Each subscriber is pinned until it unsubscribes (-=).[/] gcroot <address> [{Palette.Muted}]to find the publisher that owns the event.[/]");
        return ReplResult.Success;
    }
}
