using System.Runtime.InteropServices;
using Sherlock.Core.Storage;

namespace Sherlock.Core.Tests.Storage;

public class StackTableTests
{
    [Fact]
    public void RecordLayoutsMatchNative()
    {
        // The native FrameRecord/StackRecord are packed 8-byte records; the managed mirrors must match.
        Assert.Equal(8, Marshal.SizeOf<FrameRecord>());
        Assert.Equal(8, Marshal.SizeOf<StackRecord>());
    }

    [Fact]
    public void InternsAndDedups()
    {
        var b = new StackTableBuilder();
        uint main = b.InternFrame("Program.Main");
        uint add = b.InternFrame("Registry.Add");
        Assert.Equal(main, b.InternFrame("Program.Main")); // same name → same id

        uint s1 = b.InternStack([main, add]);
        Assert.Equal(s1, b.InternStack([main, add]));       // same stack → same id
        Assert.NotEqual(s1, b.InternStack([add, main]));    // order matters
    }

    [Fact]
    public void RoundTripsThroughContainer()
    {
        var b = new StackTableBuilder();
        uint main = b.InternFrame("Program.Main");
        uint add = b.InternFrame("Registry.Add");
        uint resize = b.InternFrame("List.Resize");
        uint deep = b.InternStack([main, add, resize]);

        var w = new ContainerWriter();
        b.WriteTo(w);
        var table = StackTable.Read(new ContainerReader(w.ToArray()));

        Assert.Equal(3, table.FrameCount);
        Assert.Equal("Program.Main", table.Frame(main));
        Assert.Equal("Registry.Add", table.Frame(add));
        Assert.Equal("List.Resize", table.Frame(resize));

        Assert.Equal([main, add, resize], table.FrameIds(deep).ToArray());
        Assert.Equal("Program.Main;Registry.Add;List.Resize", table.FormatStack(deep));
    }

    [Fact]
    public void Empty_RoundTrips()
    {
        var w = new ContainerWriter();
        new StackTableBuilder().WriteTo(w);
        var table = StackTable.Read(new ContainerReader(w.ToArray()));
        Assert.Equal(0, table.FrameCount);
        Assert.Equal(0, table.StackCount);
    }
}
