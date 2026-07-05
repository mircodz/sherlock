using System;
using System.Collections.Generic;
using System.Linq;
using Sherlock.Core.Collection;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>
/// Arms an event-driven snapshot trigger on a live target: when the event fires, sl
/// captures a heap dump into the run's session. Events:
///   call:Ns.Type.Method   a method is entered (ReJIT; non-inlined methods only)
///   alloc:Ns.Type         an instance of the type is allocated
///   gc[:gen2]             after a (generation-N) garbage collection
///   throw[:Ns.Exception]  an exception (of that type) is thrown
/// A bare Ns.Type.Method is shorthand for call:.
/// </summary>
public sealed class SnapshotOnReplCommand : IReplCommand
{
    public string Name => "snapshot-on";
    public IReadOnlyList<string> Aliases => ["snapon"];
    public string Summary => "Capture a snapshot when an event fires (call/alloc/gc/throw) on a live target.";
    public string Category => "Live";
    public string Usage => "snapshot-on <call:Type.Method | alloc:Type | gc[:gen2] | throw[:Exception]>";

    public void Execute(ReplContext context, string[] args)
    {
        if (args.Length == 0)
        {
            context.Console.MarkupLineInterpolated($"[yellow]usage:[/] {Usage}");
            return;
        }

        string spec = args[0];

        // Arm on the most recent live run that has a control channel (trigger capability).
        ProcessSupervisor? target = context.Workspace.Targets
            .LastOrDefault(t => !t.RootExited && t.ProfilerFeatures.Contains("snapshot-triggers"));
        if (target is null)
        {
            context.Console.MarkupLine(
                "[yellow]No live target with trigger support.[/] Start one with " +
                "[bold]run --correlate -- <app>[/] (or [bold]--profile[/]/[bold]--snapshot-on[/]).");
            return;
        }

        (bool ok, string detail) = context.Console.Status()
            .Start($"Arming snapshot-on {spec}…", _ => target.ArmSnapshotTrigger(spec, TimeSpan.FromSeconds(10)));

        if (ok)
        {
            context.Console.MarkupLineInterpolated(
                $"[green]armed[/] snapshot-on [bold]{spec}[/] [grey]on[/] {target.RootName} [grey](pid {target.RootPid}). A snapshot lands when it fires.[/]");
        }
        else
        {
            context.Console.MarkupLineInterpolated($"[red]could not arm:[/] {detail}");
        }
    }
}
