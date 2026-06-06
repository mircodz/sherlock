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
        .WithDescription("Launch a process under supervision and snapshot it on demand.")
        .WithExample("run", "./MyApp.dll")
        .WithExample("run", "--profile", "--", "./MyApp.dll", "arg1");
});

return app.Run(args);
