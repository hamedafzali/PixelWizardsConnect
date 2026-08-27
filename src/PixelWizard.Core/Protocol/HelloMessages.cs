using System;
using System.IO;
using System.Text;

namespace PixelWizard.Core.Protocol
{
    /// <summary>Current v2 wire-protocol version, exchanged in Hello. Bump on any change to
    /// message layout that isn't itself skippable via the unknown-type envelope.</summary>
    public static class ProtocolVersions
    {
        public const byte Current = 2;
    }

    /// <summary>
    /// What a peer can be sent. Full is today's only implementation (desktop, IInputInjector
    /// always present). ShareOnly exists so a peer that cannot inject input -- concretely, a
    /// phone with no mouse/keyboard, which the current always-present IInputInjector model
    /// can't express -- can say so up front, instead of a viewer discovering it by sending
    /// input messages into the void. Nothing in this codebase produces ShareOnly yet
    /// (that's the Phase 6 Flutter client); this only makes it representable and respected.
    /// </summary>
    public enum PeerRole : byte
    {
        Full = 1,
        ShareOnly = 2
    }

    public static class PeerRoleExtensions
    {
        public static bool AcceptsInput(this PeerRole role) => role == PeerRole.Full;
    }

    /// <summary>Codecs a peer can decode. Flags so future codecs add a bit, not a wire
    /// change. Only Jpeg exists today.</summary>
    [Flags]
    public enum SupportedCodecs : byte
    {
        None = 0,
        Jpeg = 1
    }

    /// <summary>
    /// Pre-handshake capability announcement, sent by the connecting viewer and answered by
    /// the host with HelloAck carrying the same shape. Fixed-width, 4 bytes -- unlike
    /// NetworkMessage/ScreenDelta/StreamFrame, nothing here is length-prefixed, so there is no
    /// truncatable-length field to bound-check: BinaryReader.ReadByte already throws
    /// EndOfStreamException on a short buffer, which is exactly the malformed-input behavior
    /// we want (throw, don't misparse) with no extra code needed.
    /// </summary>
    public class HelloMessage
    {
        public byte ProtocolVersion { get; set; }
        public PeerRole Role { get; set; }
        public SupportedCodecs Codecs { get; set; }
        public byte MaxConcurrentStreams { get; set; }

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(ProtocolVersion);
            writer.Write((byte)Role);
            writer.Write((byte)Codecs);
            writer.Write(MaxConcurrentStreams);
            return ms.ToArray();
        }

        public static HelloMessage Deserialize(byte[] buffer)
        {
            using var ms = new MemoryStream(buffer);
            using var reader = new BinaryReader(ms);
            return new HelloMessage
            {
                ProtocolVersion = reader.ReadByte(),
                Role = (PeerRole)reader.ReadByte(),
                Codecs = (SupportedCodecs)reader.ReadByte(),
                MaxConcurrentStreams = reader.ReadByte()
            };
        }
    }

    public enum HelloRejectReason : byte
    {
        VersionMismatch = 1,
        IncompatibleCapabilities = 2
    }

    /// <summary>
    /// Sent instead of HelloAck when negotiation fails, so the rejected peer gets an explicit,
    /// human-readable reason rather than a bare disconnect. Message is length-prefixed and
    /// gets the same truncation/negative-length bounds check as every other length-prefixed
    /// field in this protocol (see NetworkMessage.Deserialize).
    /// </summary>
    public class HelloRejectedMessage
    {
        public HelloRejectReason Reason { get; set; }
        public string Message { get; set; } = "";

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write((byte)Reason);
            byte[] messageBytes = Encoding.UTF8.GetBytes(Message);
            writer.Write(messageBytes.Length);
            writer.Write(messageBytes);
            return ms.ToArray();
        }

        public static HelloRejectedMessage Deserialize(byte[] buffer)
        {
            using var ms = new MemoryStream(buffer);
            using var reader = new BinaryReader(ms);
            var reason = (HelloRejectReason)reader.ReadByte();
            int length = reader.ReadInt32();

            long remaining = ms.Length - ms.Position;
            if (length < 0 || length > remaining)
                throw new InvalidDataException(
                    $"HelloRejected declared message length {length}, but only {remaining} byte(s) remain in the buffer.");

            return new HelloRejectedMessage
            {
                Reason = reason,
                Message = Encoding.UTF8.GetString(reader.ReadBytes(length))
            };
        }
    }

    /// <summary>
    /// Pure negotiation logic, factored out of dispatch code so it's unit-testable without a
    /// socket or UI dispatcher. Returns null for "accepted", or the reason to reject with.
    /// </summary>
    public static class HelloNegotiator
    {
        public static HelloRejectReason? Evaluate(HelloMessage local, HelloMessage remote)
        {
            if (remote.ProtocolVersion != local.ProtocolVersion)
                return HelloRejectReason.VersionMismatch;

            if ((remote.Codecs & local.Codecs) == SupportedCodecs.None)
                return HelloRejectReason.IncompatibleCapabilities;

            return null;
        }
    }
}
