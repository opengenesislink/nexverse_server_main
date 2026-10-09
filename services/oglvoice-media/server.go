// SPDX-License-Identifier: MPL-2.0
package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"strings"
	"sync"
	"time"
)

const exchangeRoute = "/internal/webrtc/v1/exchange"

type mediaServer struct {
	cfg      config
	replay   *replayGuard
	now      func() time.Time
	mu       sync.Mutex
	sessions map[string]*peerState
	newPeer  func(config, admission, string, func()) (*peerState, error)
}
func newMediaServer(cfg config) *mediaServer {
	return &mediaServer{
		cfg: cfg, replay: newReplayGuard(), now: time.Now,
		sessions: make(map[string]*peerState), newPeer: startPeer,
	}
}
func (g *mediaServer) routes() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("oglvoice-media:ready\n"))
	})
	mux.HandleFunc(exchangeRoute, g.exchange)
	return mux
}
func jsonReply(w http.ResponseWriter, code int, p reply) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("X-Content-Type-Options", "nosniff")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(p)
}

func (g *mediaServer) exchange(w http.ResponseWriter, req *http.Request) {
	if req.Method != http.MethodPost {
		jsonReply(w, http.StatusMethodNotAllowed, reply{Protocol: wireProtocol, Status: "method_not_allowed"})
		return
	}
	node := req.Header.Get("X-OGLVoice-Node")
	n, permitted := g.cfg.Nodes[node]
	if !permitted {
		jsonReply(w, http.StatusForbidden, reply{Protocol: wireProtocol, Status: "unknown_node"})
		return
	}
	data, err := readRequest(w, req)
	if err != nil {
		jsonReply(w, http.StatusRequestEntityTooLarge, reply{Protocol: wireProtocol, Status: "invalid_size"})
		return
	}
	if err = g.replay.verify(node, n, req, data, g.now()); err != nil {
		jsonReply(w, http.StatusUnauthorized, reply{Protocol: wireProtocol, Status: "invalid_or_replayed_proof"})
		return
	}
	var input exchange
	if err = json.Unmarshal(data, &input); err != nil || input.Protocol != wireProtocol {
		jsonReply(w, http.StatusBadRequest, reply{Protocol: wireProtocol, Status: "invalid_protocol"})
		return
	}
	if err = validAdmission(n, input.Admission); err != nil {
		jsonReply(w, http.StatusForbidden, reply{Protocol: wireProtocol, Status: "admission_denied"})
		return
	}
	switch input.Operation {
	case "offer":
		g.offer(w, n, node, input)
	case "trickle", "leave", "position":
		g.existing(w, node, input)
	default:
		jsonReply(w, http.StatusBadRequest, reply{Protocol: wireProtocol, Status: "invalid_operation"})
	}
}

func sessionKey(node string, a admission) string {
	return node + ":" + strings.ToLower(a.TenantID) + ":" +
		strings.ToLower(a.RegionID) + ":" + strings.ToLower(a.AvatarID) + ":" +
		strings.ToLower(a.SessionID)
}

func (g *mediaServer) capacityAllowed(a admission) bool {
	c := g.cfg.Tenants[a.TenantID]
	count := 0
	regionSet := make(map[string]struct{})
	for _, s := range g.sessions {
		if s.admission.TenantID == a.TenantID {
			count++
			regionSet[strings.ToLower(s.admission.RegionID)] = struct{}{}
		}
	}
	if c.MaxSessions > 0 && count >= c.MaxSessions {
		return false
	}
	_, exists := regionSet[strings.ToLower(a.RegionID)]
	return c.MaxRegions == 0 || exists || len(regionSet) < c.MaxRegions
}

func (g *mediaServer) offer(w http.ResponseWriter, nodeConfig nodeConfig, node string, e exchange) {
	if len(e.SDPOffer) == 0 || len(e.SDPOffer) > 32768 ||
		!strings.HasPrefix(e.SDPOffer, "v=0") || !strings.Contains(e.SDPOffer, "m=audio") {
		jsonReply(w, http.StatusBadRequest, reply{Protocol: wireProtocol, Status: "invalid_sdp_offer"})
		return
	}

	key := sessionKey(node, e.Admission)
	id, err := randomSession()
	if err != nil {
		jsonReply(w, http.StatusInternalServerError, reply{Protocol: wireProtocol, Status: "entropy_unavailable"})
		return
	}
	// Do not block live media initialization under the room/quota lock.
	g.mu.Lock()
	if _, exists := g.sessions[key]; exists {
		g.mu.Unlock()
		jsonReply(w, http.StatusConflict, reply{Protocol: wireProtocol, Status: "existing_session"})
		return
	}
	if !g.capacityAllowed(e.Admission) {
		g.mu.Unlock()
		jsonReply(w, http.StatusConflict, reply{Protocol: wireProtocol, Status: "tenant_quota"})
		return
	}
	// Reserve the slot while the media peer attempts to connect; prevents
	// parallel offers exceeding a tenant's paid quota.
	placeholder := &peerState{id: id, admission: e.Admission, node: node}
	g.sessions[key] = placeholder
	g.mu.Unlock()

	cleanup := func() {
		g.mu.Lock()
		if g.sessions[key] == placeholder {
			delete(g.sessions, key)
		}
		g.mu.Unlock()
	}
	defer func() {
		if err != nil {
			cleanup()
		}
	}()

	var peer *peerState
	peer, err = g.newPeer(g.cfg, e.Admission, id, func() {
		g.mu.Lock()
		// Don't delete a replacement when an old callback fires.
		if current := g.sessions[key]; current != nil &&
			current.id == id {
			delete(g.sessions, key)
		}
		g.mu.Unlock()
	})
	if err != nil {
		slog.Warn("OGLVoice media offer failed", "reason", err.Error())
		jsonReply(w, http.StatusServiceUnavailable, reply{Protocol: wireProtocol, Status: "livekit_or_webrtc_unavailable"})
		return
	}
	peer.node = node
	peer.admission = e.Admission
	if err = peer.acceptOffer(e.SDPOffer); err != nil {
		peer.close()
		jsonReply(w, http.StatusBadGateway, reply{Protocol: wireProtocol, Status: "sdp_negotiation_failed"})
		return
	}
	g.mu.Lock()
	// A callback may have removed the reservation if the viewer disconnected.
	if g.sessions[key] != placeholder {
		g.mu.Unlock()
		peer.close()
		jsonReply(w, http.StatusServiceUnavailable, reply{Protocol: wireProtocol, Status: "peer_disconnected"})
		return
	}
	g.sessions[key] = peer
	g.mu.Unlock()
	sdp := peer.answerSDP()
	if len(sdp) == 0 || len(sdp) > 32768 {
		peer.close()
		err = errors.New("SDP answer too large")
		jsonReply(w, http.StatusBadGateway, reply{Protocol: wireProtocol, Status: "invalid_sdp_answer"})
		return
	}
	jsonReply(w, http.StatusOK, reply{Protocol: wireProtocol, SDPAnswer: sdp, ViewerSession: id})
}

func (g *mediaServer) existing(w http.ResponseWriter, node string, e exchange) {
	if e.ViewerSession == "" || len(e.ViewerSession) > 128 {
		jsonReply(w, http.StatusForbidden, reply{Protocol: wireProtocol, Status: "unknown_session"})
		return
	}
	g.mu.Lock()
	p := g.sessions[sessionKey(node, e.Admission)]
	g.mu.Unlock()
	if p == nil || p.id != e.ViewerSession || p.node != node {
		jsonReply(w, http.StatusForbidden, reply{Protocol: wireProtocol, Status: "unknown_session"})
		return
	}
	if e.Operation == "leave" {
		p.close()
		jsonReply(w, http.StatusOK, reply{Protocol: wireProtocol, Status: "closed"})
		return
	}
	if e.Operation == "position" {
		if !e.Admission.PositionValid || !validPosition(admissionPosition(e.Admission)) {
			jsonReply(w, http.StatusBadRequest, reply{Protocol:wireProtocol,Status:"invalid_spatial_position"})
			return
		}
		if err := p.updatePosition(e.Admission); err != nil {
			jsonReply(w, http.StatusServiceUnavailable, reply{Protocol:wireProtocol,Status:"position_update_failed"})
			return
		}
		jsonReply(w, http.StatusOK, reply{Protocol:wireProtocol,Status:"ok"})
		return
	}
	if len(e.Candidates) > 32 || (len(e.Candidates) == 0 && !e.ICECompleted) {
		jsonReply(w, http.StatusBadRequest, reply{Protocol: wireProtocol, Status: "bad_ice_candidates"})
		return
	}
	for _, candidate := range e.Candidates {
		if len(candidate.Candidate) == 0 || len(candidate.Candidate) > 1024 ||
			candidate.SDPMLineIndex < 0 || candidate.SDPMLineIndex > 16 ||
			len(candidate.SDPMid) > 32 {
			jsonReply(w, http.StatusBadRequest, reply{Protocol: wireProtocol, Status: "invalid_ice_candidate"})
			return
		}
	}
	if err := p.addCandidates(e.Candidates, e.ICECompleted); err != nil {
		jsonReply(w, http.StatusBadRequest, reply{Protocol: wireProtocol, Status: "ice_update_failed"})
		return
	}
	jsonReply(w, http.StatusOK, reply{Protocol: wireProtocol, Status: "ok"})
}

func (g *mediaServer) reapOlderThan(maxIdle time.Duration) {
	g.mu.Lock()
	var stale []*peerState
	now := g.now()
	for _, p := range g.sessions {
		if p.lastSeen.IsZero() { continue } // pending offer
		if now.Sub(p.lastActivity()) > maxIdle {
			stale = append(stale, p)
		}
	}
	g.mu.Unlock()
	for _, p := range stale {
		p.close()
	}
}
func (g *mediaServer) sessionCount() int {
	g.mu.Lock()
	defer g.mu.Unlock()
	return len(g.sessions)
}
var _ = fmt.Sprintf
