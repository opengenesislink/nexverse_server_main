// SPDX-License-Identifier: MPL-2.0
package main

import (
	"bytes"
	"fmt"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

)

const ciKey = "only-for-ci-01234567890123456789012345678900"
const sampleRegion = "90201fff-0000-4000-8000-111111111111"
const sampleAvatar = "1c9a72aa-0000-4000-8000-222222222222"
const sampleSession = "429ede51-0000-4000-8000-333333333333"

func testConfig() config {
	return config{
		Nodes: map[string]nodeConfig{
			"sim-freiburg": {Secret: ciKey, Tenant: "nexverse"},
			"external-1": {Secret: ciKey, Tenant: "partner"},
		},
		Tenants: map[string]tenantConfig{
			"nexverse": {MaxSessions: 0, MaxRegions: 0}, // Unlimited is *server provisioned*.
			"partner": {MaxSessions: 1, MaxRegions: 1},
		},
		LiveKitURL: "ws://127.0.0.1:7880", LiveKitKey: "unused",
		LiveKitSecret: ciKey,
	}
}

func sampleAdmission(tenant string) admission {
	return admission{TenantID: tenant, RegionID: sampleRegion, AvatarID: sampleAvatar,
		SessionID: sampleSession, VoiceAllowed: true}
}
func signedRequest(t *testing.T, path, node string, n nodeConfig, body []byte,
	now time.Time, nonce string) *http.Request {
	t.Helper()
	r := httptest.NewRequest(http.MethodPost, path, bytes.NewReader(body))
	mac, err := signature(n.Secret, node, now.Unix(), nonce, body)
	if err != nil { t.Fatal(err) }
	r.Header.Set("X-OGLVoice-Node", node)
	r.Header.Set("X-OGLVoice-Timestamp", strconvFormat(now.Unix()))
	r.Header.Set("X-OGLVoice-Nonce", nonce)
	r.Header.Set("X-OGLVoice-Signature", mac)
	return r
}
func strconvFormat(v int64) string { return fmt.Sprintf("%d", v) }
func sampleNonce() string { return strings.Repeat("a", 32) }

func TestAuthenticationReplayAndNodeTenantBinding(t *testing.T) {
	cfg := testConfig()
	g := newMediaServer(cfg)
	g.newPeer = func(config, admission, string, func()) (*peerState,error) {
		return nil, errors.New("test: LiveKit offline")
	}
	now := time.Unix(1791568800, 0)
	g.now = func() time.Time { return now }
	message, _ := json.Marshal(exchange{
		Protocol: wireProtocol, Operation: "offer", Admission: sampleAdmission("nexverse"),
		SDPOffer: "v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\n",
	})
	req := signedRequest(t, exchangeRoute, "sim-freiburg", cfg.Nodes["sim-freiburg"],
		message, now, sampleNonce())
	resp := httptest.NewRecorder()
	g.exchange(resp, req)
	if resp.Code != http.StatusServiceUnavailable { // mock LiveKit offline
		t.Fatalf("expected unavailable media, got %d: %s", resp.Code, resp.Body.String())
	}
	// Same authenticator nonce cannot be replayed even if the failed media
	// exchange never connected.
	replayReq := signedRequest(t, exchangeRoute, "sim-freiburg",
		cfg.Nodes["sim-freiburg"], message, now, sampleNonce())
	out := httptest.NewRecorder()
	g.exchange(out, replayReq)
	if out.Code != http.StatusUnauthorized { t.Fatalf("replay accepted: %d", out.Code) }

	mismatch := signedRequest(t, exchangeRoute, "external-1",
		cfg.Nodes["external-1"], message, now, strings.Repeat("b", 32))
	out = httptest.NewRecorder()
	g.exchange(out, mismatch)
	if out.Code != http.StatusForbidden { t.Fatalf("foreign tenant accepted: %d", out.Code) }

	forbidden := signedRequest(t, exchangeRoute, "unknown-node", nodeConfig{Secret:ciKey},
		message, now, strings.Repeat("c", 32))
	out = httptest.NewRecorder()
	g.exchange(out, forbidden)
	if out.Code != http.StatusForbidden { t.Fatalf("unknown node accepted: %d", out.Code) }
}
func TestSignatureTamperAndExpiry(t *testing.T) {
	rg := newReplayGuard()
	now := time.Unix(1791568800, 0)
	data := []byte(`{"protocol":"oglvoice-firestorm-media-v1"}`)
	n := nodeConfig{Secret: ciKey, Tenant: "nexverse"}
	req := signedRequest(t, exchangeRoute, "sim-freiburg", n, data, now, sampleNonce())
	if err := rg.verify("sim-freiburg", n, req, data, now); err != nil { t.Fatal(err) }
	req = signedRequest(t, exchangeRoute, "sim-freiburg", n, data, now, strings.Repeat("b", 32))
	if err := rg.verify("sim-freiburg", n, req, []byte("forgery"), now); err == nil {
		t.Fatal("body tampering accepted")
	}
	req = signedRequest(t, exchangeRoute, "sim-freiburg", n, data, now, strings.Repeat("c", 32))
	if err := rg.verify("sim-freiburg", n, req, data, now.Add(61*time.Second)); err == nil {
		t.Fatal("old request accepted")
	}
}
func TestTenantPermissionAndQuotas(t *testing.T) {
	cfg:=testConfig()
	g:=newMediaServer(cfg)
	g.sessions["first"]=&peerState{admission:sampleAdmission("partner")}
	if g.capacityAllowed(sampleAdmission("partner")) { t.Fatal("partner quota ignored") }
	if !g.capacityAllowed(sampleAdmission("nexverse")) { t.Fatal("Unlimited nexverse quota denied") }
	g.sessions=map[string]*peerState{"other": {admission: sampleAdmission("partner")}}
	foreign:=sampleAdmission("partner")
	foreign.RegionID="a0b0c0d0-0000-4000-8000-444444444444"
	cfg.Tenants["partner"]=tenantConfig{MaxSessions: 0, MaxRegions:1}
	g.cfg=cfg
	if g.capacityAllowed(foreign) { t.Fatal("partner region quota ignored") }
	if err:=validAdmission(cfg.Nodes["external-1"], sampleAdmission("nexverse")); err==nil {
		t.Fatal("partner forged NexVerse Unlimited")
	}
	guest:=sampleAdmission("nexverse")
	guest.IsHypergridGuest=true
	if err:=validAdmission(cfg.Nodes["sim-freiburg"], guest); err==nil {
		t.Fatal("guest without verified home grid accepted")
	}
}
func TestWireShapeAndSpeakingData(t *testing.T) {
	a:=sampleAdmission("nexverse")
	if roomID(a)!="ogl.nexverse.region."+strings.ReplaceAll(sampleRegion,"-","") {
		t.Fatal("room IDs differ from OpenGenesisLINK LiveKit tokens")
	}
	id1:=participantID(a)
	a.SessionID="dd949efa-0000-4000-8000-555555555555"
	if id1==participantID(a) { t.Fatal("session collision") }
	a.HomeGridOrigin="http://osgrid.example:8002"
	if id1==participantID(a) { t.Fatal("HG home collision") }
	if avatarFromMetadata(`{"avatar":"`+sampleAvatar+`"}`)!=sampleAvatar {
		t.Fatal("speaker metadata not resolved")
	}
	if avatarFromMetadata(`{"avatar":"hacker"}`)!="" {
		t.Fatal("forged viewer ID not rejected")
	}
}
func TestBoundedHTTPBody(t *testing.T) {
	r:=httptest.NewRequest(http.MethodPost, exchangeRoute,
		io.NopCloser(strings.NewReader(strings.Repeat("X",requestLimit+1))))
	w:=httptest.NewRecorder()
	if _,err:=readRequest(w,r);err==nil {t.Fatal("oversized body accepted")}
}
