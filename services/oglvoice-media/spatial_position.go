// SPDX-License-Identifier: MPL-2.0
package main

import (
	"encoding/json"
	"errors"
	"strings"
)

const spatialWire = "oglvoice-spatial-v1"

// Positions originate from an authenticated simulator Scene, never viewer
// messages. LiveKit broadcasts are accepted only under the actual sender
// participant identity in the same pre-authorized region room.
type spatialUpdate struct {
	Protocol string  `json:"protocol"`
	X        float64 `json:"x"`
	Y        float64 `json:"y"`
	Z        float64 `json:"z"`
	Heading  float64 `json:"heading"`
}
func (v spatialUpdate) position() voicePosition {
	return voicePosition{X:v.X,Y:v.Y,Z:v.Z,Heading:v.Heading,Valid:v.Protocol==spatialWire}
}
func admissionPosition(a admission) voicePosition {
	return voicePosition{X:a.PositionX,Y:a.PositionY,Z:a.PositionZ,
		Heading:a.Heading, Valid:a.PositionValid}
}
func participantSpatialMetadata(a admission) map[string]interface{} {
	return map[string]interface{}{
		"avatar": strings.ToLower(a.AvatarID),
		"position": spatialUpdate{Protocol:spatialWire, X:a.PositionX,
			Y:a.PositionY, Z:a.PositionZ, Heading:a.Heading},
		"position_valid": a.PositionValid,
	}
}
func parseParticipantPosition(metadata string) voicePosition {
	var decoded struct {
		Position spatialUpdate `json:"position"`
		PositionValid bool `json:"position_valid"`
	}
	if len(metadata)>2048 || json.Unmarshal([]byte(metadata),&decoded)!=nil ||
		!decoded.PositionValid { return voicePosition{} }
	pos:=decoded.Position.position()
	if !validPosition(pos) { return voicePosition{} }
	return pos
}
func (p *peerState) updatePosition(a admission) error {
	pos:=admissionPosition(a)
	if !validPosition(pos) { return errors.New("invalid position") }
	if p.mixer==nil { return errors.New("missing audio mixer") }
	p.mixer.setListener(pos)
	p.touch()
	if p.room!=nil {
		wire:=spatialUpdate{Protocol:spatialWire,X:pos.X,Y:pos.Y,
			Z:pos.Z,Heading:pos.Heading}
		data,_:=json.Marshal(wire)
		// Only the room's authenticated media participant can transmit.
		if err:=p.room.LocalParticipant.PublishData(data); err!=nil {return err}
	}
	return nil
}
