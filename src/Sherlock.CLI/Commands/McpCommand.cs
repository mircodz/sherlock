using System.Threading;
using Sherlock.Mcp;
using Spectre.Console.Cli;

namespace Sherlock.CLI.Commands;

/// <summary>
/// Runs the MCP server over stdio, exposing the analysis tools to AI agents.
/// Registered with a client as <c>claude mcp add sherlock -- sl mcp</c>.
/// </summary>
public sealed class McpCommand : Command<McpCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        // stdout is the protocol transport - print nothing here. Blocks until the client disconnects.
        McpServer.RunAsync([]).GetAwaiter().GetResult();
        return 0;
    }
}
