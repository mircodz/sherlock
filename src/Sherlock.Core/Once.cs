using System;
using System.Threading;

namespace Sherlock.Core;

/// <summary>A value computed by its first caller and shared with every later one. Concurrent callers wait for that
/// computation instead of repeating it. A computation that throws or is cancelled leaves nothing behind, so the next
/// caller tries again.</summary>
internal sealed class Once<T> where T : class
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private T? _value;

    public Once()
    {
    }

    public Once(T value) => _value = value;

    /// <summary>The value, computing it with <paramref name="compute"/> if no caller has yet. Waiting for another
    /// caller's computation stops when <paramref name="cancellationToken"/> is cancelled.</summary>
    public T Get(Func<T> compute, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _value) is { } value)
        {
            return value;
        }
        _gate.Wait(cancellationToken);
        try
        {
            if (_value is { } computed)
            {
                return computed;
            }
            T result = compute();
            Volatile.Write(ref _value, result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}
