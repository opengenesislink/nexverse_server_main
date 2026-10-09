// SPDX-License-Identifier: MPL-2.0
package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	lksdk "github.com/livekit/server-sdk-go/v2"
	"github.com/pion/webrtc/v4"
)

type peerState struct {
	id        string
	node      string
	admission admission
	pc        *webrtc.PeerConnection
	room      *lksdk.Room
	downlink  *webrtc.TrackLocalStaticRTP
	downlinkBusy atomic.Bool
	lastSeen  time.Time
	lastUnix  atomic.Int64
	closeOnce sync.Once
	onClose   func()

	channelMu sync.RWMutex
	channel   *webrtc.DataChannel
}

func participantID(a admission) string {
	sum := sha256.Sum256([]byte(a.TenantID + "\n" + strings.ToLower(a.RegionID) +
		"\n" + strings.ToLower(a.HomeGridOrigin) +
		"\n" + strings.ToLower(a.AvatarID) + "\n" + strings.ToLower(a.SessionID)))
	return "ogl:" + hex.EncodeToString(sum[:])
}
func roomID(a admission) string {
	return "ogl." + a.TenantID + ".region." +
		strings.ReplaceAll(strings.ToLower(a.RegionID), "-", "")
}

func startPeer(cfg config, a admission, id string, onClose func()) (*peerState, error) {
	p := &peerState{id: id, admission: a, onClose: onClose, lastSeen: time.Now()}
	p.touch()

	var iceServers []webrtc.ICEServer
	for _, url := range strings.Split(strings.TrimSpace(getenv("OGLVOICE_WEBRTC_STUN_URLS", "stun:stun.l.google.com:19302")), ",") {
		if trimmed := strings.TrimSpace(url); trimmed != "" {
			iceServers = append(iceServers, webrtc.ICEServer{URLs: []string{trimmed}})
		}
	}
	pc, err := webrtc.NewPeerConnection(webrtc.Configuration{ICEServers: iceServers})
	if err != nil { return nil, fmt.Errorf("Pion: %w", err) }
	p.pc = pc

	// One downlink transceiver matches Firestorm's unmodified audio offer.
	// Multi-speaker *mixed* downlink is not implemented: at most one remote
	// LiveKit Opus track is forwarded. This is NOT full spatial audio.
	p.downlink, err = webrtc.NewTrackLocalStaticRTP(
		webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeOpus,
			ClockRate: 48000, Channels: 2}, "oglvoice-remote", "oglvoice")
	if err != nil { _ = pc.Close(); return nil, err }
	sender, err := pc.AddTrack(p.downlink)
	if err != nil { _ = pc.Close(); return nil, err }
	go func() {
		buf := make([]byte, 1500)
		for {
			if _, _, err := sender.Read(buf); err != nil { return }
		}
	}()

	pc.OnDataChannel(func(d *webrtc.DataChannel) {
		d.OnOpen(func() {
			p.channelMu.Lock()
			p.channel = d
			p.channelMu.Unlock()
			if p.room != nil {
				for _, rp := range p.room.GetRemoteParticipants() {
					p.sendRoster(rp, false, true)
				}
			}
		})
		d.OnClose(func() {
			p.channelMu.Lock()
			if p.channel == d { p.channel = nil }
			p.channelMu.Unlock()
		})
	})
	pc.OnTrack(func(track *webrtc.TrackRemote, receiver *webrtc.RTPReceiver) {
		if track.Kind() != webrtc.RTPCodecTypeAudio ||
			!strings.EqualFold(track.Codec().MimeType, webrtc.MimeTypeOpus) {
			return
		}
		go func() {
			// Publish native Opus RTP to LiveKit without transcoding. This
			// genuinely transports the viewer's microphone to the SFU.
			uplink, err := webrtc.NewTrackLocalStaticRTP(
				webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeOpus,
					ClockRate: 48000, Channels: 2}, "oglvoice-mic", "oglvoice")
			if err != nil { slog.Warn("uplink create", "error", err); return }
			if p.room == nil { return }
			if _, err = p.room.LocalParticipant.PublishTrack(uplink,
				&lksdk.TrackPublicationOptions{Name: "oglvoice-microphone"}); err != nil {
				slog.Warn("LiveKit microphone publish", "error", err)
				return
			}
			for {
				packet, _, err := track.ReadRTP()
				if err != nil { return }
				p.touch()
				if err := uplink.WriteRTP(packet); err != nil { return }
			}
		}()
	})
	pc.OnConnectionStateChange(func(s webrtc.PeerConnectionState) {
		switch s {
		case webrtc.PeerConnectionStateConnected:
			p.touch()
		case webrtc.PeerConnectionStateFailed, webrtc.PeerConnectionStateClosed:
			p.close()
		}
	})

	callback := &lksdk.RoomCallback{
		ParticipantCallback: lksdk.ParticipantCallback{
			OnTrackSubscribed: func(track *webrtc.TrackRemote,
				publication *lksdk.RemoteTrackPublication,
				rp *lksdk.RemoteParticipant) {
				if track.Kind() != webrtc.RTPCodecTypeAudio ||
					!strings.EqualFold(track.Codec().MimeType, webrtc.MimeTypeOpus) ||
					!p.downlinkBusy.CompareAndSwap(false, true) {
					return
				}
				go func() {
					defer p.downlinkBusy.Store(false)
					for {
						pkt, _, err := track.ReadRTP()
						if err != nil { return }
						if err := p.downlink.WriteRTP(pkt); err != nil { return }
					}
				}()
			},
			OnIsSpeakingChanged: func(participant lksdk.Participant) {
				p.sendVoiceEvent(participant)
			},
		},
		OnParticipantConnected: func(rp *lksdk.RemoteParticipant) {
			p.sendRoster(rp, false, true)
		},
		OnParticipantDisconnected: func(rp *lksdk.RemoteParticipant) {
			p.sendRoster(rp, true, false)
		},
		OnDisconnected: func() {
			p.close()
		},
	}

	// Participant metadata is sourced from the *authenticated simulator*,
	// never from a viewer-provided room ID. All LiveKit participant secrets
	// stay inside this media service.
	meta, _ := json.Marshal(map[string]string{"avatar": strings.ToLower(a.AvatarID)})
	room, err := lksdk.ConnectToRoom(cfg.LiveKitURL, lksdk.ConnectInfo{
		APIKey: cfg.LiveKitKey, APISecret: cfg.LiveKitSecret,
		RoomName: roomID(a), ParticipantIdentity: participantID(a),
		ParticipantName: a.AvatarID, ParticipantMetadata: string(meta),
	}, callback, lksdk.WithConnectTimeout(10*time.Second))
	if err != nil {
		_ = pc.Close()
		return nil, fmt.Errorf("LiveKit room connect: %w", err)
	}
	p.room = room
	return p, nil
}

func (p *peerState) acceptOffer(sdp string) error {
	if p.pc == nil { return errors.New("WebRTC transport unavailable") }
	if err := p.pc.SetRemoteDescription(webrtc.SessionDescription{
		Type: webrtc.SDPTypeOffer, SDP: sdp,
	}); err != nil { return err }

	answer, err := p.pc.CreateAnswer(nil)
	if err != nil { return err }
	gather := webrtc.GatheringCompletePromise(p.pc)
	if err := p.pc.SetLocalDescription(answer); err != nil { return err }
	// Server candidate trickle is not implemented in the current Firestorm
	// CAPS bridge, so wait for gathering; otherwise answers may be unusable.
	select {
	case <-gather:
	case <-time.After(12 * time.Second):
		return errors.New("WebRTC ICE gathering timed out")
	}
	p.touch()
	return nil
}

func (p *peerState) answerSDP() string {
	if p.pc == nil || p.pc.LocalDescription() == nil { return "" }
	return p.pc.LocalDescription().SDP
}
func (p *peerState) addCandidates(candidates []iceCandidate, done bool) error {
	if p.pc == nil { return errors.New("closed peer") }
	for _, c := range candidates {
		mid := c.SDPMid
		index := uint16(c.SDPMLineIndex)
		if err := p.pc.AddICECandidate(webrtc.ICECandidateInit{
			Candidate: c.Candidate, SDPMid: &mid, SDPMLineIndex: &index,
		}); err != nil { return err }
	}
	// Trickle ICE end-of-candidates is informational. Candidates are already
	// attached to the remote SDP or added above.
	p.touch()
	return nil
}
func (p *peerState) touch() { p.lastUnix.Store(time.Now().Unix()) }
func (p *peerState) lastActivity() time.Time {
	return time.Unix(p.lastUnix.Load(), 0)
}
func (p *peerState) close() {
	p.closeOnce.Do(func() {
		// Trigger shutdown on a separate goroutine to avoid deadlocking a
		// Pion PeerConnectionState callback that itself holds locks.
		go func() {
			if p.room != nil { p.room.Disconnect() }
			if p.pc != nil { _ = p.pc.Close() }
			if p.onClose != nil { p.onClose() }
		}()
	})
}

func avatarFromMetadata(meta string) string {
	var fields struct { Avatar string `json:"avatar"` }
	if err := json.Unmarshal([]byte(meta), &fields); err != nil ||
		!validUUID.MatchString(fields.Avatar) { return "" }
	return strings.ToLower(fields.Avatar)
}
func (p *peerState) transmit(data interface{}) {
	p.channelMu.RLock()
	channel := p.channel
	p.channelMu.RUnlock()
	if channel == nil || channel.ReadyState() != webrtc.DataChannelStateOpen {
		return
	}
	b, err := json.Marshal(data)
	if err == nil && len(b) <= 4096 {
		_ = channel.SendText(string(b))
	}
}
func (p *peerState) sendRoster(remote *lksdk.RemoteParticipant, leaving, joined bool) {
	avatar := avatarFromMetadata(remote.Metadata())
	if avatar == "" { return }
	var flags map[string]interface{}
	if leaving { flags = map[string]interface{}{"l":true} } else if joined {
		flags = map[string]interface{}{"j":map[string]bool{"p":true}}
	}
	if flags != nil {
		p.transmit(map[string]interface{}{avatar: flags})
	}
	if joined { p.sendVoiceEvent(remote) }
}
func (p *peerState) sendVoiceEvent(participant lksdk.Participant) {
	avatar := avatarFromMetadata(participant.Metadata())
	if avatar == "" { return }
	level := participant.AudioLevel()
	if level < 0 { level = 0 }
	if level > 1 { level = 1 }
	speaking := participant.IsSpeaking()
	if !speaking { level = 0 }
	p.transmit(map[string]interface{}{
		avatar: map[string]interface{}{
			"v": speaking,
			"p": int(level * 128),
		},
	})
}

func getenv(name, fallback string) string {
	if value := strings.TrimSpace(strings.TrimSpace(getEnv(name))); value != "" { return value }
	return fallback
}
var getEnv = func(s string) string { return "" } // overwritten by main at startup
