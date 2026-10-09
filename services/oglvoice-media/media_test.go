// SPDX-License-Identifier: MPL-2.0
package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/pion/webrtc/v4"
)

func generateFirestormOffer(t *testing.T) (*webrtc.PeerConnection, string) {
	t.Helper()
	pc, err := webrtc.NewPeerConnection(webrtc.Configuration{})
	if err != nil { t.Fatal(err) }
	mic, err := webrtc.NewTrackLocalStaticRTP(
		webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeOpus,
			ClockRate: 48000, Channels:2}, "mic", "oglvoice-ci")
	if err != nil { t.Fatal(err) }
	sender, err := pc.AddTrack(mic)
	if err != nil { t.Fatal(err) }
	go func() {
		buf:=make([]byte,1500)
		for { if _,_,err:=sender.Read(buf);err!=nil{return} }
	}()
	_, err = pc.CreateDataChannel("oglvoice", nil)
	if err != nil { t.Fatal(err) }
	offer, err := pc.CreateOffer(nil)
	if err != nil { t.Fatal(err) }
	ready := webrtc.GatheringCompletePromise(pc)
	if err = pc.SetLocalDescription(offer); err != nil { t.Fatal(err) }
	select {
	case <-ready:
	case <-time.After(7*time.Second): t.Fatal("Pion test offer gathering timed out")
	}
	return pc, pc.LocalDescription().SDP
}

// This is a real Pion SDP/ICE integration test. LiveKit connectivity is
// replaced by a media factory, so the protocol can be regression-tested
// without exposing production credentials or relying on external services.
func TestFirestormSDPOfferAnswerAndTrickle(t *testing.T) {
	cfg:=testConfig()
	now:=time.Unix(1791568800,0)
	g:=newMediaServer(cfg)
	g.now=func() time.Time {return now}
	g.newPeer=func(c config, a admission, id string, onClose func()) (*peerState,error) {
		pc, err:=webrtc.NewPeerConnection(webrtc.Configuration{})
		if err!=nil{return nil,err}
		out, err:=webrtc.NewTrackLocalStaticRTP(
			webrtc.RTPCodecCapability{MimeType:webrtc.MimeTypeOpus,
				ClockRate:48000,Channels:2},"downlink","oglvoice")
		if err!=nil { _=pc.Close();return nil,err }
		if _,err=pc.AddTrack(out); err!=nil {_=pc.Close();return nil,err}
		p:=&peerState{pc:pc,id:id,admission:a,lastSeen:time.Now(),onClose:onClose}
		p.touch()
		return p,nil
	}
	client,offer:=generateFirestormOffer(t)
	defer client.Close()
	request:=exchange{Protocol:wireProtocol,Operation:"offer",
		Admission:sampleAdmission("nexverse"),SDPOffer:offer}
	body,_:=json.Marshal(request)
	req:=signedRequest(t,exchangeRoute,"sim-freiburg",
		cfg.Nodes["sim-freiburg"],body,now,sampleNonce())
	rec:=httptest.NewRecorder()
	g.exchange(rec,req)
	if rec.Code!=http.StatusOK {
		t.Fatalf("Pion JSEP negotiation failed: %d %s",rec.Code,rec.Body.String())
	}
	var result reply
	if err:=json.Unmarshal(rec.Body.Bytes(),&result);err!=nil {t.Fatal(err)}
	if result.Protocol!=wireProtocol||result.ViewerSession==""||
		!strings.Contains(result.SDPAnswer,"m=audio") {
		t.Fatalf("invalid Firestorm answer: %+v",result)
	}
	if err:=client.SetRemoteDescription(webrtc.SessionDescription{
		Type:webrtc.SDPTypeAnswer, SDP:result.SDPAnswer,
	});err!=nil { t.Fatal(err) }

	req2:=exchange{Protocol:wireProtocol,Operation:"trickle",
		Admission:request.Admission,ViewerSession:result.ViewerSession,
		ICECompleted:true}
	body2,_:=json.Marshal(req2)
	rec=httptest.NewRecorder()
	g.exchange(rec,signedRequest(t,exchangeRoute,"sim-freiburg",
		cfg.Nodes["sim-freiburg"],body2,now,strings.Repeat("b",32)))
	if rec.Code!=http.StatusOK {t.Fatalf("ICE completion rejected %d",rec.Code)}

	req2.Operation="leave"
	body2,_=json.Marshal(req2)
	rec=httptest.NewRecorder()
	g.exchange(rec,signedRequest(t,exchangeRoute,"sim-freiburg",
		cfg.Nodes["sim-freiburg"],body2,now,strings.Repeat("c",32)))
	if rec.Code!=http.StatusOK {t.Fatalf("voice logout rejected: %d",rec.Code)}
}
