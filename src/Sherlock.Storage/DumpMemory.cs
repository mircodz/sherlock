using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sherlock.Storage;

/// <summary>
/// The memory a dump captured, read in place through one read-only mapping of the dump file. Reads Mach-O and ELF
/// cores and Windows minidumps. Nothing is copied, and the pages are the file's, so the OS can drop them under
/// memory pressure instead of the process running out.
/// </summary>
public sealed unsafe partial class DumpMemory : IDisposable
{
    private readonly record struct Range(ulong Address, ulong Size, long Offset);

    private readonly MappedFile _file;
    private readonly ulong[] _starts;
    private readonly ulong[] _ends;
    private readonly long[] _offsets;

    private DumpMemory(MappedFile file, List<Range> ranges)
    {
        _file = file;
        _starts = new ulong[ranges.Count];
        _ends = new ulong[ranges.Count];
        _offsets = new long[ranges.Count];
        for (int i = 0; i < ranges.Count; i++)
        {
            _starts[i] = ranges[i].Address;
            _ends[i] = ranges[i].Address + ranges[i].Size;
            _offsets[i] = ranges[i].Offset;
        }
    }

    public static DumpMemory Open(string path)
    {
        MappedFile file = MappedFile.Open(path);
        try
        {
            return new DumpMemory(file, Merge(Ranges(file.Pointer, file.Length), file.Length));
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>The bytes at <paramref name="address"/> when one run of the file holds all <paramref name="length"/> of
    /// them, otherwise null. <paramref name="limit"/> is the first address past that run.</summary>
    public byte* Pointer(ulong address, ulong length, out ulong limit)
    {
        limit = 0;
        int i = Find(address);
        if (i < 0 || length > _ends[i] - address)
        {
            return null;
        }
        limit = _ends[i];
        return _file.Pointer + _offsets[i] + (long)(address - _starts[i]);
    }

    /// <summary>Copies bytes that may span neighbouring runs; false when some of them weren't captured.</summary>
    public bool TryCopy(ulong address, Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            int i = Find(address);
            if (i < 0)
            {
                return false;
            }
            int length = (int)Math.Min((ulong)destination.Length, _ends[i] - address);
            new ReadOnlySpan<byte>(_file.Pointer + _offsets[i] + (long)(address - _starts[i]), length).CopyTo(destination);
            destination = destination[length..];
            address += (ulong)length;
        }
        return true;
    }

    /// <summary>How many bytes from <paramref name="address"/> the dump holds before its first gap, up to <paramref name="length"/>.</summary>
    public ulong Captured(ulong address, ulong length)
    {
        ulong captured = 0;
        for (int i; captured < length && (i = Find(address + captured)) >= 0;)
        {
            captured = Math.Min(length, _ends[i] - address);
        }
        return captured;
    }

    /// <summary>Asks the OS to start reading [address, address + length) from the file, so a walk over an uncached dump
    /// keeps the disk busy instead of faulting in a page at a time.</summary>
    public void WillNeed(ulong address, ulong length)
    {
        ulong end = address + length;
        while (address < end)
        {
            int i = Find(address);
            if (i < 0)
            {
                return;
            }
            ulong stop = Math.Min(end, _ends[i]);
            ReadAhead(_file.Pointer + _offsets[i] + (long)(address - _starts[i]), (nuint)(stop - address));
            address = stop;
        }
    }

    private int Find(ulong address)
    {
        int lo = 0, hi = _starts.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_starts[mid] <= address)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found >= 0 && address < _ends[found] ? found : -1;
    }

    private static List<Range> Ranges(byte* p, long length)
    {
        uint magic = At<uint>(p, length, 0);
        return magic switch
        {
            0xfeedfacf => MachO(p, length),
            0x464c457f => Elf(p, length),
            0x504d444d => Minidump(p, length),
            _ => throw new InvalidDataException($"The dump isn't a 64-bit Mach-O or ELF core or a minidump (magic 0x{magic:x8})."),
        };
    }

    private static List<Range> MachO(byte* p, long length)
    {
        uint commands = At<uint>(p, length, 16);
        var ranges = new List<Range>();
        ulong at = 32;
        for (uint i = 0; i < commands; i++)
        {
            uint command = At<uint>(p, length, at);
            uint size = At<uint>(p, length, at + 4);
            if (size < 8)
            {
                throw new InvalidDataException($"Mach-O load command {i} has size {size}.");
            }
            if (command == 0x19) // LC_SEGMENT_64
            {
                ulong address = At<ulong>(p, length, at + 24);
                ulong memorySize = At<ulong>(p, length, at + 32);
                ulong fileOffset = At<ulong>(p, length, at + 40);
                ulong fileSize = At<ulong>(p, length, at + 48);
                ranges.Add(new Range(address, Math.Min(memorySize, fileSize), (long)fileOffset));
            }
            at += size;
        }
        return ranges;
    }

    private static List<Range> Elf(byte* p, long length)
    {
        if (At<byte>(p, length, 4) != 2 || At<byte>(p, length, 5) != 1)
        {
            throw new InvalidDataException("The ELF core isn't 64-bit little-endian.");
        }
        ulong headers = At<ulong>(p, length, 0x20);
        ulong sections = At<ulong>(p, length, 0x28);
        ulong entrySize = At<ushort>(p, length, 0x36);
        ulong count = At<ushort>(p, length, 0x38);
        if (count == 0xffff)
        {
            count = At<uint>(p, length, sections + 44); // PN_XNUM: the count is in section 0's sh_info
        }
        var ranges = new List<Range>();
        for (ulong i = 0; i < count; i++)
        {
            ulong header = headers + i * entrySize;
            if (At<uint>(p, length, header) == 1) // PT_LOAD; only p_filesz bytes are in the file
            {
                ranges.Add(new Range(At<ulong>(p, length, header + 16), At<ulong>(p, length, header + 32), (long)At<ulong>(p, length, header + 8)));
            }
        }
        return ranges;
    }

    private static List<Range> Minidump(byte* p, long length)
    {
        uint streams = At<uint>(p, length, 8);
        uint directory = At<uint>(p, length, 12);
        var ranges = new List<Range>();
        for (uint i = 0; i < streams; i++)
        {
            ulong entry = directory + i * 12UL;
            uint type = At<uint>(p, length, entry);
            uint rva = At<uint>(p, length, entry + 8);
            if (type == 9) // Memory64ListStream: the ranges' bytes follow each other from one base offset
            {
                ulong count = At<ulong>(p, length, rva);
                ulong offset = At<ulong>(p, length, rva + 8UL);
                for (ulong r = 0; r < count; r++)
                {
                    ulong size = At<ulong>(p, length, rva + 24 + r * 16);
                    ranges.Add(new Range(At<ulong>(p, length, rva + 16 + r * 16), size, (long)offset));
                    offset += size;
                }
            }
            else if (type == 5) // MemoryListStream
            {
                uint count = At<uint>(p, length, rva);
                for (uint r = 0; r < count; r++)
                {
                    ulong descriptor = rva + 4 + r * 16UL;
                    ranges.Add(new Range(At<ulong>(p, length, descriptor), At<uint>(p, length, descriptor + 8), At<uint>(p, length, descriptor + 12)));
                }
            }
        }
        return ranges;
    }

    // Sorted by address. Neighbours contiguous in memory and in the file become one run; overlaps keep the first,
    // and a range cut short by the end of the file keeps what the file has.
    private static List<Range> Merge(List<Range> ranges, long length)
    {
        ranges.Sort((a, b) => a.Address.CompareTo(b.Address));
        var merged = new List<Range>(ranges.Count);
        foreach (Range range in ranges)
        {
            Range r = range;
            if (r.Offset < 0 || r.Offset >= length || r.Size == 0)
            {
                continue;
            }
            if ((ulong)(length - r.Offset) < r.Size)
            {
                r = r with { Size = (ulong)(length - r.Offset) };
            }
            if (merged.Count > 0)
            {
                Range last = merged[^1];
                ulong lastEnd = last.Address + last.Size;
                if (r.Address < lastEnd)
                {
                    ulong overlap = lastEnd - r.Address;
                    if (overlap >= r.Size)
                    {
                        continue;
                    }
                    r = new Range(lastEnd, r.Size - overlap, r.Offset + (long)overlap);
                }
                if (r.Address == lastEnd && last.Offset + (long)last.Size == r.Offset)
                {
                    merged[^1] = last with { Size = last.Size + r.Size };
                    continue;
                }
            }
            merged.Add(r);
        }
        return merged;
    }

    private static T At<T>(byte* p, long length, ulong offset) where T : unmanaged
    {
        if (offset > (ulong)length || (ulong)length - offset < (ulong)sizeof(T))
        {
            throw new InvalidDataException($"The dump's headers point past its end (offset 0x{offset:x}).");
        }
        return Unsafe.ReadUnaligned<T>(p + offset);
    }

    // Read-ahead only speeds reads up, so it turns itself off when the OS doesn't provide it.
    private static readonly delegate* unmanaged<void*, nuint, int, int> Madvise = FindMadvise();
    private static bool _prefetchUnavailable;

    private static delegate* unmanaged<void*, nuint, int, int> FindMadvise() =>
        !OperatingSystem.IsWindows() && NativeLibrary.TryGetExport(NativeLibrary.GetMainProgramHandle(), "madvise", out nint madvise)
            ? (delegate* unmanaged<void*, nuint, int, int>)madvise
            : null;

    private static void ReadAhead(byte* start, nuint length)
    {
        if (OperatingSystem.IsWindows())
        {
            if (_prefetchUnavailable)
            {
                return;
            }
            var range = new PrefetchRange { Address = start, Length = length };
            try
            {
                PrefetchVirtualMemory(-1, 1, &range, 0); // -1: the current process
            }
            catch (EntryPointNotFoundException)
            {
                _prefetchUnavailable = true;
            }
            return;
        }
        if (Madvise == null)
        {
            return;
        }
        nuint page = (nuint)Environment.SystemPageSize;
        nuint first = (nuint)start & ~(page - 1);
        Madvise((void*)first, (nuint)start - first + length, 3); // MADV_WILLNEED on Linux and macOS
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrefetchRange
    {
        public void* Address;
        public nuint Length;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrefetchVirtualMemory(nint process, nuint count, PrefetchRange* ranges, uint flags);

    public void Dispose() => _file.Dispose();
}
