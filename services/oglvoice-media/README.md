# OGLVoice Go Media Bridge — erster realer WebRTC-/LiveKit-Transport

**Zielrelease:** OpenGenesisLINK 0.9.3.10 Dev. **Status:** echte Pion-/LiveKit-Transportimplementierung, kein Stable und keine komplette Multi-Speaker-Spatial-Audio-Loesung.

## Architektur

```
Firestorm-Viewer (Audio + DataChannel)
  -> [ProvisionVoiceAccountRequest / VoiceSignalingRequest] OpenGenesisLINK C# Region CAPS
  -> [HMAC-SHA256, node ACL, 60s nonce] /internal/webrtc/v1/exchange
  -> OGLVoice Media Bridge (Go, Pion WebRTC)
  -> [Opus RTP upload, LiveKit SDK] zentraler LiveKit SFU
  -> [derzeit EIN abonnierter Opus-Downlink pro Viewer] Firestorm
```

Der Dienst liegt unter `services/oglvoice-media`. Ein einziges Media-Service-Cluster kann die Nodes vieler Grids aufnehmen. Nur die **serverseitige** Node-/Tenant-Konfiguration legt Rechte fest. Der `nexverse`-Mandant bekommt `max_sessions=0,max_regions=0` fuer **keine Lizenz-Quoten**. Fuer andere Mandanten koennen Limits gesetzt werden.

## 0.9.3.10 Dev: simultaner Opus-Spatial-Mixer

Dieser Entwicklungszweig ersetzt den vorherigen Ein-Sprecher-Downlink durch
**pro Zuhörer individuelle Stereo-Mischungen**. Der Media-Server decodiert
jede empfangene LiveKit-Opus-Spur getrennt (libopus), puffert höchstens fünf
20-ms-Frames und mischt die aktuell hörbaren Stimmen alle 20 ms als Stereo-PCM.
Entfernung (40 m Hörgrenze), Hörerrichtung, Equal-Power-Panning, Pegelabfall
und weiche Begrenzung werden in `spatial_mixer.go` berechnet. Die Summe wird
als **neuer Opus-Stream** an Firestorm encodiert.

**Positionsdaten stammen ausschließlich aus der authentifizierten
`ScenePresence` im Simulator**, nicht aus Viewer-Formularen. Der Simulator
sendet bei der Voice-Session-Einrichtung eine Positions-/Blickrichtungsprobe
und aktualisiert sie über signierte `position`-Requests alle zwei Sekunden.
Der Media-Server teilt bestätigte Veränderungen über den bestehenden LiveKit-
Raum und dessen Datenkanal mit den anderen Zuhörer-Mixern. Ist die Position
unbekannt, wird der betreffende Sprecher nicht hörbar. Bei entfallender
Estate-/Parcel-/Root-Agent-Berechtigung wird ein `leave` gesendet.

**Grenzen:** maximal 64 gleichzeitig abonnierten Quellen pro Zuhörer und
24 stärkste Quellen pro 20-ms-Mix als CPU-Sicherungsgrenze (keine
Lizenzbegrenzung der Avatare/Regionen). Es ist ein stereophoner
Entfernungs-/Richtungsmixer, **keine** vollständige HRTF-Ohrsimulation,
akustische Abschattung/Reflexion, lückenloser Regionsgrenzen-Mix oder
professionelle adaptive Jitter-/Loss-Concealment-Engine.

Für den neuen Mixer benötigt der **Build** jetzt `pkg-config` und
`libopus-dev`; die **Laufzeit** benötigt die dynamische Systembibliothek
`libopus.so.0` (Debian: `libopus0`). Mit dem Build-Tag
`nolibopusfile` sind keine `libopusfile`-Header nötig:

```bash
sudo apt-get install -y pkg-config libopus-dev
go mod tidy
go test -tags nolibopusfile -race ./...
go build -tags nolibopusfile -trimpath -o oglvoice-media .
```

Die automatische Go-CI installiert die libopus-Build-Abhängigkeiten und
testet simultane Opus-Signale, Panning, Dämpfung, 40-ms-Paket-Splitting,
Clipping, Quoten und signierte Positionsupdates. **Das ist noch kein
Nachweis der Funktion im echten Firestorm mit drei Sprechern.**

### Bereits implementiert

- Echte Pion-v4-`PeerConnection`, WebRTC Offer/Answer, ICE-Gathering, Trickle-ICE, DTLS/SRTP und SCTP-Datenkanal des Firestorm-Viewers.
- Echte LiveKit-Go-SDK-Raumsitzung pro Avatar/Region. Eingehendes Opus-RTP wird in den LiveKit-Raum publiziert und ein entfernter Opus-Track als Downlink an den Viewer uebertragen.
- LiveKit-Roster-/Speaking-Events werden in Firestorm-Frames nach tatsaechlicher Avatar-UUID gewandelt: `j` (join), `p` (0 bis 128), `v` (speaking), `l` (leave). Pro Viewer eigener SCTP-Kanal, einschliesslich Hypergrid-Gaesten.
- HMAC-SHA256 ueber exakt die C#-Requestbytes, Node/Nonce/Timestamp, Replay-Schutz und serverseitiges ACL-Mapping `Node -> Tenant`. Anbieter-/Grid-Schluessel bleiben ausserhalb von GitHub; LiveKit Signing-Keys nur auf diesem Dienst.
- Tenant-spezifische Region-/Sitzungslimits, Session-Lifecycle, Handover-/Logout-Bindung durch `Node,Region,Avatar,AgentSessionId,viewer_session`, kein freier Room-Namen-Parameter.
- Graceful Shutdown, Session-Reaper, HTTP-Schutz, Go-Tests inklusive realem WebRTC-SDP-Offer/Answer zwischen Pion-Peers.

### Grenzen dieser Ausbaustufe (wichtige Release-Blocker)

- **Noch kein Opus-Mixer:** Die aktuelle Spatial-Mixer-Ausbaustufe kann mehrere entfernte Audiostreams gleichzeitig decodieren und als Stereo-Opus-Downlink mischen; ein produktionserprobtes HRTF-System mit akustischer Abschattung, Loss Concealment und Mesh-Hindernissen bleibt offen. Der fuer den vollstaendigen Vivox-Ersatz erforderliche Mehrsprecher-Spatial-Mixer, Sichtweitenabstand, Orientierung und Parzellengrenzen sind offen.
- **Noch keine Full-E2E-Bestaetigung mit echten Firestorm-Viewern** und produktivem LiveKit sowie konkreter Firewall-/ICE-Konfiguration. Die CI uebt reale lokale Pion-WebRTC-Verbindungen aus, kein produktives Grid.
- **Keine komplette zentrale Revoke-/Presence-Kontrollschicht:** der Go-Gateway loescht Sitzungen bei `leave`, Peer-Ausfall, Leerlauf und Shutdown. Ein plattformuebergreifender zweifelsfreier Logout-/Teleport-/Rollenwechsel-Push aus Robust zum Gateway fehlt.
- **Voice-orb Status:** Der Code sendet Roster und LiveKit-Aktivitaetsereignisse an alle autorisierten Viewer-Datenkanaele. Ob die Viewer die graue Kugel in jeder Hypergrid-Konstellation zeigen, muss real getestet werden.
- **TURN-Auth und Deployment:** STUN kann ueber `OGLVOICE_WEBRTC_STUN_URLS` konfiguriert werden. Fuer NAT-Situationen sind authentifizierte TURN-Credentials, Firewall-/Proxy-Routing sowie OpenSim-/LiveKit-Endpunktverifikation vor Freigabe erforderlich.
- **Quota-Abrechnung:** Limits sind ein statischer technischer Mechanismus, keine Pakete/Bestell-/Abrechnungsplattform.

## Installation — Debian 13 / AMD64 oder ARM64

Benötigt Go gemaess `go.mod` (aktuelle LiveKit Go SDK Toolchain), eine erreichbare LiveKit-Instanz sowie einen HTTPS-Reverse-Proxy vor dem internen Mediendienst.

```bash
git clone https://github.com/opengenesislink/nexverse_server_main.git
cd nexverse_server_main/services/oglvoice-media
go mod tidy
go test -race ./...
go build -trimpath -o oglvoice-media .
```

Der Dienst startet absichtlich **nicht** ohne geschuetzte Zugangsdaten. Ein kleines, **nicht einzucheckendes** Beispiel fuer die EnvironmentFile `/etc/oglvoice/media.env`:

```bash
OGLVOICE_MEDIA_BIND=127.0.0.1:19098
OGLVOICE_LIVEKIT_URL=wss://LIVEKIT-SERVER-HOST
OGLVOICE_LIVEKIT_API_KEY=REPLACE_WITH_REAL_KEY
OGLVOICE_LIVEKIT_API_SECRET=REPLACE_WITH_REAL_RANDOM_SECRET_MIN_32_BYTES
OGLVOICE_WEBRTC_STUN_URLS=stun:stun.l.google.com:19302
OGLVOICE_MEDIA_ACL_JSON='{"nodes":{"sim-freiburg-01":{"secret":"REPLACE_WITH_THE_REAL_NEXBUS_KEY_MIN_32_BYTES","tenant":"nexverse"}},"tenants":{"nexverse":{"max_sessions":0,"max_regions":0}}}'
```

**Wichtig:** `LIVEKIT-SERVER-HOST` und `sim-freiburg-01` sind Beispiele. Weder das LiveKit-Ziel noch die Node-IDs duerfen in Produktion ungeprueft uebernommen werden. `chmod 0600 /etc/oglvoice/media.env`, Datei ausschliesslich fuer den Service-Account lesbar. Die externe HTTPS-URL des Reverse-Proxys leitet ausschliesslich `POST /internal/webrtc/v1/exchange` nach `127.0.0.1:19098` weiter, und zwar nur von erlaubten Simulator-Netzen. **Keine anonyme externe Ansteuerung**, auch wenn HMAC die Requests separat prueft.

Minimaler systemd-Unit-Entwurf:
```ini
[Unit]
Description=OpenGenesisLINK OGLVoice Media Bridge
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=oglvoice
Group=oglvoice
WorkingDirectory=/opt/oglvoice
EnvironmentFile=/etc/oglvoice/media.env
ExecStart=/opt/oglvoice/oglvoice-media
Restart=on-failure
RestartSec=3
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
LimitNOFILE=65535

[Install]
WantedBy=multi-user.target
```

Das UDP-ICE-Media-Routing benoetigt in realen Installationen geeignet freigeschaltete UDP-Ports und gegebenenfalls TURN; bei Container-/Firewall-Konfiguration kann eine nur lokale HTTP-Route allein **keine** WebRTC-Medienverbindung ermoeglichen.

## Robust-Konfiguration

Erst nach Installation und lokalem Angebot/Antwort-Test unter HTTPS:

```ini
[OGLVoice]
    Enabled = true
    Mode = "GridAuthority"
    ServiceUrl = "https://voice.stadt-nexverse.de"
    TenantId = "nexverse"
    IncludeHypergridGuests = true
    EnableFirestormGateway = true
    MediaGatewayUrl = "https://DEIN-VOICE-MEDIA-HOST/internal/webrtc/v1/exchange"
```

**Nicht** direkt `voice.stadt-nexverse.de` als MediaGatewayUrl eintragen, solange dort nur das bestehende Webportal betrieben wird. Die Domain muss wirklich auf den neuen Dienst und den exakten Exchange-Pfad zeigen. Die C#-Region verwendet den NexBus SharedKey; dieser muss zum `secret` des jeweiligen Node-Eintrags im Go-Dienst passen.

Die OpenGenesisLINK-Standalone-Konfiguration findet sich in `bin/OpenSim.ini.example`.

## CI-/Abnahmestufen

`OGLVoice Go Media Gateway`-GitHub-Actions-Workflow testet `go mod tidy`, `go vet`, `go test -race`, das ausfuehrbare Binary, negative Config und Pion SDP/ICE ohne externe Konten.

**Vor Stable zwingend:** Zwei verschiedene Firestorm-Viewer mit bidirektionalem Audio und gleichzeitigen Sprechern, graue Kugeln bei allen sichtbaren Avataren, ein externer HG-Avatar, neue/alte Region-Sitzung, Mute/PTT, Audio-Entfernung, Nachbarregionen, Zombie-Sessions, Restart, 100+ Avatar Lastsimulation und Tenant-/Node-ACL-Tests. Ein erfuellter Kompiliertest ist keine echte Voice-Abnahme.
