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

/// <summary>Control connection shared by every profiled process in a run.</summary>
internal sealed class ProfilerControl : IDisposable
{
    private const int MaxFrameBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan MaxClientWait = TimeSpan.FromSeconds(10);
    internal const string EmitCorrelation = "emit-correlation";
    internal const string FlushAllocations = "flush-allocations";
    internal const string ArmTrigger = "arm-trigger";
    internal const string GcCount = "gc-count";
    internal const string HeapSize = "heap-size";
    internal const string SnapshotTrigger = "snapshot-trigger";

    private readonly Socket _listener;
    private readonly string _path;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, Client> _clients = new(); // pid → connection

    private sealed class Client
    {
        public required Socket Socket { get; init; }
        public IReadOnlyList<string> Features { get; set; } = [];
        public readonly ConcurrentDictionary<int, TaskCompletionSource<string[]>> Pending = new();
        public readonly SemaphoreSlim SendLock = new(1, 1);
        public int NextId;
    }

    public string SocketPath => _path;

    public IReadOnlyList<string> Features =>
        _clients.Values.SelectMany(c => c.Features).Distinct().ToArray();

    public event Action<int, string[]>? EventReceived;

    public ProfilerControl(string path)
    {
        _path = path;
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(path));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite);
        }
        _listener.Listen(backlog: 16); // a subtree can bring several profilers at once
        _ = Task.Run(AcceptLoopAsync);
    }

    public async Task<(bool Ok, string[] Fields)> RequestAsync(int pid, string cmd, TimeSpan timeout, params string[] args)
    {
        Client? client = await WaitForClientAsync(pid, timeout < MaxClientWait ? timeout : MaxClientWait);
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
            await client.SendLock.WaitAsync(_cts.Token);
            try
            {
                await SendAsync(client.Socket, string.Join('\t', parts), _cts.Token);
            }
            finally
            {
                client.SendLock.Release();
            }
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
        catch (Exception) when (_cts.IsCancellationRequested)
        {
        }
        catch (SocketException)
        {
        }
        catch (InvalidDataException)
        {
        }
        finally
        {
            if (pid != 0)
            {
                _clients.TryRemove(new KeyValuePair<int, Client>(pid, client));
            }
            foreach ((int id, TaskCompletionSource<string[]> pending) in client.Pending)
            {
                if (client.Pending.TryRemove(id, out _))
                {
                    pending.TrySetException(new IOException("profiler control channel disconnected"));
                }
            }
            try { socket.Dispose(); } catch { /* ignore */ }
        }
    }

    private int Dispatch(Client client, int pid, string payload)
    {
        string[] fields = payload.Split('\t');
        switch (fields[0])
        {
            case "HELLO":
                client.Features = fields.Length > 2
                    ? fields[2].Split(',', StringSplitOptions.RemoveEmptyEntries)
                    : [];
                pid = fields.Length > 3 && int.TryParse(fields[3], out int p) ? p : 0;
                if (pid > 0)
                {
                    _clients[pid] = client;
                }
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

    private static async Task SendAsync(Socket client, string payload, CancellationToken cancellationToken)
    {
        byte[] body = Encoding.UTF8.GetBytes(payload);
        if (body.Length > MaxFrameBytes)
        {
            throw new InvalidDataException($"control frame exceeds {MaxFrameBytes} bytes");
        }
        var framed = new byte[4 + body.Length];
        framed[0] = (byte)body.Length;
        framed[1] = (byte)(body.Length >> 8);
        framed[2] = (byte)(body.Length >> 16);
        framed[3] = (byte)(body.Length >> 24);
        Buffer.BlockCopy(body, 0, framed, 4, body.Length);
        int sent = 0;
        while (sent < framed.Length)
        {
            int n = await client.SendAsync(framed.AsMemory(sent), SocketFlags.None, cancellationToken);
            if (n == 0)
            {
                throw new IOException("profiler control channel closed while sending");
            }
            sent += n;
        }
    }

    private static bool TryReadFrame(List<byte> buffer, out string payload)
    {
        payload = string.Empty;
        if (buffer.Count < 4)
        {
            return false;
        }

        int len = buffer[0] | (buffer[1] << 8) | (buffer[2] << 16) | (buffer[3] << 24);
        if (len < 0 || len > MaxFrameBytes)
        {
            throw new InvalidDataException("invalid profiler control frame length");
        }
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
            client.SendLock.Dispose();
        }
        try { _listener.Dispose(); } catch { /* ignore */ }
        try { if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        } catch { /* ignore */ }
    }
}
