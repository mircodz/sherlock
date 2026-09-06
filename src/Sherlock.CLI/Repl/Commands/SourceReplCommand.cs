using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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

    public ReplResult Execute(ReplContext context, string[] args)
    {
        Args.Require(args, 1, Usage);

        string path = Path.GetFullPath(args[0]);
        if (!File.Exists(path))
        {
            Output.Error(context.Console, $"Script not found: {path}");
            return ReplResult.Failure;
        }

        if (!context.ActiveScripts.Add(path))
        {
            Output.Error(context.Console, $"Script is already running: {path}");
            return ReplResult.Failure;
        }
        var result = ReplResult.Success;
        try
        {
            foreach (string raw in ReadCommands(path, context.Cancellation))
            {
                string line = raw.Trim();
                context.Console.MarkupLineInterpolated($"[#808791]source>[/] {line}");
                result |= context.RunLine(line);
                if ((result & (ReplResult.Quit | ReplResult.Cancelled)) != 0)
                {
                    return result;
                }
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            return result | ReplResult.Cancelled;
        }
        finally
        {
            context.ActiveScripts.Remove(path);
        }
    }

    public static IEnumerable<string> ReadCommands(string path, CancellationToken cancellation = default)
    {
        foreach (string raw in File.ReadLines(path))
        {
            cancellation.ThrowIfCancellationRequested();
            string line = raw.Trim();
            if (line.Length > 0 && !line.StartsWith('#'))
            {
                yield return raw;
            }
        }
    }
}
