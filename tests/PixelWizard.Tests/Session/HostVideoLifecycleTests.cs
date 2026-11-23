using System;
using PixelWizard.Core.Interfaces;
using PixelWizard.Protocol;
using PixelWizard.Session;
using Xunit;

namespace PixelWizard.Tests.Session;

/// <summary>
/// P4.8b: the H.264 pipeline's lifecycle inside HostSession, with no FFmpeg or WebRTC. The pipeline is
/// built once per approval, and a pipeline that ends on its own hands the media path back to JPEG.
/// </summary>
public class HostVideoLifecycleTests
{
    private static readonly HelloMessage H264Hello = new()
    {
        ProtocolVersion = ProtocolVersions.Current,
        Role = PeerRole.Full,
        Codecs = SupportedCodecs.Jpeg | SupportedCodecs.H264,
        MaxConcurrentStreams = 1
    };

    private sealed class FakeVideoTransport : FakeSessionTransport, IVideoTrackTransport
    {
        public bool VideoTrackNegotiated => true;
        public void SendVideoAccessUnit(byte[] annexB, uint durationRtpUnits) { }
        public event Action<byte[], uint>? VideoAccessUnitReceived { add { } remove { } }
        public void RequestKeyframe() { }
        public event Action? KeyframeRequested { add { } remove { } }
    }

    private sealed class FakePipeline : IHostVideoPipeline
    {
        public bool Running;
        public int Disposals;
        public bool TryStart(out string reason) { reason = ""; Running = true; return true; }
        public bool IsRunning => Running;
        public void Stop() => Running = false;
        public void Dispose() { Disposals++; Running = false; }
    }

    private static (HostSession session, FakeVideoTransport fake) VerifiedH264()
    {
        var fake = new FakeVideoTransport { IsConnected = true };
        var session = new HostSession(fake, H264Hello);
        fake.RaiseMessageReceived(new NetworkMessage { Type = MessageType.Hello, Data = H264Hello.Serialize() });
        fake.RaiseMessageReceived(new NetworkMessage { Type = MessageType.Handshake, Data = Array.Empty<byte>() });
        Assert.Equal(MessageType.HandshakeOk, fake.LastSentMessage!.Type);
        Assert.True(session.UseH264Video);
        return (session, fake);
    }

    [Fact]
    public void SecondApprove_DoesNotBuildASecondPipeline()
    {
        var (session, _) = VerifiedH264();
        var built = 0;
        var pipeline = new FakePipeline();
        session.VideoPipelineFactory = () => { built++; return pipeline; };

        session.Approve();
        session.Approve();

        Assert.Equal(1, built);
        Assert.True(session.VideoActive);
        Assert.Equal(0, pipeline.Disposals);
    }

    [Fact]
    public void PipelineThatEndsOnItsOwn_HandsMediaBackToJpeg()
    {
        var (session, _) = VerifiedH264();
        var pipeline = new FakePipeline();
        session.VideoPipelineFactory = () => pipeline;
        session.Approve();
        Assert.True(session.VideoActive);

        pipeline.Running = false; // e.g. the encoder failed to re-open after a resolution change

        Assert.False(session.VideoActive);
        Assert.True(session.IsApproved);
    }

    [Fact]
    public void Disconnect_DisposesThePipeline()
    {
        var (session, fake) = VerifiedH264();
        var pipeline = new FakePipeline();
        session.VideoPipelineFactory = () => pipeline;
        session.Approve();

        fake.RaiseDisconnected();

        Assert.Equal(1, pipeline.Disposals);
        Assert.False(session.VideoActive);
    }
}
