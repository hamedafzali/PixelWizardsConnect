// Minimal host process for the T7 Playwright smoke test (see ../drive.mjs). Not a test
// project itself -- xunit can't drive a headless-browser page, so this is a small standalone
// process the Node driver spawns and talks to over stdout, following the pattern already
// proven in spikes/webrtc-desktop/web-peer/drive.mjs.
//
// It starts a real WebSocketHostServer (the class T7 extracted), waits for the browser
// viewer to connect over /stream, and broadcasts one synthetic frame. This deliberately does
// not spin up a real IScreenCapture/PixelWizard.WindowsHost or LinuxHost pipeline: the risk
// this test guards against (per PHASE2-PLAN.md's Risk table) is the WebSocket wire protocol
// and the embedded viewer's JS decode path breaking silently, not screen capture itself --
// which already has its own coverage (the detector fixture suite) and would need a real
// display (Xvfb) in CI that doesn't exist here.
using System.Net.Sockets;
using PixelWizard.Transport.WebSocket;
using SkiaSharp;

int port = FindFreePort();

using var server = new WebSocketHostServer(port);
var viewerConnected = new TaskCompletionSource();
server.Log += msg =>
{
    Console.WriteLine($"[host] {msg}");
    if (msg.StartsWith("Web viewer connected", StringComparison.Ordinal))
        viewerConnected.TrySetResult();
};
server.Start();

// The driver waits for exactly this line to know which port to connect to.
Console.WriteLine($"READY {port}");
Console.Out.Flush();

try
{
    await viewerConnected.Task.WaitAsync(TimeSpan.FromSeconds(20));
}
catch (TimeoutException)
{
    Console.WriteLine("ERROR no viewer connected within 20s");
    return 1;
}

await server.BroadcastFrameAsync(MakeSyntheticJpeg());
Console.WriteLine("SENT frame");
Console.Out.Flush();

// Stay alive long enough for the driver to observe the render, then exit on its own kill.
await Task.Delay(TimeSpan.FromSeconds(15));
return 0;

static int FindFreePort()
{
    var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static byte[] MakeSyntheticJpeg()
{
    using var bitmap = new SKBitmap(4, 4);
    using (var canvas = new SKCanvas(bitmap))
        canvas.Clear(new SKColor(220, 40, 40));
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
    return data.ToArray();
}
