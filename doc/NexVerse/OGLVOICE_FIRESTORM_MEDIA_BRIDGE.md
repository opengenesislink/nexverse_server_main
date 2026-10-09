# OGLVoice ↔ Firestorm WebRTC Media Bridge v1

**Status:** Gateway-Adapter in OpenGenesisLINK 0.9.3.10 Dev, **kein lauffaehiger eigenstaendiger SDP-/LiveKit-Medienserver**. Kein Stable-/RC-Claim. Standardmaessig deaktiviert.

## Zweck

Die originalen Firestorm-WebRTC-Clients koennen bei `VoiceServerType = webrtc` die OpenSim-Region-CAPS `ProvisionVoiceAccountRequest` und `VoiceSignalingRequest` nutzen. Die Region vermittelt **LLSD/XML** und authentifizierte Avatar-/Estate-/Parcel-Sitzung zur **zentralen externen OGLVoice Media Bridge**. Der Medienserver muss den WebRTC-SDP/ICE/DTLS/SRTP/SCTP-Vertrag wirklich bedienen und die Audio-Medien mit unserem LiveKit-Netz verbinden. LiveKit-SFU-Tokens sind **nicht** dasselbe wie eine WebRTC-SDP-Answer fuer den unveraenderten Firestorm.

Der neue Quellcode enthaelt:
- `NexVerse/RegionModules/Voice/OglVoiceFirestormCapsModule.cs` mit Firestorm-`SimulatorFeatures` und den beiden LLSD-Viewer-CAPS.
- `NexVerse/Core/Voice/OglVoiceFirestormWire.cs` mit Protokollvalidierung und **originalem Firestorm-Sprecher-Datenkanalformat**.
- `NexVerse/Core/Voice/OglVoiceMediaProof.cs` mit auf 64KiB begrenzter, HMAC-SHA256-signierter Media-Exchange, `node`/`nonce`/`timestamp`/`SHA256(body)` und abweichendem Prefix `oglvoice-media-v1`.
- Die signierte `OglVoiceProviderDescriptor`-Discovery enthaelt nur bei ausdruecklich aktiviertem Mediengateway `viewer_capability = "firestorm-webrtc-v1"` und `media_gateway_url`.
- Die private LiveKit-JWT-Session-Authority aus PR #119 bleibt unveraendert; die Media Bridge muss ihre eigene autorisierte LiveKit-Integration entwickeln.

## Inbetriebnahme und Grenzen

**Robust/HG-Modus:** In `Robust.HG.ini` oder `Robust.ini` nach bestaetigtem Gateway-Test:
```ini
[OGLVoice]
    Enabled = true
    Mode = "GridAuthority"
    ServiceUrl = "https://voice.stadt-nexverse.de"
    TenantId = "nexverse"
    IncludeHypergridGuests = true
    EnableFirestormGateway = true
    MediaGatewayUrl = "https://VOICE-MEDIA-BRIDGE-HOST/internal/webrtc/v1/exchange"
```
Die URL mit `VOICE-MEDIA-BRIDGE-HOST` ist **ein Platzhalter, kein existierender Endpunkt**. Es muss eine wirklich installierte, kompatible und hinter HTTPS betriebene OGLVoice Media Bridge geben. Dann uebernehmen alle Simulatoren mit aktivem `[NexVerseNodeAgent]` und dem passenden NexBus-SharedKey diese Provider-Konfiguration aus Robust. Der neue `OglVoiceFirestormCapsModule` ist in solchen Simulatoren automatisch aktiv und wirbt `VoiceServerType=webrtc` fuer berechtigte Avatar-Sitzungen, sobald signierte und frische Discoverydaten mit Gateway-URL vorliegen. **Kein API-Schluessel pro Simulator in der INI**.

**Standalone:** `[OGLVoice] Mode="Standalone"`, `ServiceUrl`, `TenantId`, `EnableFirestormGateway=true`, `MediaGatewayUrl`. Die gesonderte Authentisierung nutzt `OGLVOICE_MEDIA_BRIDGE_SHARED_KEY` (mindestens 32 UTF-8-Bytes) und optional `OGLVOICE_MEDIA_BRIDGE_NODE_ID` aus der geschuetzten Simulatorumgebung. **Keine LiveKit-Signing-Keys auf Simulatoren**.

Bei fehlendem Provider, ungueltigem HTTPS-Gateway, fehlendem Node-Trust oder abgelehnten Estate-/Parcel-Rechten werden keine neuen Viewer-CAPS angekuendigt und kein WebRTC als verfuegbar beworben. Ein abgeschaltetes Gateway darf weder Inworld-Text-IM noch Login/Teleport blockieren. Voice kann aber erst nach dem naechsten CAPS-/Feature-Abruf sichtbar werden, wenn Discovery nach einem Regionstart verzoegert erfolgt.

## Firestorm ↔ Region CAPS (LLSD/XML)

**1. SimulatorFeatures:** `VoiceServerType: "webrtc"` nur bei signiertem `firestorm-webrtc-v1`-Provider und zugelassenem Agenten.

**2. ProvisionVoiceAccountRequest, `POST`:**
```xml
<llsd><map>
  <key>voice_server_type</key><string>webrtc</string>
  <key>channel_type</key><string>local</string>
  <key>jsep</key><map>
    <key>type</key><string>offer</string>
    <key>sdp</key><string>v=0&#13;&#10;...m=audio...</string>
  </map>
</map></llsd>
```
Die Region prueft den Agent-Circuit, den Root-Agent, Estate-`AllowVoice`, Parcel-`AllowVoiceChat` und HG-Gast-Policy. Die angeforderte `parcel_local_id` darf **nicht** als Raumzugriffserlaubnis gelten: die Position in der Scene bestimmt den zulaessigen Raum. Der Medienserver antwortet mit einer *echten* SDP-Answer:
```xml
<llsd><map>
  <key>viewer_session</key><string>opaque-session-id</string>
  <key>jsep</key><map>
    <key>type</key><string>answer</string>
    <key>sdp</key><string>v=0&#13;&#10;...m=audio...</string>
  </map>
</map></llsd>
```

**3. VoiceSignalingRequest, `POST`:** `viewer_session`, `voice_server_type=webrtc`, `candidates: [{candidate,sdpMid,sdpMLineIndex}]` oder `candidate: {completed:true}` werden gebunden an den in derselben Region ausgegebenen `viewer_session` weitergeleitet. Logout wird als `ProvisionVoiceAccountRequest { logout: true, viewer_session: ...}` gehandhabt. Der Medienserver MUSS Sitzungen bei Logout, Regionwechsel, Agent-Timeout und Berechtigungsaenderung ebenfalls invalidieren; der aktuelle Adapter bietet **keine vollstaendige aktive zentrale Revocation**.

## Region → zentrale Media Bridge (JSON)

Der Simulator sendet an die **exakt konfigurierte HTTPS-MediaGatewayUrl** eine JSON-Struktur:
```json
{
  "protocol": "oglvoice-firestorm-media-v1",
  "operation": "offer",
  "admission": {
    "TenantId": "nexverse",
    "RegionId": "00000000-0000-0000-0000-000000000001",
    "AvatarId": "00000000-0000-0000-0000-000000000002",
    "SessionId": "00000000-0000-0000-0000-000000000003",
    "HomeGridOrigin": null,
    "IsHypergridGuest": false,
    "VoiceAllowed": true
  },
  "sdp_offer": "v=0\\r\\n...m=audio..."
}
```
Fuer ICE: `operation: trickle`, `viewer_session`, `candidates`, `ice_completed`. Fuer Logout: `operation: leave`, `viewer_session`.

Die Bridge muss folgende Header validieren:
- `X-OGLVoice-Node`: per ACL zugelassene Simulator-NodeId.
- `X-OGLVoice-Timestamp`: Unix-Sekunden, Abweichung maximal 60 Sekunden.
- `X-OGLVoice-Nonce`: 16 Byte hex, innerhalb Zeitfenster nur **einmal** verwendbar; Node+Nonce getrennt speichern.
- `X-OGLVoice-Signature`: lowercase hex HMAC-SHA256 ueber UTF-8-String `oglvoice-media-v1\n<node>\n<timestamp>\n<nonce>\n<lowercase-sha256-of-exact-json-bytes>`, mit dem Grid-/Node-spezifischen vorab provisionierten SharedKey.

**Maximaler Body: 64 KiB**; SDP maximal 32768 UTF-8-Bytes, Kandidaten maximal 32 pro Anfrage, je Kandidat maximal 1024 Bytes. Die Bridge darf **niemals** eine frei im Body behauptete Mandanten- oder Avataridentitaet ohne vertrauenswuerdige Node-/Grid-Zuordnung und gepruefte Simulatorsitzung uebernehmen. `nexverse` und Unlimited sind ausschliesslich serverseitig zugeordnete Rechte.

Antwort auf `offer`:
```json
{
  "protocol": "oglvoice-firestorm-media-v1",
  "viewer_session": "opaque-session-id",
  "sdp_answer": "v=0\\r\\n...m=audio..."
}
```
`trickle`/`leave` benoetigen eine gueltige `protocol`-Antwort. Die Region antwortet dem Viewer in diesen Faellen mit `<llsd><undef /></llsd>`.

## Remote Voice-Kugeln und Firestorm SCTP-Datenkanal

Die **zentrale Media Bridge** muss ueber den von ihr terminiertem **WebRTC-SCTP-Datenkanal** an **jeden Viewer** Meldungen schicken, deren oberster JSON-Key die tatsaechlich im Viewer sichtbare **Avatar-UUID** ist (nicht die gehashte LiveKit-Identitaet).

- Beitritt: `{"<avatar-uuid>":{"j":{"p":true}}}`. Die Kugel erscheint, auch wenn der Avatar gerade schweigt.
- Aktives Sprechen: `{"<avatar-uuid>":{"p":64,"v":true}}`. Der Pegel `p` ist `round(linearLevel*128)`, auf 0..128 beschraenkt.
- Stille: `{"<avatar-uuid>":{"p":0,"v":false}}`. Die Kugel bleibt grau.
- Verlassen: `{"<avatar-uuid>":{"l":true}}`. Die Kugel verschwindet.

Die Media Bridge **muss** jeden Join, die Pegel/VAD-Events und Leave-Nachrichten an alle zur selben Region/Parzelle berechtigten Teilnehmer senden, einschliesslich HG-Gaesten. Bei PTT/Mute/Disconnect/Stille muessen Speaking-Zustaende zeitnah zurueckgesetzt werden. Firestorm kann die Darstellung seiner Visualizer benutzerseitig abschalten. Die Nachrichtengeneratoren `OglVoiceFirestormWire.Join/Speaking/Leave` und ihre .NET-Tests existieren; **die tatsaechliche LiveKit-Mischung, das Weiterleiten dieser Daten ueber reale SCTP-Kanaele und die Abnahme mit zwei Firestorm-Clients stehen noch aus**.

## Zwingende fehlende Komponenten fuer 0.9.3.10 Stable

1. Eine **echte** OGLVoice Media Bridge, welche Firestorms SDP/ICE/DTLS/SRTP/SCTP annimmt und Audio zu/von LiveKit vermittelt. Das vorhandene LiveKit-SFU allein bietet diesen Firestorm-Protokollvertrag nicht.
2. Spatial-Audio-Wiedergabe pro Zuhoerer (Entfernung, Richtung, Parcel/Estate, Grenzuebertritte), lebende Teilnehmerliste inkl. HG und tatsaechlich ausgestrahlte Data-Channel-Frames.
3. Serverseitige langlebige Session-/Room-Registry, Event-getriebene Join/Leave-Revocation, Schutz gegen stale/duplizierte Medien-Sitzungen, Mandantenisolation und Paketlimits; NexVerse first-party `Unlimited` ohne kuenstliche Quoten.
4. LiveKit-Server-Integration auf `voice.stadt-nexverse.de` oder dem vorgesehenen separaten Media-Host und erreichbare ICE/STUN/TURN-Routen.
5. End-to-End-Test mit zwei lokalen Firestorm-Viewern und mindestens einem Hypergrid-Besucher: Audio beide Richtungen, graue Kugeln ueber **allen** Avataren, animierte Wellen nur am Sprecher, PTT, mute, Abstand, Teleport, Rechtewechsel und Restart.
6. Wiederholbare Integration-/Performance-/Sicherheits-Livetests und erst dann RC1/Stable-Abnahme.

Es wird **weder** ein Existieren des oben beispielhaft genannten Media-Gateway-Endpunkts **noch** eine funktionierende Audioverbindung oder Stable-Freigabe behauptet.
