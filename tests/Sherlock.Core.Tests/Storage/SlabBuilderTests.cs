using System;
using System.IO;
using System.Runtime.InteropServices;
using Sherlock.Core.Tests.Common;
using Sherlock.Storage;
using Xunit;

namespace Sherlock.Core.Tests.Storage;

public sealed unsafe class SlabBuilderTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void WritesSectionsInPlaceAndThroughTheMapping()
    {
        string path = _tmp.File();
        using (var slab = new SlabBuilder(path, sectionCount: 3))
        {
            long meta = slab.Place(SectionType.DomMeta, sizeof(Guid), 1);
            long idom = slab.Place(SectionType.DomIdom, sizeof(int), 5000);
            long children = slab.Place(SectionType.DomChildren, sizeof(int), 0);
            slab.Write(MemoryMarshal.AsBytes<Guid>([Guid.Empty]), meta);
            byte* file = slab.Map();
            var ids = new Span<int>(file + idom, 5000);
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = i * 7;
            }
            Assert.False(File.Exists(path));
            slab.Commit(SlabKind.Dominators, 9);
        }

        using SlabFile written = SlabFile.Open(path, SlabKind.Dominators, 9);
        Assert.Equal(Guid.Empty, written.GetColumn<Guid>(SectionType.DomMeta)[0]);
        Column<int> column = written.GetColumn<int>(SectionType.DomIdom);
        Assert.Equal(5000, column.Length);
        Assert.Equal(4999 * 7, column[4999]);
        Assert.Equal(0, written.GetColumn<int>(SectionType.DomChildren).Length);
        Assert.Equal([path], Directory.GetFiles(_tmp.Path));
    }

    [Fact]
    public void AnUncommittedSlabLeavesNothing()
    {
        string path = _tmp.File();
        using (var slab = new SlabBuilder(path, sectionCount: 1))
        {
            slab.Place(SectionType.DomIdom, sizeof(int), 10);
            slab.Map()[0] = 1;
        }

        Assert.Empty(Directory.GetFiles(_tmp.Path));
    }

    [Fact]
    public void RefusesToPublishMissingSections()
    {
        using var slab = new SlabBuilder(_tmp.File(), sectionCount: 2);
        slab.Place(SectionType.DomIdom, sizeof(int), 1);

        Assert.Throws<InvalidOperationException>(() => slab.Commit(SlabKind.Dominators, 1));
    }
}
