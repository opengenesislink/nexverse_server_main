// SPDX-License-Identifier: MPL-2.0
package main

import (
	"encoding/json"
	"io"
	"strconv"
	"math"
	"testing"

	"github.com/hraban/opus"
)

func voicePos(x,y,z,heading float64) voicePosition {
	return voicePosition{X:x,Y:y,Z:z,Heading:heading,Valid:true}
}
func TestSpatialMultiSpeakerMixAndRange(t *testing.T) {
	m,err:=newSpatialMixer()
	if err!=nil { t.Fatal(err) }
	m.setListener(voicePos(0,0,20,0))
	if !m.addSource("alice",voicePos(1,0,20,0)) ||
		!m.addSource("bob",voicePos(1,0,20,0)) {
		t.Fatal("two simultaneous speakers not admitted")
	}
	frame:=make([]float32,audioSamples)
	for i:=range frame {frame[i]=0.15}
	m.queueFrame("alice",frame)
	m.queueFrame("bob",frame)
	mixed:=m.mixFrame()
	if mixed[0]<=0.15||mixed[1]<=0.15 {
		t.Fatalf("multiple simultaneous speakers were not summed: %f / %f",mixed[0],mixed[1])
	}
	payload,err:=m.encodeFrame(mixed)
	if err!=nil||len(payload)==0 {t.Fatalf("mixed Opus not encoded: %v",err)}
	decoder,err:=opus.NewDecoder(audioRate,audioChannels)
	if err!=nil {t.Fatal(err)}
	decoded:=make([]float32,5760*audioChannels)
	n,err:=decoder.DecodeFloat32(payload,decoded)
	if err!=nil||n!=audioFrame {t.Fatalf("mixed stream could not be decoded: n=%d %v",n,err)}

	m.updateSource("alice",voicePos(120,120,20,0))
	m.updateSource("bob",voicePos(120,120,20,0))
	m.queueFrame("alice",frame)
	m.queueFrame("bob",frame)
	far:=m.mixFrame()
	for _,v:=range far {if v!=0 {t.Fatal("voice leaked beyond audible distance")}}
	m.removeSource("bob")
	m.removeSource("alice")
	if len(m.sources)!=0 {t.Fatal("speaker cleanup")}
}

func TestSpatialStereoOrientation(t *testing.T) {
	center:=voicePos(5,5,20,0)
	onLeft:=voicePos(5,7,20,0)
	l:=gains(center,onLeft)
	if l.left<=l.right {t.Fatalf("speaker north of listener should pan left: %+v",l)}
	center.Heading=math.Pi
	r:=gains(center,onLeft)
	if r.right<=r.left {t.Fatalf("turning around should move speaker to right: %+v",r)}
	near:=gains(center,voicePos(6,5,20,0))
	distant:=gains(center,voicePos(38,5,20,0))
	if near.left+near.right <= distant.left+distant.right {t.Fatal("distance attenuation not applied")}
	if gains(voicePosition{},voicePos(1,0,20,0)).left!=0 {
		t.Fatal("unknown listener coordinates must remain inaudible")
	}
	if gains(center,voicePosition{}).left!=0 {
		t.Fatal("unknown speaker location must remain inaudible")
	}
}

func TestSpatialJitterBoundAndLimiting(t *testing.T) {
	m,err:=newSpatialMixer();if err!=nil {t.Fatal(err)}
	m.setListener(voicePos(0,0,20,0))
	for i:=0;i<maxSources;i++ {
		if !m.addSource("speaker-"+strconv.Itoa(i),voicePos(1,0,20,0)) {
			t.Fatal("failed to admit speaker under cap")
		}
	}
	if m.addSource("too-many",voicePos(1,0,20,0)) {t.Fatal("unbounded source count")}
	pcm:=make([]float32,audioSamples)
	for i:=range pcm {pcm[i]=0.9}
	for source:=range m.sources {
		for i:=0;i<20;i++ {m.queueFrame(source,pcm)}
		if len(m.sources[source].frames)>5 {t.Fatal("unbounded audio jitter queue")}
	}
	out:=m.mixFrame()
	if out[0]>1||out[0]<-1||out[1]>1||out[1]<-1 {
		t.Fatalf("sample limiter failed: %v",out[:2])
	}
}

func TestSpatialOpusPacketSplit(t *testing.T) {
	m,err:=newSpatialMixer();if err!=nil {t.Fatal(err)}
	m.addSource("two-frames",voicePos(1,1,20,0))
	enc,err:=opus.NewEncoder(audioRate,audioChannels,opus.AppVoIP)
	if err!=nil {t.Fatal(err)}
	pcm:=make([]float32,audioSamples*2) // 40ms
	for i:=range pcm {pcm[i]=float32(0.1*math.Sin(float64(i)*0.02))}
	buf:=make([]byte,4000)
	n,err:=enc.EncodeFloat32(pcm,buf)
	if err!=nil {t.Fatal(err)}
	count:=0
	m.consumeOpus("two-frames",func()([]byte,error){
		if count>0 {return nil,io.EOF}
		count++
		return buf[:n],nil
	})
	if got:=len(m.sources["two-frames"].frames);got!=2 {
		t.Fatalf("40ms Opus packet must split into two 20ms frames, got %d",got)
	}
}

func TestPositionsStayWithinTrustedRoom(t *testing.T) {
	local:=sampleAdmission("nexverse")
	local.PositionValid=true
	local.PositionX=12
	local.PositionY=17
	local.PositionZ=22
	local.Heading=.5
	if !validPosition(admissionPosition(local)) {t.Fatal("verified position rejected")}
	meta,err:=json.Marshal(participantSpatialMetadata(local))
	if err!=nil {t.Fatal(err)}
	pos:=parseParticipantPosition(string(meta))
	if pos.X!=12||pos.Y!=17||!pos.Valid {
		t.Fatalf("room metadata coordinates: %+v",pos)
	}
	if parseParticipantPosition(`{"avatar":"someone"}`).Valid {
		t.Fatal("unknown room metadata must remain silent")
	}
	local.PositionX=math.NaN()
	if validPosition(admissionPosition(local)) {t.Fatal("NaN position accepted")}
}
