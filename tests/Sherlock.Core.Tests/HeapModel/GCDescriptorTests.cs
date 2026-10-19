using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;
using Sherlock.Core.HeapModel;
using Xunit;

namespace Sherlock.Core.Tests.HeapModel;

public sealed unsafe class GCDescriptorTests
{
    [Fact]
    public void ObjectWithTwoSeriesVisitsTheHighestFirst()
    {
        // Base size 40: references at 8 (one slot) and 24 (two slots); sizes are biased by the base size.
        byte[] descriptor = Series((8, 8 - 40), (24, 16 - 40));
        AssertSameAsClrMD(descriptor, ObjectBytes(40, (8, 0x1000), (24, 0x2000), (32, 0x3000)));
    }

    [Fact]
    public void ReferenceArrayCoversEveryElement()
    {
        byte[] descriptor = Series((16, -24)); // object[]: elements from 16 to the end
        AssertSameAsClrMD(descriptor, ObjectBytes(24 + 8 * 5, (16, 0xA), (24, 0), (32, 0xB), (40, 0xC), (48, 0)));
    }

    [Fact]
    public void StructArrayRepeatsRunsAndSkipsPerElement()
    {
        // struct { object a; long x; object b; }[]: per element one reference, skip 8, one reference.
        byte[] descriptor = Repeating(start: 16, (1, 8), (1, 0));
        AssertSameAsClrMD(descriptor, ObjectBytes(24 + 24 * 3, (16, 0x10), (32, 0x11), (40, 0), (56, 0x13), (64, 0x14), (80, 0x15)));
    }

    [Fact]
    public void SeriesPastTheObjectIsRejected()
    {
        GCDescriptor descriptor = GCDescriptor.Decode(Series((16, 0)), 0x5000); // claims the whole object again, past its end
        byte[] bytes = ObjectBytes(32);

        var error = Assert.Throws<InvalidDataException>(() => Walk(descriptor, bytes));
        Assert.Contains("doesn't fit", error.Message);
    }

    private static void AssertSameAsClrMD(byte[] descriptor, byte[] bytes)
    {
        ulong[] expected = [.. new GCDesc(descriptor, 8).WalkObject(bytes, bytes.Length).Select(r => r.ReferencedObject)];
        Assert.NotEmpty(expected);
        Assert.Equal(expected, Walk(GCDescriptor.Decode(descriptor, 0x5000), bytes));
    }

    private static List<ulong> Walk(GCDescriptor descriptor, byte[] bytes)
    {
        var visitor = new Collect { References = [] };
        fixed (byte* p = bytes)
        {
            descriptor.Walk(p, bytes.Length, ref visitor);
        }
        return visitor.References;
    }

    private struct Collect : IReferenceVisitor
    {
        public List<ulong> References;
        public readonly void Reference(ulong target) => References.Add(target);
    }

    // The bytes below a method table: series in reverse (the first given ends up highest), then their count.
    private static byte[] Series(params (long Offset, long Size)[] series)
    {
        var data = new byte[8 * (1 + 2 * series.Length)];
        for (int i = 0; i < series.Length; i++)
        {
            int at = data.Length - 24 - 16 * i;
            MemoryMarshal.Write(data.AsSpan(at), series[i].Size);
            MemoryMarshal.Write(data.AsSpan(at + 8), series[i].Offset);
        }
        MemoryMarshal.Write(data.AsSpan(data.Length - 8), (long)series.Length);
        return data;
    }

    // An array of structs: the count is negative, the start offset sits below it, then one (references, skip) per run.
    private static byte[] Repeating(long start, params (int References, int Skip)[] runs)
    {
        var data = new byte[8 * (1 + 2 * runs.Length)];
        for (int i = 0; i < runs.Length; i++)
        {
            int at = data.Length - 24 - 8 * i;
            MemoryMarshal.Write(data.AsSpan(at), runs[i].References);
            MemoryMarshal.Write(data.AsSpan(at + 4), runs[i].Skip);
        }
        MemoryMarshal.Write(data.AsSpan(data.Length - 16), start);
        MemoryMarshal.Write(data.AsSpan(data.Length - 8), -(long)runs.Length);
        return data;
    }

    private static byte[] ObjectBytes(int size, params (int Offset, ulong Value)[] slots)
    {
        var bytes = new byte[size];
        MemoryMarshal.Write(bytes, 0x5000UL); // method table
        foreach ((int offset, ulong value) in slots)
        {
            MemoryMarshal.Write(bytes.AsSpan(offset), value);
        }
        return bytes;
    }
}
