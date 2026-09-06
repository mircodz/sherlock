using System.Threading;
using Sherlock.Mcp;
using Spectre.Console.Cli;

namespace Sherlock.CLI.Commands;

/// <summary>Runs the MCP analysis server over stdio.</summary>
public sealed class McpCommand : Command<McpCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        // Reserve stdout for the protocol.
        McpServer.RunAsync([]).GetAwaiter().GetResult();
        return 0;
    }
}
