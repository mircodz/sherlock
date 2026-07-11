using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sherlock.Core;
using Sherlock.Core.Store;

namespace Sherlock.Mcp;

public static class McpServer
{
    public static async Task RunAsync(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton(_ => new SnapshotLibrary(SnapshotStore.Default()));
        builder.Services.AddSingleton<OpenSnapshots>();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
    }
}
