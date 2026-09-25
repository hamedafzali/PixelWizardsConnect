using System;
using System.Threading;
using System.Threading.Tasks;
using PixelWizard.Core.Interfaces;
using PixelWizard.Protocol;

namespace PixelWizard.Session;

/// <summary>
/// Host-side listen/relisten mechanics (formerly <c>MainViewModel.RelistenHostAsync</c> and
/// its <c>_relistening</c> guard, moved here in T9.3b). One listener per hosting run; each
/// accepted connection gets its own fresh <see cref="HostSession"/> built from an injected
/// transport factory, so Hello/handshake state never carries over between viewers and this
/// project never references a concrete transport.
///
/// Mechanics only, not policy: <em>whether</em> to relisten after a disconnect still depends
/// on UI-bound state (<c>IsHostRunning</c>) and runs on the UI thread, so the caller decides
/// and calls <see cref="RelistenAsync"/>. Per-session event wiring is the caller's too, via
/// <see cref="SessionCreated"/>, raised before the session starts listening so no event from
/// the new connection can be missed.
/// </summary>
public sealed class HostListener
{
    private readonly Func<ISessionTransport> _transportFactory;
    private readonly HelloMessage _ourHello;
    private readonly string _expectedSessionSecret;
    private readonly int _port;
    // Read on every listen, not once: MainViewModel's TLS checkbox stays editable while
    // hosting, and pre-T9.3b each relisten read HostTlsEnabled afresh.
    private readonly Func<bool> _useTls;
    private int _relistening;
    private volatile bool _stopped;

    public HostListener(Func<ISessionTransport> transportFactory, HelloMessage ourHello,
                        int port, Func<bool> useTls, string expectedSessionSecret = "")
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _ourHello = ourHello ?? throw new ArgumentNullException(nameof(ourHello));
        _port = port;
        _useTls = useTls ?? throw new ArgumentNullException(nameof(useTls));
        _expectedSessionSecret = expectedSessionSecret;
    }

    /// <summary>The session currently listening or connected; null once stopped.</summary>
    public HostSession? Current { get; private set; }

    public bool IsRelistening => Volatile.Read(ref _relistening) != 0;

    public event Action<HostSession>? SessionCreated;

    /// <summary>
    /// First listen. Like <see cref="ISessionTransport.StartServerAsync"/>, completes only
    /// once a viewer connects (or the listen fails).
    /// </summary>
    public Task StartAsync() => ListenAsync();

    /// <summary>
    /// Disposes the current session and listens again with a fresh one. Returns false without
    /// doing anything if a relisten is already in flight or the listener has been stopped.
    /// Exceptions from building/starting the new session propagate to the caller.
    /// </summary>
    public async Task<bool> RelistenAsync()
    {
        if (_stopped) return false;
        if (Interlocked.CompareExchange(ref _relistening, 1, 0) != 0) return false;
        try
        {
            // Null out first so the Disconnected event fired by Dispose sees no current
            // session (matches the pre-T9.3b ordering in MainViewModel).
            var old = Current;
            Current = null;
            old?.Dispose();

            await ListenAsync();
            return true;
        }
        finally
        {
            Volatile.Write(ref _relistening, 0);
        }
    }

    /// <summary>
    /// Disconnects the current session -- including one still waiting to accept, which the
    /// transport cancels -- and makes every later <see cref="RelistenAsync"/> a no-op.
    /// </summary>
    public void Stop()
    {
        _stopped = true;
        Current?.Disconnect();
        Current = null;
    }

    private async Task ListenAsync()
    {
        var session = new HostSession(_transportFactory(), _ourHello, _expectedSessionSecret);
        Current = session;
        SessionCreated?.Invoke(session);
        await session.StartServerAsync(_port, _useTls());
    }
}
