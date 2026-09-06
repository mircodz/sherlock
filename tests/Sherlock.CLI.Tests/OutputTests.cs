using System;
using System.IO;
using Sherlock.CLI.Export;
using Sherlock.CLI.Rendering;
using Sherlock.Core.Profiling;
using Sherlock.Core.Store;
using Spectre.Console;
using Xunit;

namespace Sherlock.CLI.Tests;

public sealed class OutputTests
{
    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "release failed", false)]
    [InlineData(false, "capture failed", false)]
    public void TriggeredCapturePreservesStatusAndFailureDetails(bool hasEntry, string? error, bool succeeded)
    {
        using var text = new StringWriter();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(text),
            Ansi = AnsiSupport.No,
            Interactive = InteractionSupport.No,
        });
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "heap.dmp");
        var entry = hasEntry ? new SnapshotEntry("s1", path, false, null, DateTimeOffset.UtcNow, 32) : null;

        bool result = Output.TriggeredCapture(console, new TriggeredCaptureResult("gc[2]", entry, error));

        Assert.Equal(succeeded, result);
        string output = text.ToString();
        if (hasEntry)
        {
            Assert.Contains("[+] gc[2] fired", output);
            Assert.Contains("snapshot s1", output);
            Assert.Contains("(heap only)", output);
        }
        else
        {
            Assert.Contains("[x] gc[2] fired but capture failed", output);
        }
        if (error is not null)
        {
            Assert.Contains(error, output);
            if (hasEntry)
            {
                Assert.Contains("[!]", output);
            }
        }
    }

    [Fact]
    public void DotGraphKeepsEscapingAndMatchingBorderAndFontColors()
    {
        var graph = new DotGraph("example");
        graph.AddNode("root", heat: 0, size: 0, "quoted \"value\"");

        string output = graph.Render();

        Assert.Contains("label=\"quoted \\\"value\\\"\"", output);
        Assert.Contains("fontsize=8", output);
        Assert.Contains("fillcolor=\"#ededed\"", output);
        Assert.Contains("color=\"#b2b2b2\", fontcolor=\"#b2b2b2\"", output);
    }

    [Fact]
    public void FoldedStacksKeepLegacyEscapingWeightsAndFrameOrder()
    {
        var profile = new AllocationProfile(
        [
            new(["Root;Literal", "Leaf"], 256, 2, 64, 1),
            new(["Empty"], 0, 0, 0, 0),
        ]);

        Assert.Equal("Root:Literal;Leaf 256\n", FoldedStacks.Write(profile));
        Assert.Equal("Root:Literal;Leaf 64\n", FoldedStacks.Write(profile, survived: true));
    }
}
