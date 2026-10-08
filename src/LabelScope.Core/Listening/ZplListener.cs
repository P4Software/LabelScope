
// src/LabelScope.Core/Listening/ZplListener.cs
using System.Net;
using System.Net.Sockets;

namespace LabelScope.Core.Listening;

/// <summary>A label (or the unfinished start of one) received from a sender.</summary>
/// <param name="Zpl">The raw ZPL text.</param>
/// <param name="ReceivedAt">When the label finished arriving.</param>
/// <param name="Source">Address of the sender, for the history list.</param>
/// <param name="Complete">False when the sender disconnected before sending <c>^XZ</c>.</param>
public sealed record ReceivedLabel(string Zpl, DateTimeOffset ReceivedAt, string Source, bool Complete);

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

    /// <summary>Creates a listener; nothing is bound until <see cref="Start"/>.</summary>
    public ZplListener(IPAddress address, int port)
    {
        _address = address;
        _port = port;
    }

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
                $"Port {_port} is already in use by another program (possibly another copy of LabelScope). " +
                "Close that program, or choose a different number for ListenPort in settings.json and start LabelScope again.", ex);
        }
        catch (SocketException ex)
        {
            throw new ListenerStartException(
                $"LabelScope could not start listening on {_address}:{_port} ({ex.Message}). " +
                "Check ListenAddress and ListenPort in settings.json.", ex);
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
                    "LabelScope had trouble accepting a connection and is still listening. If labels stop arriving, restart LabelScope.");
                try
                {
                    // Short pause so a persistent fault cannot spin the CPU.
                    await Task.Delay(100, ct);
                }
                catch (OperationCanceledException) { return; }
                continue;
            }

            // Each sender is handled on its own task so one slow sender cannot block others.
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var source = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
            var splitter = new ZplStreamSplitter();
            var buffer = new byte[8192];
            try
            {
                var stream = client.GetStream();
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    foreach (var zpl in splitter.Feed(buffer, read))
                        Raise(LabelReceived, new ReceivedLabel(zpl, DateTimeOffset.Now, source, true));
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
                Raise(LabelReceived, new ReceivedLabel(rest, DateTimeOffset.Now, source, false));
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
