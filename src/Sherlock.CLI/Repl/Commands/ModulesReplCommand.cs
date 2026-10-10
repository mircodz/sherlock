using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Lists loaded managed modules/assemblies.</summary>
public sealed class ModulesReplCommand : IReplCommand
{
    public string Name => "modules";
    public IReadOnlyList<string> Aliases => ["lm"];
    public string Summary => "List loaded managed modules/assemblies.";
    public string Usage => "modules [name-filter]";
    public int MaxArgs => 1;

    public ReplResult Execute(ReplContext context, string[] args)
    {
        string? filter = args.Length > 0 ? args[0] : null;

        IReadOnlyList<ModuleInfo> modules = context.Snapshot.Modules;
        if (filter is not null)
        {
            modules = modules.Where(m => m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (modules.Count == 0)
        {
            context.Console.MarkupLine($"[{Palette.Warning}]No modules matched.[/]");
            return ReplResult.Success;
        }

        var table = Theme.Table(expand: true);
        table.AddColumn("[bold]Module[/]");
        table.AddColumn(new TableColumn("[bold]ImageBase[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Size[/]").RightAligned());

        foreach (ModuleInfo module in modules)
        {
            string name = Markup.Escape(Path.GetFileName(module.Name));
            table.AddRow(
                module.IsDynamic ? $"{name} [{Palette.Muted}](dynamic)[/]" : name,
                module.ImageBase == 0 ? "-" : $"[{Palette.Address}]0x{module.ImageBase:x}[/]",
                module.Size == 0 ? "-" : $"[bold {Palette.Text}]{ByteSize.Format((long)module.Size)}[/]");
        }

        context.Console.Write(table);
        context.Console.MarkupLine($"[{Palette.Muted}]{modules.Count} modules.[/]");
        return ReplResult.Success;
    }
}
