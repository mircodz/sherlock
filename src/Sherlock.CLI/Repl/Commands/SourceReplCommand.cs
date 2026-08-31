using System.Collections.Generic;
using System.IO;
using Sherlock.CLI.Rendering;
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
            Output.Error(context.Console, $"Script not found: {path}");
            return;
        }

        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            context.Console.MarkupLineInterpolated($"[#808791]source>[/] {line}");
            if (!context.RunLine(line))
            {
                return; // `exit` in the script stops execution
            }
        }
    }
}
