// SPDX-License-Identifier: MPL-2.0
package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestSignedSpatialUpdatesBoundToNodeAndSession(t *testing.T) {
	cfg:=testConfig()
	g:=newMediaServer(cfg)
	now:=time.Unix(1791568800,0)
	g.now=func()time.Time{return now}
	a:=sampleAdmission("nexverse")
	a.PositionValid=true
	a.PositionX=12
	a.PositionY=15
	a.PositionZ=25
	mix,err:=newSpatialMixer()
	if err!=nil {t.Fatal(err)}
	peer:=&peerState{id:"media-session",node:"sim-freiburg",
		admission:a,mixer:mix,lastSeen:time.Now()}
	peer.touch()
	g.sessions[sessionKey("sim-freiburg",a)]=peer
	send:=func(update admission,session,nonce string) *httptest.ResponseRecorder {
		t.Helper()
		wire:=exchange{Protocol:wireProtocol,Operation:"position",
			Admission:update,ViewerSession:session}
		data,_:=json.Marshal(wire)
		request:=signedRequest(t,exchangeRoute,"sim-freiburg",
			cfg.Nodes["sim-freiburg"],data,now,nonce)
		out:=httptest.NewRecorder()
		g.exchange(out,request)
		return out
	}
	result:=send(a,"media-session",strings.Repeat("b",32))
	if result.Code!=http.StatusOK {
		t.Fatalf("signed spatial update failed: %d: %s",result.Code,result.Body)
	}
	if mix.listener.X!=12||mix.listener.Y!=15 {t.Fatal("mixer missed current verified location")}
	a.PositionX=35
	a.Heading=1.1
	result=send(a,"wrong-media-session",strings.Repeat("c",32))
	if result.Code!=http.StatusForbidden {t.Fatal("stolen viewer_session accepted")}
	if mix.listener.X!=12 {t.Fatal("invalid token mutated position")}
	result=send(a,"media-session",strings.Repeat("d",32))
	if result.Code!=http.StatusOK||mix.listener.X!=35 {
		t.Fatal("real-time position update not applied")
	}
	a.PositionValid=false
	result=send(a,"media-session",strings.Repeat("e",32))
	if result.Code!=http.StatusBadRequest {t.Fatal("invalid position accepted")}
}
