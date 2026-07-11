using System.Collections.Generic;
using System.IO;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Runs a file of commands, one per line (lines starting with <c>#</c> are comments).</summary>
public sealed class SourceReplCommand : IReplCommand
{
    public string Name => "source";
    public IReadOnlyList<string> Aliases => ["@"];
    public string Summary => "Run commands from a script file, one per line.";
    public string Usage => "source <file>";
    public string Category => "Session";

    public void Execute(ReplContext context, string[] args)
    {
        Args.Require(args, 1, Usage);

        string path = args[0];
        if (!File.Exists(path))
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] script not found: {path}");
            return;
        }

        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            context.Console.MarkupLineInterpolated($"[grey]source>[/] {line}");
            if (!context.RunLine(line))
            {
                return; // an `exit` inside the script stops execution
            }
        }
    }
}
