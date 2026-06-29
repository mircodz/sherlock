using System;
using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Repl.Commands;

namespace Sherlock.CLI.Repl;

/// <summary>
/// The set of analysis commands available to the REPL and to <c>--exec</c>,
/// indexed by name and alias.
/// </summary>
public sealed class ReplCommandRegistry
{
    private readonly List<IReplCommand> _commands;
    private readonly Dictionary<string, IReplCommand> _byName;

    public ReplCommandRegistry(IEnumerable<IReplCommand> commands)
    {
        _commands = commands.ToList();
        _byName = new Dictionary<string, IReplCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (IReplCommand command in _commands)
        {
            _byName[command.Name] = command;
            foreach (string alias in command.Aliases)
                _byName[alias] = command;
        }
    }

    public IReadOnlyList<IReplCommand> Commands => _commands;

    public IReplCommand? Resolve(string name) =>
        _byName.GetValueOrDefault(name);

    /// <summary>The default command set Sherlock ships with.</summary>
    public static ReplCommandRegistry CreateDefault(ReplHistory history)
    {
        var commands = new List<IReplCommand>
        {
            new InfoReplCommand(),
            new DumpHeapReplCommand(),
            new ObjectsReplCommand(),
            new DumpObjReplCommand(),
            new StringsReplCommand(),
            new ThreadsReplCommand(),
            new GcRootReplCommand(),
            new ExceptionsReplCommand(),
            new SegmentsReplCommand(),
            new ModulesReplCommand(),
            new DominatorsReplCommand(),
            new RetainedReplCommand(),
            new AllocationsReplCommand(),
            new SnapshotsReplCommand(),
            new LoadReplCommand(),
            new ImportReplCommand(),
            new RmReplCommand(),
            new LabelReplCommand(),
            new RunReplCommand(),
            new CollectReplCommand(),
            new PsReplCommand(),
            new SnapshotReplCommand(),
            new LogsReplCommand(),
            new KillReplCommand(),
            new SourceReplCommand(),
            new HistoryReplCommand(history),
        };
        // `help` lists every command including itself, so hand it a live view.
        commands.Add(new HelpReplCommand(() => commands));
        return new ReplCommandRegistry(commands);
    }
}
