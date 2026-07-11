using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Sherlock.Core.Collection;

/// <summary>
/// The sl side of the control channel: a Unix-domain-socket server the in-process profilers connect
/// back to. A whole supervised subtree shares one socket, so it's multi-client - each connection
/// identifies itself by pid in its HELLO and requests route to a specific process. Handles the HELLO
/// handshake, request/response, and unsolicited events (probe hits, tagged with the firing pid).
/// </summary>
public sealed class ProfilerControlServer : IDisposable
{
    private readonly Socket _listener;
    private readonly string _path;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, Client> _clients = new(); // pid → connection

    /// <summary>A single connected profiler process.</summary>
    private sealed class Client
    {
        public required Socket Socket { get; init; }
        public string? Version { get; set; }
        public IReadOnlyList<string> Features { get; set; } = [];
        public readonly ConcurrentDictionary<int, TaskCompletionSource<string[]>> Pending = new();
        public int NextId;
    }

    /// <summary>Path of the socket the profiler connects to (passed via SHERLOCK_CONTROL_SOCKET).</summary>
    public string SocketPath => _path;

    /// <summary>Pids of every profiler currently connected.</summary>
    public IReadOnlyCollection<int> ConnectedPids => _clients.Keys.ToArray();

    /// <summary>Union of capabilities advertised by all connected profilers.</summary>
    public IReadOnlyList<string> Features =>
        _clients.Values.SelectMany(c => c.Features).Distinct().ToArray();

    /// <summary>Capabilities a specific profiler advertised, or empty if it isn't connected.</summary>
    public IReadOnlyList<string> FeaturesFor(int pid) =>
        _clients.TryGetValue(pid, out Client? c) ? c.Features : [];

    /// <summary>Raised for each EVENT frame, tagged with the pid that sent it.</summary>
    public event Action<int, string[]>? EventReceived;

    public ProfilerControlServer(string path)
    {
        _path = path;
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(path));
        _listener.Listen(backlog: 16); // a process subtree can bring several profilers at once
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Sends a request to a specific process and awaits its response.</summary>
    public async Task<(bool Ok, string[] Fields)> RequestAsync(int pid, string cmd, TimeSpan timeout, params string[] args)
    {
        Client? client = await WaitForClientAsync(pid, timeout);
        if (client is null)
        {
            return (false, []);
        }

        int id = Interlocked.Increment(ref client.NextId);
        var tcs = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Pending[id] = tcs;

        var parts = new List<string>(3 + args.Length) { "REQ", id.ToString(), cmd };
        parts.AddRange(args);
        try
        {
            await SendAsync(client.Socket, string.Join('\t', parts));
        }
        catch
        {
            client.Pending.TryRemove(id, out _);
            return (false, []);
        }

        try
        {
            string[] res = await tcs.Task.WaitAsync(timeout);
            bool ok = res.Length >= 3 && res[2] == "ok";
            string[] fields = res.Length > 3 ? res[3..] : [];
            return (ok, fields);
        }
        catch
        {
            client.Pending.TryRemove(id, out _);
            return (false, []);
        }
    }

    /// <summary>Waits (up to <paramref name="timeout"/>) for a process to connect and say HELLO.</summary>
    private async Task<Client?> WaitForClientAsync(int pid, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (_clients.TryGetValue(pid, out Client? client))
            {
                return client;
            }
            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }
            await Task.Delay(20);
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptAsync(_cts.Token);
            }
            catch
            {
                return; // listener disposed / cancelled
            }
            _ = Task.Run(() => ServeClientAsync(socket));
        }
    }

    private async Task ServeClientAsync(Socket socket)
    {
        var client = new Client { Socket = socket };
        int pid = 0;
        var buffer = new List<byte>();
        var chunk = new byte[4096];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n;
                try { n = await socket.ReceiveAsync(chunk, _cts.Token); }
                catch { break; }
                if (n <= 0)
                {
                    break;
                }

                buffer.AddRange(new ArraySegment<byte>(chunk, 0, n));
                while (TryReadFrame(buffer, out string payload))
                {
                    pid = Dispatch(client, pid, payload);
                }
            }
        }
        finally
        {
            if (pid != 0)
            {
                _clients.TryRemove(new KeyValuePair<int, Client>(pid, client));
            }
            try { socket.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>Handles one frame from a client; returns the (possibly newly-learned) client pid.</summary>
    private int Dispatch(Client client, int pid, string payload)
    {
        string[] fields = payload.Split('\t');
        switch (fields[0])
        {
            case "HELLO":
                client.Version = fields.Length > 1 ? fields[1] : null;
                client.Features = fields.Length > 2
                    ? fields[2].Split(',', StringSplitOptions.RemoveEmptyEntries)
                    : [];
                pid = fields.Length > 3 && int.TryParse(fields[3], out int p) ? p : 0;
                _clients[pid] = client;
                break;

            case "RES":
                if (fields.Length >= 3 && int.TryParse(fields[1], out int id) &&
                    client.Pending.TryRemove(id, out TaskCompletionSource<string[]>? tcs))
                {
                    tcs.TrySetResult(fields);
                }
                break;

            case "EVENT":
                EventReceived?.Invoke(pid, fields);
                break;
        }
        return pid;
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
        foreach (Client client in _clients.Values)
        {
            try { client.Socket.Dispose(); } catch { /* ignore */ }
        }
        try { _listener.Dispose(); } catch { /* ignore */ }
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* ignore */ }
    }
}
