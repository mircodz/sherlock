using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Sherlock.Core.Profiling;

/// <summary>
/// A live-object → allocation-stack map, as emitted by the native profiler under
/// <c>SHERLOCK_CORRELATE</c>. Each line is <c>0x&lt;address&gt;\t&lt;object_id&gt;\t&lt;stack&gt;</c>,
/// where the stack is folded root→leaf. Joined against a heap dump by address so that
/// analysis can answer "this retained object was allocated here".
/// </summary>
public sealed class CorrelationMap
{
    private readonly Dictionary<ulong, string> _stackByAddress;

    private CorrelationMap(Dictionary<ulong, string> stackByAddress) => _stackByAddress = stackByAddress;

    /// <summary>Number of live objects with a recorded allocation stack.</summary>
    public int Count => _stackByAddress.Count;

    /// <summary>The allocation stack for an object address, or null if untracked.</summary>
    public string? StackFor(ulong address) =>
        _stackByAddress.TryGetValue(address, out string? s) ? s : null;

    public static CorrelationMap Read(string path)
    {
        var map = new Dictionary<ulong, string>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            string addr = parts[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parts[0][2..] : parts[0];
            if (ulong.TryParse(addr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address))
            {
                map[address] = parts[2];
            }
        }
        return new CorrelationMap(map);
    }
}
