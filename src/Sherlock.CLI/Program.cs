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
});

return app.Run(args);
