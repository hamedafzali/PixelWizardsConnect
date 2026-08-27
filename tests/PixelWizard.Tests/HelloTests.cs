using System;
using System.IO;
using PixelWizard.Core.Protocol;
using Xunit;

namespace PixelWizard.Tests;

/// <summary>
/// Coverage for the Hello/capability negotiation types: HelloMessage/HelloRejectedMessage
/// round-trips and malformed-input handling, HelloNegotiator's pure accept/reject logic
/// (matching versions, mismatched versions, absent capabilities), and PeerRole.AcceptsInput.
/// </summary>
public class HelloTests
{
    [Fact]
    public void HelloMessage_RoundTrips()
    {
        var hello = new HelloMessage
        {
            ProtocolVersion = ProtocolVersions.Current,
            Role = PeerRole.ShareOnly,
            Codecs = SupportedCodecs.Jpeg,
            MaxConcurrentStreams = 3
        };

        var restored = HelloMessage.Deserialize(hello.Serialize());

        Assert.Equal(hello.ProtocolVersion, restored.ProtocolVersion);
        Assert.Equal(hello.Role, restored.Role);
        Assert.Equal(hello.Codecs, restored.Codecs);
        Assert.Equal(hello.MaxConcurrentStreams, restored.MaxConcurrentStreams);
    }

    [Fact]
    public void HelloMessage_AbsentCapabilities_RoundTripsWithoutCrashing()
    {
        var hello = new HelloMessage
        {
            ProtocolVersion = ProtocolVersions.Current,
            Role = PeerRole.Full,
            Codecs = SupportedCodecs.None,
            MaxConcurrentStreams = 0
        };

        var restored = HelloMessage.Deserialize(hello.Serialize());

        Assert.Equal(SupportedCodecs.None, restored.Codecs);
        Assert.Equal(0, restored.MaxConcurrentStreams);
    }

    [Fact]
    public void HelloMessage_Deserialize_TruncatedBuffer_Throws()
    {
        Assert.ThrowsAny<Exception>(() => HelloMessage.Deserialize(new byte[] { 1, 2 }));
    }

    [Fact]
    public void HelloMessage_Deserialize_EmptyBuffer_Throws()
    {
        Assert.ThrowsAny<Exception>(() => HelloMessage.Deserialize(Array.Empty<byte>()));
    }

    [Fact]
    public void HelloRejectedMessage_RoundTrips()
    {
        var rejected = new HelloRejectedMessage { Reason = HelloRejectReason.VersionMismatch, Message = "v3 vs v2" };

        var restored = HelloRejectedMessage.Deserialize(rejected.Serialize());

        Assert.Equal(HelloRejectReason.VersionMismatch, restored.Reason);
        Assert.Equal("v3 vs v2", restored.Message);
    }

    [Fact]
    public void HelloRejectedMessage_Deserialize_LengthFieldLiesLonger_ThrowsInsteadOfTruncatingSilently()
    {
        byte[] malformed = BuildRejectedFrame((byte)HelloRejectReason.IncompatibleCapabilities,
            declaredLength: 500, actualData: new byte[] { 1, 2, 3 });

        Assert.Throws<InvalidDataException>(() => HelloRejectedMessage.Deserialize(malformed));
    }

    [Fact]
    public void HelloRejectedMessage_Deserialize_LengthFieldNegative_Throws()
    {
        byte[] malformed = BuildRejectedFrame((byte)HelloRejectReason.VersionMismatch,
            declaredLength: -1, actualData: Array.Empty<byte>());

        Assert.ThrowsAny<Exception>(() => HelloRejectedMessage.Deserialize(malformed));
    }

    [Fact]
    public void HelloRejectedMessage_Deserialize_EmptyBuffer_Throws()
    {
        Assert.ThrowsAny<Exception>(() => HelloRejectedMessage.Deserialize(Array.Empty<byte>()));
    }

    [Fact]
    public void Negotiator_MatchingVersionsAndCodecs_Accepted()
    {
        var local = new HelloMessage { ProtocolVersion = 2, Role = PeerRole.Full, Codecs = SupportedCodecs.Jpeg, MaxConcurrentStreams = 1 };
        var remote = new HelloMessage { ProtocolVersion = 2, Role = PeerRole.ShareOnly, Codecs = SupportedCodecs.Jpeg, MaxConcurrentStreams = 1 };

        Assert.Null(HelloNegotiator.Evaluate(local, remote));
    }

    [Fact]
    public void Negotiator_MismatchedVersion_RejectsWithVersionMismatch()
    {
        var local = new HelloMessage { ProtocolVersion = 2, Codecs = SupportedCodecs.Jpeg };
        var remote = new HelloMessage { ProtocolVersion = 3, Codecs = SupportedCodecs.Jpeg };

        Assert.Equal(HelloRejectReason.VersionMismatch, HelloNegotiator.Evaluate(local, remote));
    }

    [Fact]
    public void Negotiator_AbsentCapabilities_RejectsWithIncompatibleCapabilities()
    {
        var local = new HelloMessage { ProtocolVersion = 2, Codecs = SupportedCodecs.Jpeg };
        var remote = new HelloMessage { ProtocolVersion = 2, Codecs = SupportedCodecs.None };

        Assert.Equal(HelloRejectReason.IncompatibleCapabilities, HelloNegotiator.Evaluate(local, remote));
    }

    [Theory]
    [InlineData(PeerRole.Full, true)]
    [InlineData(PeerRole.ShareOnly, false)]
    public void PeerRole_AcceptsInput_ReflectsRole(PeerRole role, bool expected)
    {
        Assert.Equal(expected, role.AcceptsInput());
    }

    private static byte[] BuildRejectedFrame(byte reason, int declaredLength, byte[] actualData)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(reason);
        writer.Write(declaredLength);
        writer.Write(actualData);
        return ms.ToArray();
    }
}
