
// tests/LabelScope.Core.Tests/ZplListenerTests.cs
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LabelScope.Core.Listening;
using Xunit;

namespace LabelScope.Core.Tests;

public sealed class ZplListenerTests
{
    private static async Task Send(int port, params string[] chunks)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        foreach (var chunk in chunks)
        {
            var bytes = Encoding.UTF8.GetBytes(chunk);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
            await Task.Delay(30); // make the chunks arrive separately
        }
    }

    private static async Task<List<ReceivedLabel>> WaitFor(BlockingCollection<ReceivedLabel> q, int count)
    {
        var got = new List<ReceivedLabel>();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (got.Count < count && DateTime.UtcNow < deadline)
        {
            if (q.TryTake(out var item, 100)) got.Add(item);
            await Task.Yield();
        }
        return got;
    }

    [Fact]
    public async Task TwoLabelsInChunks_AreRaisedSeparately_AndMarkedComplete()
    {
        using var listener = new ZplListener(IPAddress.Loopback, 0);
        var q = new BlockingCollection<ReceivedLabel>();
        listener.LabelReceived += q.Add;
        listener.Start();

        await Send(listener.LocalPort, "^XA^FDone^FS^X", "Z^XA^FDtwo^FS^XZ");

        var got = await WaitFor(q, 2);
        Assert.Equal(2, got.Count);
        Assert.All(got, l => Assert.True(l.Complete));
        Assert.Contains("one", got[0].Zpl);
        Assert.Contains("two", got[1].Zpl);
    }

    [Fact]
    public async Task ConnectionClosedWithoutXz_RaisesIncompleteLabel()
    {
        using var listener = new ZplListener(IPAddress.Loopback, 0);
        var q = new BlockingCollection<ReceivedLabel>();
        listener.LabelReceived += q.Add;
        listener.Start();

        await Send(listener.LocalPort, "^XA^FDcut off^FS");

        var got = await WaitFor(q, 1);
        var label = Assert.Single(got);
        Assert.False(label.Complete);
        Assert.Contains("cut off", label.Zpl);
    }

    [Fact]
    public void PortAlreadyInUse_ThrowsPlainLanguageException()
    {
        using var first = new ZplListener(IPAddress.Loopback, 0);
        first.Start();

        using var second = new ZplListener(IPAddress.Loopback, first.LocalPort);
        var ex = Assert.Throws<ListenerStartException>(() => second.Start());

        Assert.Contains("already in use", ex.Message);
        Assert.Contains("ListenPort", ex.Message);
    }

    [Fact]
    public async Task OversizedLabel_ReportsProblem_ClosesThatConnection_AndKeepsServing()
    {
        using var listener = new ZplListener(IPAddress.Loopback, 0);
        var problem = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var q = new BlockingCollection<ReceivedLabel>();
        listener.ProblemReported += message => problem.TrySetResult(message);
        listener.LabelReceived += q.Add;
        listener.Start();

        // A little over the cap, in 1 MB chunks, and never an end marker.
        using (var bad = new TcpClient())
        {
            await bad.ConnectAsync(IPAddress.Loopback, listener.LocalPort);
            var stream = bad.GetStream();
            var chunk = new byte[1024 * 1024];
            Array.Fill(chunk, (byte)'A');
            try
            {
                for (var sent = 0; sent <= ZplStreamSplitter.MaxPendingChars + chunk.Length; sent += chunk.Length)
                    await stream.WriteAsync(chunk);
            }
            catch (IOException)
            {
                // The listener closes the connection once it has seen enough; a failed write after that is expected.
            }

            var finished = await Task.WhenAny(problem.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.Same(problem.Task, finished);
            Assert.Contains("16 MB", await problem.Task);
        }

        // The listener must still accept and parse a normal label from a new connection.
        await Send(listener.LocalPort, "^XA^FDafter^FS^XZ");
        var got = await WaitFor(q, 1);
        var label = Assert.Single(got);
        Assert.Contains("after", label.Zpl);
        Assert.True(label.Complete);
    }

    [Fact]
    public async Task ThrowingSubscribers_DoNotStopTheListener()
    {
        using var listener = new ZplListener(IPAddress.Loopback, 0);
        var q = new BlockingCollection<ReceivedLabel>();
        // The throwing handler is registered first, so the working one only runs if the failure is contained.
        listener.LabelReceived += _ => throw new InvalidOperationException("subscriber bug");
        listener.LabelReceived += q.Add;
        listener.ProblemReported += _ => throw new InvalidOperationException("subscriber bug");
        listener.Start();

        await Send(listener.LocalPort, "^XA^FDone^FS^XZ");
        await Send(listener.LocalPort, "^XA^FDtwo^FS^XZ");

        var got = await WaitFor(q, 2);
        Assert.Equal(2, got.Count);
    }

    [Fact]
    public async Task AcceptLoop_SurvivesAFailedAccept_ReportsIt_AndKeepsServing()
    {
        using var listener = new ZplListener(IPAddress.Loopback, 0);
        var q = new BlockingCollection<ReceivedLabel>();
        var problems = new BlockingCollection<string>();
        listener.LabelReceived += q.Add;
        listener.ProblemReported += problems.Add;

        var real = new TcpListener(IPAddress.Loopback, 0);
        real.Start();
        using var cts = new CancellationTokenSource();
        var calls = 0;
        // The first accept fails the way a client reset does; later ones are real.
        var loop = listener.RunAcceptLoopAsync(ct =>
            Interlocked.Increment(ref calls) == 1
                ? throw new SocketException((int)SocketError.ConnectionReset)
                : real.AcceptTcpClientAsync(ct).AsTask(), cts.Token);

        await Send(((IPEndPoint)real.LocalEndpoint).Port, "^XA^FDsurvived^FS^XZ");

        var got = await WaitFor(q, 1);
        Assert.Contains("survived", Assert.Single(got).Zpl);
        Assert.True(problems.TryTake(out var message, 5000));
        Assert.Contains("still listening", message);

        cts.Cancel();
        real.Stop();
        await loop.WaitAsync(TimeSpan.FromSeconds(5)); // the loop must end cleanly on shutdown
    }

    [Fact]
    public async Task ClientResetBeforeSendingAnything_DoesNotStopTheListener()
    {
        using var listener = new ZplListener(IPAddress.Loopback, 0);
        var q = new BlockingCollection<ReceivedLabel>();
        listener.LabelReceived += q.Add;
        listener.Start();

        // LingerState 0 makes Close send a TCP reset instead of a normal FIN.
        using (var rude = new TcpClient { LingerState = new LingerOption(true, 0) })
            await rude.ConnectAsync(IPAddress.Loopback, listener.LocalPort);

        await Send(listener.LocalPort, "^XA^FDnormal^FS^XZ");
        var got = await WaitFor(q, 1);
        Assert.Contains("normal", Assert.Single(got).Zpl);
    }
}
