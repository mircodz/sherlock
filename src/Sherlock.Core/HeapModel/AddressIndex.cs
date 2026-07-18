using System;

namespace Sherlock.Core.HeapModel;

/// <summary>
/// Resolves an object address to its dense id over a sorted address array, in roughly O(1): a
/// two-level index that buckets the address range, then does a tiny binary search inside a bucket.
/// This replaces a plain ~log2(N) binary search, which dominates when resolving tens of millions of
/// reference targets during graph extraction.
/// </summary>
public sealed class AddressIndex
{
    private readonly ReadOnlyMemory<ulong> _addresses;
    private readonly int[] _bucketStart;
    private readonly ulong _min;
    private readonly int _shift;

    public AddressIndex(ReadOnlyMemory<ulong> sortedAddresses)
    {
        _addresses = sortedAddresses;
        ReadOnlySpan<ulong> a = sortedAddresses.Span;
        int n = a.Length;
        if (n == 0)
        {
            _bucketStart = [0, 0];
            return;
        }

        _min = a[0];
        ulong span = a[^1] - _min;
        while ((span >> _shift) > (ulong)n && _shift < 63) _shift++;
        int buckets = (int)((span >> _shift) + 2);
        _bucketStart = new int[buckets + 1];
        int bi = 0;
        for (int i = 0; i < n; i++)
        {
            int b = (int)((a[i] - _min) >> _shift);
            while (bi <= b) _bucketStart[bi++] = i;
        }
        while (bi <= buckets) _bucketStart[bi++] = n;
    }

    /// <summary>The dense id of <paramref name="address"/>, or -1 if absent.</summary>
    public int IndexOf(ulong address)
    {
        ReadOnlySpan<ulong> addresses = _addresses.Span;
        int n = addresses.Length;
        if (n == 0 || address < addresses[0] || address > addresses[n - 1])
        {
            return -1;
        }

        int b = (int)((address - _min) >> _shift);
        int lo = _bucketStart[b], hi = _bucketStart[b + 1];
        while (lo < hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            ulong v = addresses[mid];
            if (v == address) return mid;
            if (v < address) lo = mid + 1; else hi = mid;
        }
        return -1;
    }
}
