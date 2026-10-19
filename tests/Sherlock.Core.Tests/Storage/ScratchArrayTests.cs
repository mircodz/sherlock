using System;
using System.IO;
using Sherlock.Core.Storage;
using Sherlock.Core.Tests.Common;
using Xunit;

namespace Sherlock.Core.Tests.Storage;

public sealed unsafe class ScratchArrayTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void StartsZeroedAndHoldsWrites()
    {
        using var array = new ScratchArray<long>(_tmp.Path, 1_000_000);

        Assert.Equal(1_000_000, array.Length);
        long* items = array.Pointer;
        for (int i = 0; i < 1_000_000; i += 997)
        {
            Assert.Equal(0, items[i]);
            items[i] = i * 3L;
        }
        for (int i = 0; i < 1_000_000; i += 997)
        {
            Assert.Equal(i * 3L, items[i]);
        }
    }

    [Fact]
    public void LeavesNoFileBehind()
    {
        using (var array = new ScratchArray<int>(_tmp.Path, 4096))
        {
            array.Pointer[0] = 1;
            if (!OperatingSystem.IsWindows())
            {
                Assert.Empty(Directory.GetFiles(_tmp.Path)); // unlinked while still mapped
            }
        }
        Assert.Empty(Directory.GetFiles(_tmp.Path));
    }

    [Fact]
    public void AnEmptyArrayCanBeCreated()
    {
        using var array = new ScratchArray<int>(_tmp.Path, 0);

        Assert.Equal(0, array.Length);
    }
}
