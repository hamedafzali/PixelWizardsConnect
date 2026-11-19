using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PixelWizard.Signaling
{
    /// <summary>TURN credentials the router issues at pairing time (docs/SIGNALING.md).</summary>
    public sealed record TurnInfo(IReadOnlyList<string> Urls, string Username, string Credential);

    /// <summary>One <c>/signal</c> message: a <c>{"v":1,"type":...}</c> envelope plus per-type fields.</summary>
    public abstract record SignalingMessage
    {
        public abstract string Type { get; }
    }

    // Pairing: host -> router
    public sealed record RegisterMessage : SignalingMessage { public override string Type => "register"; }
    public sealed record ResumeMessage(string ResumeToken) : SignalingMessage { public override string Type => "resume"; }
    // Pairing: viewer -> router
    public sealed record JoinMessage(string Code) : SignalingMessage { public override string Type => "join"; }
    // Pairing: router -> peer
    public sealed record RegisteredMessage(string Code, string ResumeToken) : SignalingMessage { public override string Type => "registered"; }
    public sealed record ResumedMessage(string Code) : SignalingMessage { public override string Type => "resumed"; }
    public sealed record JoinedMessage(TurnInfo? Turn) : SignalingMessage { public override string Type => "joined"; }
    public sealed record PeerJoinedMessage(TurnInfo? Turn) : SignalingMessage { public override string Type => "peer-joined"; }
    public sealed record PeerLeftMessage : SignalingMessage { public override string Type => "peer-left"; }
    public sealed record ErrorMessage(string Code, string Message) : SignalingMessage { public override string Type => "error"; }

    // Relayed between the paired peers
    public sealed record OfferMessage(string Sdp) : SignalingMessage { public override string Type => "offer"; }
    public sealed record AnswerMessage(string Sdp) : SignalingMessage { public override string Type => "answer"; }
    /// <summary><c>SdpMid</c> and <c>SdpMLineIndex</c> are always written, as JSON null when unset.</summary>
    public sealed record CandidateMessage(string Candidate, string? SdpMid, int? SdpMLineIndex) : SignalingMessage { public override string Type => "candidate"; }
    public sealed record EndOfCandidatesMessage : SignalingMessage { public override string Type => "end-of-candidates"; }
    public sealed record ByeMessage(string? Reason = null) : SignalingMessage { public override string Type => "bye"; }

    /// <summary>The router answered with an <c>error</c> message (always followed by a close).</summary>
    public sealed class SignalingErrorException : Exception
    {
        /// <summary>Protocol error code, e.g. "code-unknown", "code-used", "resume-rejected".</summary>
        public string Code { get; }
        public SignalingErrorException(string code, string message) : base($"{code}: {message}") => Code = code;
    }

    /// <summary>JSON mapping for <see cref="SignalingMessage"/>.</summary>
    public static class SignalingJson
    {
        public const int Version = 1;

        public static string Serialize(SignalingMessage msg)
        {
            var o = new JsonObject { ["v"] = Version, ["type"] = msg.Type };
            switch (msg)
            {
                case ResumeMessage m: o["resumeToken"] = m.ResumeToken; break;
                case JoinMessage m: o["code"] = m.Code; break;
                case RegisteredMessage m: o["code"] = m.Code; o["resumeToken"] = m.ResumeToken; break;
                case ResumedMessage m: o["code"] = m.Code; break;
                case JoinedMessage m: if (m.Turn != null) o["turn"] = TurnToJson(m.Turn); break;
                case PeerJoinedMessage m: if (m.Turn != null) o["turn"] = TurnToJson(m.Turn); break;
                case ErrorMessage m: o["code"] = m.Code; o["message"] = m.Message; break;
                case OfferMessage m: o["sdp"] = m.Sdp; break;
                case AnswerMessage m: o["sdp"] = m.Sdp; break;
                case CandidateMessage m:
                    o["candidate"] = m.Candidate;
                    o["sdpMid"] = m.SdpMid;               // present even when null
                    o["sdpMLineIndex"] = m.SdpMLineIndex; // present even when null
                    break;
                case ByeMessage m: if (m.Reason != null) o["reason"] = m.Reason; break;
            }
            return o.ToJsonString();
        }

        /// <summary>
        /// Parses one frame. Returns null for a well-formed envelope this build does not know
        /// (or a version other than 1), which callers ignore. Throws <see cref="InvalidDataException"/>
        /// for malformed JSON or a known type with missing fields.
        /// </summary>
        public static SignalingMessage? Parse(string json)
        {
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(json);
                root = doc.RootElement.Clone();
            }
            catch (JsonException e) { throw new InvalidDataException("Signaling frame is not JSON.", e); }

            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Signaling frame is not an object.");
            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int ver))
                throw new InvalidDataException("Signaling frame has no integer 'v'.");
            if (ver != Version) return null;
            string type = Str(root, "type");

            switch (type)
            {
                case "register": return new RegisterMessage();
                case "resume": return new ResumeMessage(Str(root, "resumeToken"));
                case "join": return new JoinMessage(Str(root, "code"));
                case "registered": return new RegisteredMessage(Str(root, "code"), Str(root, "resumeToken"));
                case "resumed": return new ResumedMessage(Str(root, "code"));
                case "joined": return new JoinedMessage(Turn(root));
                case "peer-joined": return new PeerJoinedMessage(Turn(root));
                case "peer-left": return new PeerLeftMessage();
                case "error": return new ErrorMessage(Str(root, "code"), OptStr(root, "message") ?? "");
                case "offer": return new OfferMessage(Str(root, "sdp"));
                case "answer": return new AnswerMessage(Str(root, "sdp"));
                case "candidate":
                    return new CandidateMessage(Str(root, "candidate"), OptStr(root, "sdpMid"),
                        root.TryGetProperty("sdpMLineIndex", out var idx) && idx.ValueKind == JsonValueKind.Number && idx.TryGetInt32(out int i) ? i : null);
                case "end-of-candidates": return new EndOfCandidatesMessage();
                case "bye": return new ByeMessage(OptStr(root, "reason"));
                default: return null;
            }
        }

        private static string Str(JsonElement e, string name) =>
            OptStr(e, name) ?? throw new InvalidDataException($"Signaling frame is missing '{name}'.");

        private static string? OptStr(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        private static TurnInfo? Turn(JsonElement e)
        {
            if (!e.TryGetProperty("turn", out var t) || t.ValueKind != JsonValueKind.Object) return null;
            if (!t.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Signaling 'turn' has no 'urls'.");
            return new TurnInfo(
                urls.EnumerateArray().Where(u => u.ValueKind == JsonValueKind.String).Select(u => u.GetString()!).ToList(),
                Str(t, "username"), Str(t, "credential"));
        }

        private static JsonObject TurnToJson(TurnInfo t) => new()
        {
            ["urls"] = new JsonArray(t.Urls.Select(u => (JsonNode?)JsonValue.Create(u)).ToArray()),
            ["username"] = t.Username,
            ["credential"] = t.Credential,
        };
    }
}
