using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sherlock.Core.Collection;
using Xunit;

namespace Sherlock.Core.Tests.Collection;

public sealed class ProfilerControlTests
{
    [Theory]
    [InlineData("")]
    [InlineData("\tSherlock.Core.Tests.dll")]
    public async Task HelloPublishesOptionalProcessIdentity(string suffix)
    {
        string directory = !OperatingSystem.IsWindows() && Directory.Exists("/tmp") ? "/tmp" : Path.GetTempPath();
        string path = Path.Combine(directory, $"sl-{Guid.NewGuid():N}.sock");
        using var control = new ProfilerControl(path);
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var connected = new TaskCompletionSource<(int Pid, string? Name)>(TaskCreationOptions.RunContinuationsAsynchronously);
        control.ClientConnected += (pid, name) => connected.TrySetResult((pid, name));
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await client.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellation);

        await SendAsync(client, $"HELLO\t0.1\tallocations,process-filtered\t4242{suffix}", cancellation);
        (int pid, string? name) = await connected.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellation);

        Assert.Equal(4242, pid);
        Assert.Equal(suffix.Length == 0 ? null : "Sherlock.Core.Tests.dll", name);
        Assert.True(control.Supports(pid, "process-filtered"));
        Assert.False(control.Supports(pid, "correlate"));
        Assert.False(control.Supports(9999, "process-filtered"));
        var disconnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        control.ClientDisconnected += process => disconnected.TrySetResult(process);
        control.Disconnect(pid);
        Assert.Equal(pid, await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellation));
    }

    [Fact]
    public async Task RoundTripsRequestsAndEvents()
    {
        string directory = !OperatingSystem.IsWindows() && Directory.Exists("/tmp") ? "/tmp" : Path.GetTempPath();
        string path = Path.Combine(directory, $"sl-{Guid.NewGuid():N}.sock");
        using var control = new ProfilerControl(path);
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await client.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellation);
        await SendAsync(client, "HELLO\t0.1\tallocations,correlate,snapshot-triggers\t4242", cancellation);

        var response = Task.Run(async () =>
        {
            string request = await ReceiveAsync(client, cancellation);
            string[] fields = request.Split('\t');
            Assert.Equal(["REQ", fields[1], "gc-count"], fields);
            await SendAsync(client, $"RES\t{fields[1]}\tok\t17", cancellation);
        }, cancellation);

        (bool ok, string[] fields) = await control.RequestAsync(4242, ProfilerControl.GcCount, TimeSpan.FromSeconds(2));
        Assert.True(ok);
        Assert.Equal(["17"], fields);
        await response;

        var received = new TaskCompletionSource<(int Pid, string[] Fields)>(TaskCreationOptions.RunContinuationsAsynchronously);
        control.EventReceived += (pid, message) => received.TrySetResult((pid, message));
        await SendAsync(client, "EVENT\tsnapshot-trigger\tthrow:MarkerException", cancellation);
        (int pid, string[] message) = await received.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellation);
        Assert.Equal(4242, pid);
        Assert.Equal(["EVENT", "snapshot-trigger", "throw:MarkerException"], message);

        var disconnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        control.ClientDisconnected += pid => disconnected.TrySetResult(pid);
        client.Shutdown(SocketShutdown.Both);
        Assert.Equal(4242, await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellation));
    }

    private static async Task SendAsync(Socket socket, string payload, CancellationToken cancellation)
    {
        byte[] body = Encoding.UTF8.GetBytes(payload);
        byte[] frame = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        int sent = 0;
        while (sent < frame.Length)
        {
            int count = await socket.SendAsync(frame.AsMemory(sent), SocketFlags.None, cancellation);
            if (count == 0)
            {
                throw new EndOfStreamException();
            }
            sent += count;
        }
    }

    private static async Task<string> ReceiveAsync(Socket socket, CancellationToken cancellation)
    {
        byte[] header = new byte[4];
        await ReceiveExactlyAsync(socket, header, cancellation);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        byte[] body = new byte[length];
        await ReceiveExactlyAsync(socket, body, cancellation);
        return Encoding.UTF8.GetString(body);
    }

    private static async Task ReceiveExactlyAsync(Socket socket, Memory<byte> destination, CancellationToken cancellation)
    {
        int received = 0;
        while (received < destination.Length)
        {
            int count = await socket.ReceiveAsync(destination[received..], SocketFlags.None, cancellation);
            if (count == 0)
            {
                throw new EndOfStreamException();
            }
            received += count;
        }
    }
}
