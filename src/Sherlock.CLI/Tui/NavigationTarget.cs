using System;

namespace Sherlock.CLI.Tui;

internal enum ObjectTab { Inspect, Roots, Allocation }
internal enum TypeTab { Instances, Allocations }

internal abstract record NavigationTarget
{
    public static NavigationTarget FromLink(object payload) =>
        payload as NavigationTarget ?? throw new ArgumentException("Unknown explorer navigation target.", nameof(payload));
}

internal sealed record ObjTarget(ulong Address, ObjectTab Tab = ObjectTab.Inspect) : NavigationTarget;
internal sealed record TypeTarget(string Type, TypeTab Tab = TypeTab.Instances) : NavigationTarget;
internal sealed record MethodTarget(string Method) : NavigationTarget;
