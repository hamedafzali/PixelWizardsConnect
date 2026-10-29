using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using PixelWizard.Protocol;
using PixelWizard.Session;
using PixelWizard.Transport.Tcp;
using Xunit;

namespace PixelWizard.Tests.Session;

/// <summary>
/// Consent gate over real <see cref="TcpTransport"/> sockets: a viewer that has passed the
/// handshake but not yet been approved by the host must receive no screen and must not be
/// able to drive the host's input -- otherwise it can click the host's own Allow button.
/// Absence is checked deterministically: the sender disconnects right after sending, and
/// TCP delivers (and the receive loop dispatches) everything before the disconnect.
/// </summary>
public class HostApprovalEndToEndTests
{
    private static readonly HelloMessage OurHello = new()
    {
        ProtocolVersion = ProtocolVersions.Current,
        Role = PeerRole.Full,
        Codecs = SupportedCodecs.Jpeg,
        MaxConcurrentStreams = 1
    };

    private static readonly ScreenDelta Frame = new()
    {
        X = 0, Y = 0, Width = 1, Height = 1, ImageData = new byte[] { 1, 2, 3 }
    };

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitAsync(TaskCompletionSource tcs, string what)
    {
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (completed != tcs.Task)
            throw new TimeoutException($"Timed out waiting for: {what}");
    }

    private static async Task<(HostSession host, ViewerSession viewer)> ConnectVerifiedAsync()
    {
        int port = GetFreePort();
        var host = new HostSession(new TcpTransport(), OurHello);
        var verified = new TaskCompletionSource();
        host.HandshakeVerified += () => verified.TrySetResult();
        _ = host.StartServerAsync(port, useTls: false);
        await Task.Delay(50); // give the listener a moment to bind before connecting

        var viewer = new ViewerSession(() => new TcpTransport(), OurHello);
        await viewer.ConnectAsync("127.0.0.1", port, useTls: false);
        await WaitAsync(verified, "host to reach HandshakeVerified");
        return (host, viewer);
    }

    [Fact]
    public async Task BeforeApproval_ViewerReceivesNoFrame()
    {
        var (host, viewer) = await ConnectVerifiedAsync();
        using var _h = host;
        using var _v = viewer;
        bool frameReceived = false;
        viewer.FullScreenReceived += _ => frameReceived = true;
        viewer.ScreenDeltaReceived += _ => frameReceived = true;
        var viewerDisconnected = new TaskCompletionSource();
        viewer.Disconnected += () => viewerDisconnected.TrySetResult();

        await host.SendFrameAsync(Frame, full: true);
        await host.SendFrameAsync(Frame, full: false);
        host.Disconnect();
        await WaitAsync(viewerDisconnected, "viewer to see the host disconnect");

        Assert.False(frameReceived, "viewer received a frame before the host approved it");
    }

    [Fact]
    public async Task BeforeApproval_ViewerInputIsNotRaisedOnHost()
    {
        var (host, viewer) = await ConnectVerifiedAsync();
        using var _h = host;
        using var _v = viewer;
        bool inputRaised = false;
        host.MouseMoveReceived += _ => inputRaised = true;
        host.MouseClickReceived += _ => inputRaised = true;
        host.MouseButtonDownReceived += _ => inputRaised = true;
        host.MouseButtonUpReceived += _ => inputRaised = true;
        host.KeyPressReceived += _ => inputRaised = true;
        host.KeyReleaseReceived += _ => inputRaised = true;
        host.ClipboardReceived += _ => inputRaised = true;
        var hostDisconnected = new TaskCompletionSource();
        host.Disconnected += () => hostDisconnected.TrySetResult();

        await viewer.SendMouseMoveAsync(10, 10);
        await viewer.SendMouseDownAsync(10, 10, leftButton: true);
        await viewer.SendMouseUpAsync(10, 10, leftButton: true);
        await viewer.SendMouseClickAsync(10, 10, leftButton: true);
        await viewer.SendKeyAsync(0x0D, isDown: true);
        await viewer.SendKeyAsync(0x0D, isDown: false);
        await viewer.SendClipboardAsync("x");
        viewer.Disconnect();
        await WaitAsync(hostDisconnected, "host to see the viewer disconnect");

        Assert.False(inputRaised, "host raised viewer input before approving the session");
    }

    [Fact]
    public async Task AfterApproval_FramesAndInputFlow()
    {
        var (host, viewer) = await ConnectVerifiedAsync();
        using var _h = host;
        using var _v = viewer;
        var frame = new TaskCompletionSource();
        var moved = new TaskCompletionSource();
        viewer.FullScreenReceived += _ => frame.TrySetResult();
        host.MouseMoveReceived += _ => moved.TrySetResult();

        host.Approve();
        await host.SendFrameAsync(Frame, full: true);
        await viewer.SendMouseMoveAsync(10, 10);

        await WaitAsync(frame, "viewer to receive the frame after approval");
        await WaitAsync(moved, "host to raise the mouse move after approval");
        Assert.True(host.IsApproved);
    }
}
