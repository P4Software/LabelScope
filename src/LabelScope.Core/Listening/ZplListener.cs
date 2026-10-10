using System.Net;
using System.Net.Sockets;

namespace LabelScope.Core.Listening;

/// <summary>A label (or the unfinished start of one) received from a sender.</summary>
/// <param name="Zpl">The raw ZPL text.</param>
/// <param name="ReceivedAt">When the label finished arriving.</param>
/// <param name="Source">Address of the sender, for the history list.</param>
/// <param name="Complete">False when the sender disconnected before sending <c>^XZ</c>.</param>
/// <param name="ConnectionId">
/// The connection the label came over, the same number for every label of one send (one print job can hold several
/// ^XA..^XZ labels), so the window can show them as one job. Numbers start at 1 and are never reused while the
/// listener runs; 0 means the label did not arrive over the network (a file or a paste).
/// </param>
public sealed record ReceivedLabel(string Zpl, DateTimeOffset ReceivedAt, string Source, bool Complete, long ConnectionId = 0);

/// <summary>Raised when the listener cannot start; the message is written for the end user.</summary>
public sealed class ListenerStartException : Exception
{
    /// <summary>Creates the exception with a plain-language message.</summary>
    public ListenerStartException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Accepts TCP connections carrying raw ZPL and raises one event per label.</summary>
public sealed class ZplListener : IDisposable
{
    private readonly IPAddress _address;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    // Number of connections being served right now; touched from many threads, so only through Interlocked.
    private int _activeConnections;

    // Environment.TickCount64 of the last "too many connections" message, 0 when none was sent yet.
    private long _lastBusyReport;

    // Last connection number handed out (see ReceivedLabel.ConnectionId); only through Interlocked.
    private long _lastConnectionId;

    /// <summary>Creates a listener; nothing is bound until <see cref="Start"/>.</summary>
    public ZplListener(IPAddress address, int port)
    {
        _address = address;
        _port = port;
    }

    /// <summary>
    /// Most clients served at the same time. A real print spooler uses one or two connections, so 16 is generous;
    /// the cap stops a misbehaving program from opening thousands of sockets and exhausting memory or threads.
    /// </summary>
    internal int MaxConnections { get; init; } = 16;

    /// <summary>
    /// A connection that sends nothing for this long is closed. Without it, a program that connects and then
    /// never writes or closes would hold one of the <see cref="MaxConnections"/> slots forever.
    /// </summary>
    internal TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Minimum time between two "too many connections" messages, so a flood does not flood the app as well.</summary>
    internal TimeSpan BusyReportInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The port actually bound (differs from the requested one only when port 0 was requested).</summary>
    public int LocalPort => ((IPEndPoint?)_listener?.LocalEndpoint)?.Port ?? _port;

    /// <summary>Raised on a background thread for every label received.</summary>
    public event Action<ReceivedLabel>? LabelReceived;

    /// <summary>
    /// Raised on a background thread when a connection had to be dropped because of a problem with
    /// what it sent (for example a label over 16 MB with no <c>^XZ</c>). The argument is a
    /// plain-language message the app can show to the operator. Core has no logger, so this is
    /// how such problems reach the app; the listener itself keeps serving other connections.
    /// </summary>
    public event Action<string>? ProblemReported;

    /// <summary>
    /// Raised on a background thread when a connection has ended, after its last label was raised. The argument is
    /// the connection number of <see cref="ReceivedLabel.ConnectionId"/>, so the app can stop adding labels to that
    /// send's job and let go of what it kept for it.
    /// </summary>
    public event Action<long>? ConnectionClosed;

    /// <summary>Starts listening. Throws <see cref="ListenerStartException"/> with advice if the port cannot be used.</summary>
    public void Start()
    {
        try
        {
            // ExclusiveAddressUse stops a second copy silently sharing the port.
            _listener = new TcpListener(_address, _port) { ExclusiveAddressUse = true };
            _listener.Start();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new ListenerStartException(
                Text.Get("Listener_PortInUse", _port), ex);
        }
        catch (SocketException ex)
        {
            throw new ListenerStartException(
                Text.Get("Listener_StartFailed", _address, _port, ex.Message), ex);
        }

        _ = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
    }

    /// <summary>Stops accepting connections.</summary>
    public void Stop()
    {
        _cts.Cancel();
        _listener?.Stop();
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    private Task AcceptLoopAsync(TcpListener listener, CancellationToken ct) =>
        RunAcceptLoopAsync(token => listener.AcceptTcpClientAsync(token).AsTask(), ct);

    /// <summary>
    /// The accept loop, with the accept call injected so tests can make it fail. It only ends on
    /// shutdown: any other failure is reported and the loop carries on, because a single bad
    /// connection attempt (for example a client that resets before being accepted) must not leave
    /// the port bound with nobody accepting.
    /// </summary>
    internal async Task RunAcceptLoopAsync(Func<CancellationToken, Task<TcpClient>> accept, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await accept(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) { return; } // the TcpListener was stopped on purpose
            catch (Exception) when (ct.IsCancellationRequested) { return; } // Stop() aborts a pending accept with a socket error
            catch (Exception)
            {
                Raise(ProblemReported,
                    Text.Get("Listener_AcceptTrouble"));
                try
                {
                    // Short pause so a persistent fault cannot spin the CPU.
                    await Task.Delay(100, ct);
                }
                catch (OperationCanceledException) { return; }
                continue;
            }

            // Count first, then decide: Increment returns the new total, so two connections arriving at the
            // same moment can never both slip in under the cap.
            if (Interlocked.Increment(ref _activeConnections) > MaxConnections)
            {
                Interlocked.Decrement(ref _activeConnections);
                client.Dispose(); // refused: closing at once tells the sender "not now" without costing us a task
                ReportBusy();
                continue;
            }

            // Each sender is handled on its own task so one slow sender cannot block others.
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    /// <summary>Tells the app that connections were refused, at most once per <see cref="BusyReportInterval"/>.</summary>
    private void ReportBusy()
    {
        var now = Math.Max(1, Environment.TickCount64); // 0 is reserved for "never reported"
        var last = Interlocked.Read(ref _lastBusyReport);
        if (last != 0 && now - last < (long)BusyReportInterval.TotalMilliseconds) return;
        // Only the thread that wins the swap reports, so concurrent refusals produce a single message.
        if (Interlocked.CompareExchange(ref _lastBusyReport, now, last) != last) return;
        Raise(ProblemReported,
            Text.Get("Listener_Busy"));
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            await ServeClientAsync(client, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections); // frees the slot whatever happened
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var source = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
            var connectionId = Interlocked.Increment(ref _lastConnectionId);
            try
            {
                var splitter = new ZplStreamSplitter();
                var buffer = new byte[8192];
                // One timer for the whole connection: CancelAfter is called again before every read, which restarts it.
                // When it fires the read is cancelled and the code below flushes whatever partial label arrived.
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                try
                {
                    var stream = client.GetStream();
                    while (true)
                    {
                        idle.CancelAfter(IdleTimeout);
                        var read = await stream.ReadAsync(buffer, idle.Token);
                        if (read <= 0) break;
                        // The sender just proved it is alive. Stop the idle timer while we process what arrived: handling
                        // can wait for the render gate for a long time under load, and that wait must not count as the
                        // sender being idle (it would cut off an active sender and flush its label as incomplete).
                        idle.CancelAfter(Timeout.InfiniteTimeSpan);
                        foreach (var zpl in splitter.Feed(buffer, read))
                            Raise(LabelReceived, new ReceivedLabel(zpl, DateTimeOffset.Now, source, true, connectionId));
                    }
                }
                catch (ZplTooLargeException ex)
                {
                    // This sender is not sending real labels: tell the app and drop only this connection.
                    // The splitter already discarded the oversized text, so there is nothing left to flush.
                    Raise(ProblemReported, ex.Message);
                    return;
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
                {
                    // A sender that drops the connection is normal; fall through and flush what arrived.
                }

                // The spooler can cut a job off; show what we got instead of silently losing it.
                var rest = splitter.Flush();
                if (rest is not null)
                    Raise(LabelReceived, new ReceivedLabel(rest, DateTimeOffset.Now, source, false, connectionId));
            }
            finally
            {
                // Every label of this connection has been handed over by now (LabelReceived runs synchronously).
                Raise(ConnectionClosed, connectionId);
            }
        }
    }

    /// <summary>
    /// Calls every subscriber separately and swallows their exceptions. A bug in app code that
    /// handles an event must never kill the connection handler (losing the sender's remaining
    /// labels) or the accept loop (the whole listener going deaf), and one faulty subscriber must
    /// not stop the others from being told. There is no logger in Core to report it to.
    /// </summary>
    private static void Raise<T>(Action<T>? handlers, T argument)
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try { handler(argument); }
            catch { /* subscriber failure is the subscriber's problem; see summary above */ }
        }
    }
}
