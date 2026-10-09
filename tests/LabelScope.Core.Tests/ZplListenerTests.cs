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
    public async Task TooManyConnections_AreRefused_ReportedOnce_AndServingResumesWhenOneLeaves()
    {
        using var listener = new ZplListener(IPAddress.Loopback, 0) { MaxConnections = 2 };
        var q = new BlockingCollection<ReceivedLabel>();
        var problems = new BlockingCollection<string>();
        listener.LabelReceived += q.Add;
        listener.ProblemReported += problems.Add;
        listener.Start();

        // Two idle senders fill the table.
        using var first = new TcpClient();
        using var second = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, listener.LocalPort);
        await second.ConnectAsync(IPAddress.Loopback, listener.LocalPort);
        await Task.Delay(300); // let the listener count both

        // Two more are turned away: the listener closes them, so a read ends at once (or is reset).
        for (var i = 0; i < 2; i++)
        {
            using var refused = new TcpClient();
            await refused.ConnectAsync(IPAddress.Loopback, listener.LocalPort);
            var buffer = new byte[1];
            var read = 0;
            try { read = await refused.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (IOException) { /* a reset also means "closed on us" */ }
            Assert.Equal(0, read);
        }

        // Both refusals fall into the same 10 second window, so only one message is raised.
        Assert.True(problems.TryTake(out var message, 5000));
        Assert.Contains("too many programs", message);
        Assert.False(problems.TryTake(out _, 300));

        // When a sender leaves, a new one is served again.
        first.Dispose();
        await Task.Delay(300);
        await Send(listener.LocalPort, "^XA^FDagain^FS^XZ");
        Assert.Contains("again", Assert.Single(await WaitFor(q, 1)).Zpl);
    }

    [Fact]
    public async Task IdleConnection_IsClosed_AndWhatArrivedIsFlushedAsIncomplete()
    {
        using var listener = new ZplListener(IPAddress.Loopback, 0) { IdleTimeout = TimeSpan.FromMilliseconds(300) };
        var q = new BlockingCollection<ReceivedLabel>();
        listener.LabelReceived += q.Add;
        listener.Start();

        // The sender writes half a label and then stays connected without sending anything.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.LocalPort);
        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("^XA^FDstuck^FS"));

        var got = await WaitFor(q, 1);
        var label = Assert.Single(got);
        Assert.False(label.Complete);
        Assert.Contains("stuck", label.Zpl);

        // The listener has closed its side, so the client sees the end of the stream.
        var read = await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task SlowHandling_DoesNotCountAsTheSenderBeingIdle()
    {
        // Handling a label can take a long time under load (waiting for the render gate). That time must not
        // count against the sender: an active sender must not be cut off and must not lose its next label.
        using var listener = new ZplListener(IPAddress.Loopback, 0) { IdleTimeout = TimeSpan.FromMilliseconds(300) };
        var q = new BlockingCollection<ReceivedLabel>();
        var first = true;
        listener.LabelReceived += label =>
        {
            if (first)
            {
                first = false;
                Thread.Sleep(900); // three times the idle timeout, spent inside the subscriber
            }
            q.Add(label);
        };
        listener.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.LocalPort);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("^XA^FDone^FS^XZ"));
        await Task.Delay(100); // the first label is now being handled (slowly)
        await stream.WriteAsync(Encoding.UTF8.GetBytes("^XA^FDtwo^FS^XZ"));

        var got = await WaitFor(q, 2);
        Assert.Equal(2, got.Count);
        Assert.All(got, l => Assert.True(l.Complete));
        Assert.Contains("two", got[1].Zpl);
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
