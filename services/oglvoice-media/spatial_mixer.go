// SPDX-License-Identifier: MPL-2.0
package main

import (
	"context"
	"errors"
	"math"
	"sync"
	"time"

	"github.com/hraban/opus"
	"github.com/pion/webrtc/v4/pkg/media"
)

const (
	audioRate      = 48000
	audioChannels  = 2
	audioFrame     = 960 // 20 ms per channel
	audioSamples   = audioFrame * audioChannels
	maxSources     = 64
	maxAudible     = 24
	maxDistance    = 40.0
	audioFrameTime = 20 * time.Millisecond
)

type voicePosition struct {
	X, Y, Z float64
	Heading float64 // viewer-forward heading in radians, 0 east
	Valid   bool
}
func validPosition(p voicePosition) bool {
	return p.Valid && isFinite(p.X) && isFinite(p.Y) &&
		isFinite(p.Z) && isFinite(p.Heading) &&
		p.X >= -4096 && p.X <= 4096 && p.Y >= -4096 &&
		p.Y <= 4096 && p.Z >= -4096 && p.Z <= 8192
}
func isFinite(n float64) bool { return !math.IsNaN(n) && !math.IsInf(n, 0) }

type audioSource struct {
	frames chan []float32 // bounded low-latency jitter FIFO
	pos    voicePosition
}
type spatialMixer struct {
	mu       sync.RWMutex
	listener voicePosition
	sources  map[string]*audioSource
	encoder  *opus.Encoder // accessed by single mix/encode loop only
}

// Each Firestorm viewer gets a separate mix: no audio from other tenants,
// and the listener itself is never included in its own returned audio.
func newSpatialMixer() (*spatialMixer, error) {
	encoder, err := opus.NewEncoder(audioRate, audioChannels, opus.AppVoIP)
	if err != nil { return nil, err }
	if err = encoder.SetBitrate(48000); err != nil { return nil, err }
	return &spatialMixer{sources: make(map[string]*audioSource), encoder: encoder}, nil
}
func (m *spatialMixer) setListener(pos voicePosition) {
	if !validPosition(pos) { return }
	m.mu.Lock()
	m.listener = pos
	m.mu.Unlock()
}
func (m *spatialMixer) addSource(identity string, pos voicePosition) bool {
	if identity == "" { return false }
	m.mu.Lock()
	defer m.mu.Unlock()
	if len(m.sources) >= maxSources { return false }
	if _, ok := m.sources[identity]; ok { return false }
	m.sources[identity] = &audioSource{frames: make(chan []float32, 5), pos: pos}
	return true
}
func (m *spatialMixer) updateSource(identity string, pos voicePosition) {
	if !validPosition(pos) { return }
	m.mu.Lock()
	if source := m.sources[identity]; source != nil { source.pos = pos }
	m.mu.Unlock()
}
func (m *spatialMixer) removeSource(identity string) {
	m.mu.Lock()
	delete(m.sources, identity)
	m.mu.Unlock()
}
func (m *spatialMixer) queueFrame(identity string, pcm []float32) {
	if len(pcm) != audioSamples { return }
	m.mu.RLock()
	source := m.sources[identity]
	if source == nil { m.mu.RUnlock(); return }
	frame := append([]float32(nil), pcm...)
	select {
	case source.frames <- frame:
	default:
		// Drop the oldest frame rather than building unbounded latency.
		select { case <-source.frames: default: }
		select { case source.frames <- frame: default: }
	}
	m.mu.RUnlock()
}

type spatialGains struct{ left, right float64 }
func gains(listener, speaker voicePosition) spatialGains {
	if !validPosition(listener) || !validPosition(speaker) {
		return spatialGains{} // unknown location cannot bypass spatial policy
	}
	dx, dy, dz := speaker.X-listener.X, speaker.Y-listener.Y, speaker.Z-listener.Z
	distance := math.Sqrt(dx*dx+dy*dy+dz*dz)
	if distance >= maxDistance { return spatialGains{} }
	// 1 m is full volume; radius and exponential curve are service policies.
	attenuation := 1.0 / (1.0 + math.Pow(math.Max(0, distance-1)/9, 1.7))
	// Equal-power stereo panning relative to listener orientation.
	angle := math.Atan2(dy, dx) - listener.Heading
	pan := math.Sin(angle)
	left := math.Sqrt((1-pan)/2) * math.Sqrt2
	right := math.Sqrt((1+pan)/2) * math.Sqrt2
	return spatialGains{left: attenuation*left, right: attenuation*right}
}

func (m *spatialMixer) mixFrame() []float32 {
	// Mixer lock protects positions and session membership. Frame channels are
	// bounded; never wait for remote speakers to send a packet.
	m.mu.RLock()
	defer m.mu.RUnlock()
	out := make([]float32, audioSamples)
	type audible struct {
		src *audioSource
		gain spatialGains
	}
	active := make([]audible, 0, len(m.sources))
	for _, source := range m.sources {
		g := gains(m.listener, source.pos)
		if g.left+g.right > 0 {
			active = append(active, audible{src:source, gain:g})
		}
	}
	// Hard maximum work per listener/frame, even for Unlimited tenants.
	// Prefer strongest/nearest sources; a call with >24 simultaneous
	// speakers should use hierarchy mixing, not unlimited CPU work.
	if len(active) > maxAudible {
		for i:=0;i<maxAudible;i++ {
			best:=i
			for j:=i+1;j<len(active);j++ {
				if active[j].gain.left+active[j].gain.right >
					active[best].gain.left+active[best].gain.right {best=j}
			}
			active[i],active[best]=active[best],active[i]
		}
		active=active[:maxAudible]
	}
	for _, current := range active {
		var pcm []float32
		select { case pcm = <-current.src.frames: default: continue }
		for j:=0;j<audioSamples;j+=2 {
			// Mix each decoded source's stereo signal to mono first, then
			// position it in the listener's stereo soundstage.
			sample:=float64(pcm[j]+pcm[j+1])*0.5
			out[j] += float32(sample*current.gain.left)
			out[j+1] += float32(sample*current.gain.right)
		}
	}
	// Smooth limiter to avoid hard clipping when many people speak at once.
	for i,v:=range out {
		if v > 1 || v < -1 {
			out[i]=float32(math.Tanh(float64(v)))
		}
	}
	return out
}
func (m *spatialMixer) encodeFrame(frame []float32) ([]byte,error) {
	if len(frame)!=audioSamples {return nil,errors.New("invalid stereo PCM frame")}
	buffer:=make([]byte, 4000)
	n,err:=m.encoder.EncodeFloat32(frame,buffer)
	if err!=nil {return nil,err}
	return buffer[:n],nil
}
func (m *spatialMixer) stream(ctx context.Context, send func(media.Sample) error) {
	tick:=time.NewTicker(audioFrameTime)
	defer tick.Stop()
	for {
		select {
		case <-ctx.Done():return
		case <-tick.C:
			audio:=m.mixFrame()
			payload,err:=m.encodeFrame(audio)
			if err!=nil {continue}
			if err=send(media.Sample{Data:payload,Duration:audioFrameTime});err!=nil {return}
		}
	}
}

// Every subscribed remote Opus RTP track has its OWN decoder state. One
// packet may carry >20ms of audio; split into 20ms chunks for the mixer.
// Out-of-order RTP and loss concealment are still improvement areas.
func (m *spatialMixer) consumeOpus(identity string, read func() ([]byte,error)) {
	decoder,err:=opus.NewDecoder(audioRate, audioChannels)
	if err!=nil {return}
	pending:=make([]float32,0,audioSamples*3)
	for {
		packet,e:=read()
		if e!=nil {return}
		if len(packet)==0 {continue}
		pcm:=make([]float32,5760*audioChannels) // Opus maximum 120ms packet
		n,e:=decoder.DecodeFloat32(packet,pcm)
		if e!=nil || n<=0 || n>5760 {continue}
		pending=append(pending,pcm[:n*audioChannels]...)
		for len(pending)>=audioSamples {
			m.queueFrame(identity,pending[:audioSamples])
			pending=pending[audioSamples:]
		}
	}
}
