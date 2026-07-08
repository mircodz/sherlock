using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Sherlock.Core.Collection;

/// <summary>
/// The sl side of the sl ↔ profiler control channel: a Unix-domain-socket server that the
/// in-process profiler connects back to. Handles the capability handshake (HELLO),
/// request/response (emit-correlation, flush-allocations, …), and unsolicited events
/// (probe hits). Framing/verbs mirror <c>sherlock/control/protocol.hpp</c>.
/// </summary>
public sealed class ProfilerControlServer : IDisposable
{
    private readonly Socket _listener;
    private readonly string _path;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string[]>> _pending = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Socket? _client;
    private int _nextId;

    /// <summary>Path of the socket the profiler connects to (passed via SHERLOCK_CONTROL_SOCKET).</summary>
    public string SocketPath => _path;

    /// <summary>Profiler version reported in HELLO, once connected.</summary>
    public string? Version { get; private set; }

    /// <summary>Capabilities the profiler advertised (e.g. correlate, probes, allocations).</summary>
    public IReadOnlyList<string> Features { get; private set; } = [];

    /// <summary>Raised for each EVENT frame (fields after the "EVENT" verb), e.g. probe hits.</summary>
    public event Action<string[]>? EventReceived;

    public ProfilerControlServer(string path)
    {
        _path = path;
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(path));
        _listener.Listen(1);
        _ = Task.Run(AcceptAndServeAsync);
    }

    /// <summary>Sends a request and awaits its response. Returns (ok, detail fields after ok/err).</summary>
    public async Task<(bool Ok, string[] Fields)> RequestAsync(string cmd, TimeSpan timeout, params string[] args)
    {
        try
        {
            await _ready.Task.WaitAsync(timeout);
        }
        catch
        {
            return (false, []);
        }

        Socket? client = _client;
        if (client is null)
        {
            return (false, []);
        }

        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var parts = new List<string>(3 + args.Length) { "REQ", id.ToString(), cmd };
        parts.AddRange(args);
        await SendAsync(client, string.Join('\t', parts));

        try
        {
            string[] res = await tcs.Task.WaitAsync(timeout);
            bool ok = res.Length >= 3 && res[2] == "ok";
            string[] fields = res.Length > 3 ? res[3..] : [];
            return (ok, fields);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            return (false, []);
        }
    }

    private async Task AcceptAndServeAsync()
    {
        try
        {
            _client = await _listener.AcceptAsync(_cts.Token);
        }
        catch
        {
            return;
        }

        var buffer = new List<byte>();
        var chunk = new byte[4096];
        while (!_cts.IsCancellationRequested)
        {
            int n;
            try { n = await _client.ReceiveAsync(chunk, _cts.Token); }
            catch { break; }
            if (n <= 0)
            {
                break;
            }

            buffer.AddRange(new ArraySegment<byte>(chunk, 0, n));
            while (TryReadFrame(buffer, out string payload))
            {
                Dispatch(payload);
            }
        }
    }

    private void Dispatch(string payload)
    {
        string[] fields = payload.Split('\t');
        switch (fields[0])
        {
            case "HELLO":
                Version = fields.Length > 1 ? fields[1] : null;
                Features = fields.Length > 2
                    ? fields[2].Split(',', StringSplitOptions.RemoveEmptyEntries)
                    : [];
                _ready.TrySetResult();
                break;

            case "RES":
                if (fields.Length >= 3 && int.TryParse(fields[1], out int id) &&
                    _pending.TryRemove(id, out TaskCompletionSource<string[]>? tcs))
                {
                    tcs.TrySetResult(fields);
                }
                break;

            case "EVENT":
                EventReceived?.Invoke(fields);
                break;
        }
    }

    private static async Task SendAsync(Socket client, string payload)
    {
        byte[] body = Encoding.UTF8.GetBytes(payload);
        var framed = new byte[4 + body.Length];
        framed[0] = (byte)body.Length;
        framed[1] = (byte)(body.Length >> 8);
        framed[2] = (byte)(body.Length >> 16);
        framed[3] = (byte)(body.Length >> 24);
        Buffer.BlockCopy(body, 0, framed, 4, body.Length);
        await client.SendAsync(framed);
    }

    private static bool TryReadFrame(List<byte> buffer, out string payload)
    {
        payload = string.Empty;
        if (buffer.Count < 4)
        {
            return false;
        }

        int len = buffer[0] | (buffer[1] << 8) | (buffer[2] << 16) | (buffer[3] << 24);
        if (buffer.Count < 4 + len)
        {
            return false;
        }

        payload = Encoding.UTF8.GetString(buffer.GetRange(4, len).ToArray());
        buffer.RemoveRange(0, 4 + len);
        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _client?.Dispose(); } catch { /* ignore */ }
        try { _listener.Dispose(); } catch { /* ignore */ }
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* ignore */ }
    }
}
