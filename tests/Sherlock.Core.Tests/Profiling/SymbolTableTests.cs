using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Sherlock.Core.Profiling;
using Sherlock.Core.Storage;
using Sherlock.Core.Tests.Common;
using Xunit;
using static Sherlock.Tests.Frames;

namespace Sherlock.Core.Tests.Profiling;

public sealed class SymbolTableTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private SymbolTable RoundTrip(SymbolWriter writer)
    {
        var container = new ContainerWriter(SlabKind.Profile, ProfileFormat.Version);
        writer.WriteTo(container);
        using SlabFile slab = _tmp.WriteSlab(container);
        return SymbolTable.Read(slab);
    }

    [Fact]
    public void RecordLayoutsMatchNative()
    {
        Assert.Equal(8, Marshal.SizeOf<StringRef>());
        Assert.Equal(24, Marshal.SizeOf<ModuleRecord>());
        Assert.Equal(80, Marshal.SizeOf<MethodRecord>());
        Assert.Equal(8, Marshal.SizeOf<TypeRecord>());
        Assert.Equal(8, Marshal.SizeOf<StackRecord>());
    }

    [Fact]
    public void RoundTripsEveryPartOfAMethod()
    {
        var mvid = Guid.NewGuid();
        var method = new MethodFrame(mvid, "Shop.dll", 0x06000042, "Shop.Orders", "Repository<TOrder>+Cache", "Größe", "<TKey>",
            "(int, List<Order>)", "(int, System.Collections.Generic.List<Shop.Order>)", "Order", "Shop.Order", problem: null);
        var writer = new SymbolWriter();
        uint stack = writer.InternStack([writer.InternMethod(method)]);

        SymbolTable table = RoundTrip(writer);

        MethodFrame read = Assert.Single(table.Methods);
        Assert.Equal(method, read);
        Assert.Equal(mvid, read.Mvid);
        Assert.Equal("Shop.dll", read.Module);
        Assert.Equal(0x06000042u, read.Token);
        Assert.Equal("Shop.Orders", read.Namespace);
        Assert.Equal("Repository<TOrder>+Cache", read.Type);
        Assert.Equal("Größe", read.Name);
        Assert.Equal("<TKey>", read.GenericParameters);
        Assert.Equal("(int, List<Order>)", read.Parameters);
        Assert.Equal("(int, System.Collections.Generic.List<Shop.Order>)", read.QualifiedParameters);
        Assert.Equal("Order", read.ReturnType);
        Assert.Equal("Shop.Order", read.QualifiedReturnType);
        Assert.Null(read.Problem);
        Assert.Equal("Repository<TOrder>+Cache.Größe<TKey>(int, List<Order>)", read.Label);
        Assert.Same(read, Assert.Single(table.Stack(stack)));
    }

    [Fact]
    public void MethodsAreInternedByIdentity()
    {
        var writer = new SymbolWriter();
        uint libA = writer.InternMethod(Method("App.Mapper.Map(int)", "LibA.dll"));
        uint libB = writer.InternMethod(Method("App.Mapper.Map(int)", "LibB.dll"));

        Assert.NotEqual(libA, libB);
        Assert.Equal(libA, writer.InternMethod(Method("App.Mapper.Map(int)", "LibA.dll")));
    }

    [Fact]
    public void MethodsOfUnreadableModulesAreNeverMerged()
    {
        static MethodFrame Unresolved() => new(Guid.Empty, "", 0x06000001, "", "", "", "", "", "", "", "", "resolution failed");
        var writer = new SymbolWriter();
        uint first = writer.InternMethod(Unresolved());
        uint second = writer.InternMethod(Unresolved());
        writer.InternStack([first, second]);

        SymbolTable table = RoundTrip(writer);

        Assert.NotEqual(first, second);
        Assert.Equal(2, table.Methods.Count);
        Assert.NotEqual(table.Methods[0], table.Methods[1]);
        Assert.Equal(["<unresolved method>", "<unresolved method> #2"], table.Methods.Select(method => method.Label));
        Assert.Equal("resolution failed", table.Methods[0].Problem);
    }

    [Fact]
    public void AMethodNamedThroughAnyLoadOfItsModuleKeepsTheName()
    {
        MethodFrame named = Method("App.Mapper.Map(int)", "LibA.dll");
        var unresolved = new MethodFrame(named.Mvid, "LibA.dll", named.Token, "", "", "", "", "", "", "", "", "signature failed");
        var writer = new SymbolWriter();
        uint id = writer.InternMethod(unresolved);
        Assert.Equal(id, writer.InternMethod(named));
        Assert.Equal(id, writer.InternMethod(unresolved));
        writer.InternStack([id]);

        MethodFrame read = Assert.Single(RoundTrip(writer).Methods);

        Assert.Null(read.Problem);
        Assert.Equal("Mapper.Map(int)", read.Label);
    }

    [Fact]
    public void SitesAndObjectsOnTheSameStackShareItsFrames()
    {
        var writer = new SymbolWriter();
        uint stack = writer.InternStack([writer.InternMethod(Method("Program.Main"))]);

        SymbolTable table = RoundTrip(writer);

        Assert.Same(table.Stack(stack), table.Stack(stack));
    }

    [Fact]
    public void LabelsTellTheProfilesMethodsApart()
    {
        var writer = new SymbolWriter();
        writer.InternStack([
            writer.InternMethod(Method("App.Api.Mapper.Map(Order)")),
            writer.InternMethod(Method("App.Data.Mapper.Map(Order)")),
            writer.InternMethod(Method("App.Program.Main")),
        ]);

        SymbolTable table = RoundTrip(writer);

        Assert.Equal(["App.Api.Mapper.Map(Order)", "App.Data.Mapper.Map(Order)", "Program.Main()"], table.Methods.Select(method => method.Label));
    }

    [Fact]
    public void StacksAndTypesAreShared()
    {
        var writer = new SymbolWriter();
        uint main = writer.InternMethod(Method("Program.Main"));
        uint add = writer.InternMethod(Method("Registry.Add"));
        uint forward = writer.InternStack([main, add]);
        uint backward = writer.InternStack([add, main]);
        uint text = writer.InternType("System.String");
        uint bytes = writer.InternType("System.Byte[]");

        Assert.Equal(forward, writer.InternStack([main, add]));
        Assert.NotEqual(forward, backward);
        Assert.Equal(text, writer.InternType("System.String"));

        SymbolTable table = RoundTrip(writer);

        Assert.Equal(2, table.StackCount);
        Assert.Equal(Stack("Program.Main", "Registry.Add"), table.Stack(forward));
        Assert.Equal(Stack("Registry.Add", "Program.Main"), table.Stack(backward));
        Assert.Equal("System.String", table.Types[(int)text]);
        Assert.Equal("System.Byte[]", table.Types[(int)bytes]);
    }

    [Fact]
    public void EmptyTablesRoundTrip()
    {
        SymbolTable table = RoundTrip(new SymbolWriter());

        Assert.Empty(table.Methods);
        Assert.Empty(table.Types);
        Assert.Equal(0, table.StackCount);
    }

    [Fact]
    public void ReadsTheSectionsTheNativeWriterLaysOut()
    {
        using SlabFile slab = Sections(ProgramMain(), new StackRecord { FirstFrame = 0, FrameCount = 1 }, [0]);

        SymbolTable table = SymbolTable.Read(slab);

        Assert.Equal("Program.Main()", Assert.Single(table.Stack(0)).Label);
    }

    [Fact]
    public void RejectsAMissingSection()
    {
        var container = new ContainerWriter(SlabKind.Profile, ProfileFormat.Version);
        container.AddBytes(SectionType.Strings, []);
        using SlabFile slab = _tmp.WriteSlab(container);

        Assert.Contains("no Modules section", Assert.Throws<InvalidDataException>(() => SymbolTable.Read(slab)).Message);
    }

    [Fact]
    public void RejectsAMethodOfAnUnknownModule()
    {
        MethodRecord method = ProgramMain();
        method.Module = 1;
        using SlabFile slab = Sections(method, new StackRecord { FrameCount = 1 }, [0]);

        Assert.Throws<InvalidDataException>(() => SymbolTable.Read(slab));
    }

    [Fact]
    public void RejectsANamePastTheStringTable()
    {
        MethodRecord method = ProgramMain();
        method.Name = new StringRef { Offset = 10, Length = 4 };
        using SlabFile slab = Sections(method, new StackRecord { FrameCount = 1 }, [0]);

        Assert.Throws<InvalidDataException>(() => SymbolTable.Read(slab));
    }

    [Fact]
    public void RejectsAStackOfAnUnknownMethod()
    {
        using SlabFile slab = Sections(ProgramMain(), new StackRecord { FrameCount = 1 }, [1]);

        Assert.Throws<InvalidDataException>(() => SymbolTable.Read(slab));
    }

    [Fact]
    public void RejectsAStackDeeperThanAnyTheProfilerRecords()
    {
        uint[] frames = new uint[SymbolFormat.MaxStackDepth + 1];
        using SlabFile slab = Sections(ProgramMain(), new StackRecord { FrameCount = (uint)frames.Length }, frames);

        Assert.Contains("deeper than", Assert.Throws<InvalidDataException>(() => SymbolTable.Read(slab)).Message);
    }

    [Fact]
    public void RejectsAStackPastTheFramePool()
    {
        using SlabFile slab = Sections(ProgramMain(), new StackRecord { FirstFrame = 1, FrameCount = 1 }, [0]);

        Assert.Throws<InvalidDataException>(() => SymbolTable.Read(slab));
    }

    // Strings: "MainProgram()"; one module, one method (Program.Main()), one stack.
    private static MethodRecord ProgramMain() => new()
    {
        Name = new StringRef { Offset = 0, Length = 4 },
        Type = new StringRef { Offset = 4, Length = 7 },
        Parameters = new StringRef { Offset = 11, Length = 2 },
    };

    private SlabFile Sections(MethodRecord method, StackRecord stack, uint[] frames)
    {
        var container = new ContainerWriter(SlabKind.Profile, ProfileFormat.Version);
        container.AddBytes(SectionType.Strings, "MainProgram()"u8);
        container.AddRecords(SectionType.Modules, [new ModuleRecord { Mvid = Guid.NewGuid() }]);
        container.AddRecords(SectionType.Methods, [method]);
        container.AddRecords<TypeRecord>(SectionType.Types, []);
        container.AddRecords(SectionType.Stacks, [stack]);
        container.AddRecords(SectionType.StackFrames, frames);
        return _tmp.WriteSlab(container);
    }
}
