using Sherlock.CLI.Commands;
using Spectre.Console.Cli;

var app = new CommandApp<AnalyzeCommand>();
app.Configure(config =>
{
    config.SetApplicationName("sherlock");
    config.AddCommand<AnalyzeCommand>("analyze")
        .WithDescription("Open a .NET memory dump and analyze it interactively.")
        .WithExample("analyze", "app.dmp")
        .WithExample("analyze", "app.dmp", "--exec", "info");
    config.AddCommand<CollectCommand>("collect")
        .WithDescription("Collect a memory dump from a live .NET process.")
        .WithExample("collect", "--list")
        .WithExample("collect", "--pid", "1234", "--analyze");
    config.AddCommand<RunCommand>("run")
        .WithDescription("Run a process to completion, capturing snapshots and exit-time artifacts.")
        .WithExample("run", "--", "./MyApp.dll", "arg1")
        .WithExample("run", "--correlate", "--snapshot-on", "throw:My.App.FatalException", "--", "./MyApp.dll");
    config.AddCommand<McpCommand>("mcp")
        .WithDescription("Serve the analysis tools to AI agents over MCP (stdio).");
});

return app.Run(args);
