// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace NexVerse.Core.Voice
{
    /// <summary>
    /// Narrow, versioned JSON contract for an external OGLVoice media bridge
    /// that MUST terminate Firestorm's SDP/ICE and SCTP data channel itself.
    /// A LiveKit JWT alone cannot answer a Firestorm JSEP offer.
    /// </summary>
    public sealed class OglVoiceMediaExchange
    {
        public const string Protocol = "oglvoice-firestorm-media-v1";
        public string protocol { get; set; } = Protocol;
        public string operation { get; set; } // offer, trickle, leave
        public OglVoiceAdmission admission { get; set; }
        public string sdp_offer { get; set; }
        public string viewer_session { get; set; }
        public OglVoiceIceCandidate[] candidates { get; set; }
        public bool ice_completed { get; set; }
    }

    public sealed class OglVoiceIceCandidate
    {
        public string candidate { get; set; }
        public string sdpMid { get; set; }
        public int sdpMLineIndex { get; set; }
    }

    public sealed class OglVoiceMediaReply
    {
        public string protocol { get; set; }
        public string sdp_answer { get; set; }
        public string viewer_session { get; set; }
        public string status { get; set; }
    }

    /// <summary>
    /// The *actual* Firestorm voice visualizer data channel protocol, keyed by
    /// the avatar UUID seen by that viewer (NOT hashed LiveKit participant id).
    /// Deliver on each listener's WebRTC SCTP data channel to visualize all
    /// remote speakers. Does not itself establish a WebRTC data channel.
    /// </summary>
    public static class OglVoiceFirestormWire
    {
        public const int MaximumSdpBytes = 32_768;
        public const int MaximumCandidateBytes = 1_024;
        public const int MaximumCandidates = 32;
        public const int MaximumResponseBytes = 48_000;

        public static void ValidateOffer(string sdp)
        {
            if (string.IsNullOrWhiteSpace(sdp) ||
                Encoding.UTF8.GetByteCount(sdp) > MaximumSdpBytes ||
                !sdp.StartsWith("v=0", StringComparison.Ordinal) ||
                !sdp.Contains("m=audio", StringComparison.Ordinal))
                throw new ArgumentException("Invalid or excessive Firestorm audio SDP offer");
        }

        public static void ValidateCandidates(IReadOnlyList<OglVoiceIceCandidate> candidates,
            bool completed)
        {
            if (candidates == null || candidates.Count > MaximumCandidates ||
                (candidates.Count == 0 && !completed))
                throw new ArgumentException("Empty or oversized ICE update");
            foreach (OglVoiceIceCandidate candidate in candidates)
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.candidate) ||
                    Encoding.UTF8.GetByteCount(candidate.candidate) > MaximumCandidateBytes ||
                    candidate.sdpMLineIndex < 0 || candidate.sdpMLineIndex > 16 ||
                    candidate.sdpMid?.Length > 32)
                    throw new ArgumentException("Invalid ICE candidate");
        }

        public static void ValidateViewerSession(string session)
        {
            if (string.IsNullOrWhiteSpace(session) || session.Length > 128 ||
                !System.Text.RegularExpressions.Regex.IsMatch(session,
                    @"^[a-zA-Z0-9_.:-]+$"))
                throw new ArgumentException("Invalid media viewer_session");
        }

        public static OglVoiceMediaReply ParseAnswer(byte[] response, bool expectSdp)
        {
            if (response == null || response.Length == 0 ||
                response.Length > MaximumResponseBytes)
                throw new ArgumentException("Missing or oversized media gateway response");
            OglVoiceMediaReply reply;
            try { reply = JsonSerializer.Deserialize<OglVoiceMediaReply>(response); }
            catch (JsonException ex) { throw new ArgumentException("Invalid media gateway JSON", ex); }
            if (reply == null || reply.protocol != OglVoiceMediaExchange.Protocol)
                throw new ArgumentException("Unexpected media gateway protocol");
            if (expectSdp)
            {
                ValidateViewerSession(reply.viewer_session);
                if (string.IsNullOrWhiteSpace(reply.sdp_answer) ||
                    Encoding.UTF8.GetByteCount(reply.sdp_answer) > MaximumSdpBytes ||
                    !reply.sdp_answer.StartsWith("v=0", StringComparison.Ordinal) ||
                    !reply.sdp_answer.Contains("m=audio", StringComparison.Ordinal))
                    throw new ArgumentException("Invalid WebRTC SDP answer");
            }
            return reply;
        }

        public static string Join(Guid avatarId, bool primary = true) =>
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [RequireAvatar(avatarId)] = new { j = new { p = primary } }
            });

        public static string Speaking(Guid avatarId, float audioLevel, bool isSpeaking)
        {
            if (!float.IsFinite(audioLevel) || audioLevel < 0 || audioLevel > 1)
                throw new ArgumentOutOfRangeException(nameof(audioLevel));
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [RequireAvatar(avatarId)] = new
                {
                    p = Math.Clamp((int)Math.Round(audioLevel * 128f), 0, 128),
                    v = isSpeaking
                }
            });
        }

        public static string Leave(Guid avatarId) =>
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [RequireAvatar(avatarId)] = new { l = true }
            });

        private static string RequireAvatar(Guid avatarId)
        {
            if (avatarId == Guid.Empty)
                throw new ArgumentException("An avatar UUID is required");
            return avatarId.ToString("D");
        }
    }
}
