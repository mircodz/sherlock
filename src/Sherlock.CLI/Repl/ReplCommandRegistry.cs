using System;
using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Repl.Commands;

namespace Sherlock.CLI.Repl;

/// <summary>The analysis commands available to the REPL and <c>--exec</c>, indexed by name and alias.</summary>
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
            {
                _byName[alias] = command;
            }
        }
    }

    public IReadOnlyList<IReplCommand> Commands => _commands;

    public IReplCommand? Resolve(string name) =>
        _byName.GetValueOrDefault(name);

    public static ReplCommandRegistry CreateDefault(ReplHistory history)
    {
        var commands = new List<IReplCommand>
        {
            new InfoReplCommand(),
            new DumpHeapReplCommand(),
            new ObjectsReplCommand(),
            new PrintReplCommand(),
            new PrintExReplCommand(),
            new StringsReplCommand(),
            new ThreadsReplCommand(),
            new GcRootReplCommand(),
            new ExceptionsReplCommand(),
            new SegmentsReplCommand(),
            new ModulesReplCommand(),
            new DominatorsReplCommand(),
            new RetainedReplCommand(),
            new AllocationsReplCommand(),
            new WhoAllocReplCommand(),
            new ExportReplCommand(),
            new FinalizersReplCommand(),
            new EventLeaksReplCommand(),
            new InspectReplCommand(),
            new DiffReplCommand(),
            new ListReplCommand(),
            new LoadReplCommand(),
            new ImportReplCommand(),
            new RmReplCommand(),
            new LabelReplCommand(),
            new RunReplCommand(),
            new PsReplCommand(),
            new SnapshotOnReplCommand(),
            new WaitTriggerReplCommand(),
            new SleepReplCommand(),
            new SnapshotReplCommand(),
            new LogsReplCommand(),
            new KillReplCommand(),
            new SourceReplCommand(),
            new HistoryReplCommand(history),
        };
        // Include help in its own command list.
        commands.Add(new HelpReplCommand(() => commands));
        return new ReplCommandRegistry(commands);
    }
}
