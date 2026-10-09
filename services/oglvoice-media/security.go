// SPDX-License-Identifier: MPL-2.0
// Package main implements the independently deployable OGLVoice media bridge.
package main

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"
)

const (
	wireProtocol  = "oglvoice-firestorm-media-v1"
	requestLimit  = 64 * 1024
	responseLimit = 48 * 1024
	nonceLimit    = 4096
)

var (
	validNode  = regexp.MustCompile(`^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$`)
	validNonce = regexp.MustCompile(`^[a-f0-9]{32}$`)
	validSig   = regexp.MustCompile(`^[a-f0-9]{64}$`)
	validUUID  = regexp.MustCompile(`^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$`)
)

type nodeConfig struct {
	Secret string `json:"secret"`
	Tenant string `json:"tenant"`
}
type tenantConfig struct {
	MaxSessions int `json:"max_sessions"`
	MaxRegions  int `json:"max_regions"`
}
type config struct {
	Nodes      map[string]nodeConfig   `json:"nodes"`
	Tenants    map[string]tenantConfig `json:"tenants"`
	LiveKitURL string                 `json:"-"`
	LiveKitKey string                 `json:"-"`
	LiveKitSecret string              `json:"-"`
	Bind       string                 `json:"-"`
	PublicURL  string                 `json:"-"`
}

func loadConfig() (config, error) {
	var cfg config
	if err := json.Unmarshal([]byte(os.Getenv("OGLVOICE_MEDIA_ACL_JSON")), &cfg); err != nil {
		return cfg, fmt.Errorf("OGLVOICE_MEDIA_ACL_JSON: %w", err)
	}
	cfg.LiveKitURL = os.Getenv("OGLVOICE_LIVEKIT_URL")
	cfg.LiveKitKey = os.Getenv("OGLVOICE_LIVEKIT_API_KEY")
	cfg.LiveKitSecret = os.Getenv("OGLVOICE_LIVEKIT_API_SECRET")
	cfg.Bind = os.Getenv("OGLVOICE_MEDIA_BIND")
	if cfg.Bind == "" {
		cfg.Bind = "127.0.0.1:19098"
	}
	if cfg.LiveKitURL == "" || cfg.LiveKitKey == "" || len(cfg.LiveKitSecret) < 32 {
		return cfg, errors.New("LiveKit URL/API credentials missing or too short")
	}
	if len(cfg.Nodes) == 0 || len(cfg.Tenants) == 0 {
		return cfg, errors.New("media node ACL and tenant registry must not be empty")
	}
	for name, n := range cfg.Nodes {
		if !validNode.MatchString(name) || len(n.Secret) < 32 || len(n.Tenant) == 0 {
			return cfg, fmt.Errorf("invalid node entry: %s", name)
		}
		if _, ok := cfg.Tenants[n.Tenant]; !ok {
			return cfg, fmt.Errorf("node %s references undefined tenant", name)
		}
	}
	for name, tenant := range cfg.Tenants {
		if !validNode.MatchString(name) || tenant.MaxSessions < 0 || tenant.MaxRegions < 0 {
			return cfg, fmt.Errorf("invalid tenant quota: %s", name)
		}
		// 0 = unlimited only for explicitly provisioned tenants; it is
		// never inferred from request.TenantId or from a viewer.
	}
	return cfg, nil
}

type admission struct {
	TenantID        string `json:"TenantId"`
	RegionID        string `json:"RegionId"`
	AvatarID        string `json:"AvatarId"`
	SessionID       string `json:"SessionId"`
	HomeGridOrigin  string `json:"HomeGridOrigin"`
	IsHypergridGuest bool  `json:"IsHypergridGuest"`
	VoiceAllowed    bool   `json:"VoiceAllowed"`
}
type iceCandidate struct {
	Candidate     string `json:"candidate"`
	SDPMid        string `json:"sdpMid"`
	SDPMLineIndex int    `json:"sdpMLineIndex"`
}
type exchange struct {
	Protocol      string         `json:"protocol"`
	Operation     string         `json:"operation"`
	Admission     admission      `json:"admission"`
	SDPOffer      string         `json:"sdp_offer"`
	ViewerSession string         `json:"viewer_session"`
	Candidates    []iceCandidate `json:"candidates"`
	ICECompleted  bool           `json:"ice_completed"`
}
type reply struct {
	Protocol      string `json:"protocol"`
	SDPAnswer     string `json:"sdp_answer,omitempty"`
	ViewerSession string `json:"viewer_session,omitempty"`
	Status        string `json:"status,omitempty"`
}

func validAdmission(node nodeConfig, a admission) error {
	if !a.VoiceAllowed || a.TenantID != node.Tenant ||
		!validUUID.MatchString(a.AvatarID) ||
		!validUUID.MatchString(a.RegionID) ||
		!validUUID.MatchString(a.SessionID) ||
		strings.EqualFold(a.SessionID, "00000000-0000-0000-0000-000000000000") {
		return errors.New("invalid or cross-tenant admission")
	}
	if a.IsHypergridGuest && a.HomeGridOrigin == "" {
		return errors.New("Hypergrid guest requires verified home grid")
	}
	if !a.IsHypergridGuest && a.HomeGridOrigin != "" {
		return errors.New("local avatar must not forge a Hypergrid home")
	}
	if len(a.HomeGridOrigin) > 1024 {
		return errors.New("invalid home grid")
	}
	return nil
}

func signature(secret, node string, stamp int64, nonce string, data []byte) (string, error) {
	if len(secret) < 32 || !validNode.MatchString(node) ||
		!validNonce.MatchString(nonce) || stamp <= 0 ||
		len(data) == 0 || len(data) > requestLimit {
		return "", errors.New("invalid media proof")
	}
	sum := sha256.Sum256(data)
	body := "oglvoice-media-v1\n" + node + "\n" + strconv.FormatInt(stamp, 10) +
		"\n" + nonce + "\n" + hex.EncodeToString(sum[:])
	mac := hmac.New(sha256.New, []byte(secret))
	mac.Write([]byte(body))
	return hex.EncodeToString(mac.Sum(nil)), nil
}

type replayGuard struct {
	sync.Mutex
	used map[string]int64
}
func newReplayGuard() *replayGuard { return &replayGuard{used: make(map[string]int64)} }
func (r *replayGuard) verify(node string, cfg nodeConfig, req *http.Request,
	body []byte, now time.Time) error {
	stampString := req.Header.Get("X-OGLVoice-Timestamp")
	stamp, err := strconv.ParseInt(stampString, 10, 64)
	if err != nil || stampString != strconv.FormatInt(stamp, 10) {
		return errors.New("invalid timestamp")
	}
	delta := now.Unix() - stamp
	if delta < -60 || delta > 60 {
		return errors.New("expired media proof")
	}
	nonce := req.Header.Get("X-OGLVoice-Nonce")
	proof := req.Header.Get("X-OGLVoice-Signature")
	if !validSig.MatchString(proof) {
		return errors.New("invalid signature")
	}
	expected, err := signature(cfg.Secret, node, stamp, nonce, body)
	if err != nil || !hmac.Equal([]byte(expected), []byte(proof)) {
		return errors.New("invalid signature")
	}
	r.Lock()
	defer r.Unlock()
	for k, seenAt := range r.used {
		if now.Unix()-seenAt > 121 {
			delete(r.used, k)
		}
	}
	key := node + ":" + nonce
	if _, exists := r.used[key]; exists {
		return errors.New("replayed media proof")
	}
	if len(r.used) >= nonceLimit {
		return errors.New("replay cache capacity")
	}
	r.used[key] = now.Unix()
	return nil
}

func readRequest(w http.ResponseWriter, req *http.Request) ([]byte, error) {
	req.Body = http.MaxBytesReader(w, req.Body, requestLimit)
	defer req.Body.Close()
	data, err := io.ReadAll(req.Body)
	if err != nil || len(data) == 0 {
		return nil, errors.New("empty or oversized request")
	}
	return data, nil
}
func randomSession() (string, error) {
	var id [24]byte
	if _, err := rand.Read(id[:]); err != nil {
		return "", err
	}
	return hex.EncodeToString(id[:]), nil
}
