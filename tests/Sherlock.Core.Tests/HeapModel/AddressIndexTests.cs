using System;
using Sherlock.Core.HeapModel;

namespace Sherlock.Core.Tests.HeapModel;

/// <summary>
/// Exercises <see cref="AddressIndex"/>, the two-level bucket index that resolves an object address to
/// its dense id. It replaces a plain binary search over hundreds of millions of addresses, so a
/// boundary/off-by-one bug here silently mis-resolves reference targets during graph extraction.
/// </summary>
public sealed class AddressIndexTests
{
    [Fact]
    public void ResolvesEveryPresentAddress()
    {
        ulong[] addresses = [0x1000, 0x1008, 0x2000, 0x2400, 0x9000, 0x9008, 0xF000];
        var index = new AddressIndex(addresses);
        for (int i = 0; i < addresses.Length; i++)
        {
            Assert.Equal(i, index.IndexOf(addresses[i]));
        }
    }

    [Fact]
    public void ReturnsMinusOneForAbsentAddresses()
    {
        ulong[] addresses = [0x1000, 0x2000, 0x3000];
        var index = new AddressIndex(addresses);
        Assert.Equal(-1, index.IndexOf(0x0FFF)); // before the first
        Assert.Equal(-1, index.IndexOf(0x1001)); // between two objects
        Assert.Equal(-1, index.IndexOf(0x2FFF)); // between two objects
        Assert.Equal(-1, index.IndexOf(0x3001)); // just past the last
        Assert.Equal(-1, index.IndexOf(0xFFFF)); // far past the end
    }

    [Fact]
    public void HandlesEmptyAndSingleton()
    {
        var empty = new AddressIndex((ulong[])[]);
        Assert.Equal(-1, empty.IndexOf(0x1000));

        var one = new AddressIndex((ulong[])[0x4000]);
        Assert.Equal(0, one.IndexOf(0x4000));
        Assert.Equal(-1, one.IndexOf(0x3FFF));
        Assert.Equal(-1, one.IndexOf(0x4001));
    }

    [Fact]
    public void HandlesDenseContiguousAddresses()
    {
        // Contiguous 8-byte-spaced addresses stress the bucketing (small span, many entries).
        var addresses = new ulong[1000];
        for (int i = 0; i < addresses.Length; i++) addresses[i] = 0x10000 + (ulong)i * 8;
        var index = new AddressIndex(addresses);
        for (int i = 0; i < addresses.Length; i++)
        {
            Assert.Equal(i, index.IndexOf(addresses[i]));
            Assert.Equal(-1, index.IndexOf(addresses[i] + 1)); // interior byte
        }
    }

    [Fact]
    public void HandlesWideSparseAddresses()
    {
        // A huge span with few entries stresses the shift/bucket math the other direction.
        ulong[] addresses = [0x1000, 0x8000_0000, 0xFFFF_FFFF_0000];
        var index = new AddressIndex(addresses);
        Assert.Equal(0, index.IndexOf(0x1000));
        Assert.Equal(1, index.IndexOf(0x8000_0000));
        Assert.Equal(2, index.IndexOf(0xFFFF_FFFF_0000));
        Assert.Equal(-1, index.IndexOf(0x4000_0000));
    }

    [Fact]
    public void MatchesBinarySearchOnRandomizedInput()
    {
        // Differential check: the index must agree with a plain binary search over the same sorted set.
        var rng = new Random(12345);
        var set = new System.Collections.Generic.SortedSet<ulong>();
        while (set.Count < 5000) set.Add((ulong)rng.NextInt64(0x1000, 0x1_0000_0000));
        ulong[] addresses = [.. set];
        var index = new AddressIndex(addresses);

        for (int probe = 0; probe < 20000; probe++)
        {
            ulong x = (ulong)rng.NextInt64(0, 0x1_0000_0000);
            int expected = Array.BinarySearch(addresses, x);
            expected = expected >= 0 ? expected : -1;
            Assert.Equal(expected, index.IndexOf(x));
        }
    }
}
