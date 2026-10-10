# OpenGenesisLINK 0.9.3.10 Dev – Firestorm Voice/Pathfinding Logabgleich (10.10.2026)

## Gesicherte Symptome aus dem Firestorm 7.2.4.80712-Log

Region `OGL Developer Gen1`; Log-Zeiten sind UTC (13:23 = 15:23 MESZ).

- **Navigation:** `LLPathfindingManager::getCapabilityURLForRegion:
  cannot find capability 'RetrieveNavMeshSrc' for current region 'OGL Developer Gen1'`.
- **Voice:** `LLVivoxVoiceClient::provisionVoiceAccount`, dann
  `cannot POST url ''` und `Unable to provision voice account`.
- Firestorm startet damit **den Legacy-Vivox-Pfad**; das Log beweist
  **nicht**, dass ein OGLVoice/WebRTC-Mediendienst funktionierte.

## Technische Trennung: kein kosmetisches Unterdruecken der Warnungen

**OpenGenesisLINK Pathfinding:** `[OGLPathfinding] Enabled=true` aktiviert
den bereits implementierten **nativen Terrain-A*-Snapshot** mit
`IOglTerrainNavigationRegion` und optionalen NPC-Wegpunkten. Er erzeugt
derzeit **keinen** Firestorm-/Second-Life-kompatiblen NavMesh-Datenstrom.
Firestorm verlangt fuer `RetrieveNavMeshSrc` einen POST-Endpunkt mit
`navmesh_version` und zlib/LLSD-komprimierten binären
`navmesh_data`; zusaetzlich ist eine konsistente
`NavMeshGenerationStatus`-Capability erforderlich. Ein leerer
oder frei erfundener Payload ist **kein** Ersatz und kann den Viewer
stoeren. Deshalb **nicht** als CAP registrieren, solange ein korrektes
Viewer-NavMesh-Backend fehlt. Die fehlende CAP ist eine reale
Kompatibilitaetsluecke; ein eingeschalteter Terrain-Pathfinder
beseitigt die Warnung nicht.

**OGLVoice:** Firestorm benoetigt den von OpenGenesisLINK bei
`VoiceServerType=webrtc` beworbenen zweistufigen
`ProvisionVoiceAccountRequest` / `VoiceSignalingRequest`-Pfad.
Die Implementierung existiert als **opt-in** C#-Regionadapter mit
zentral signierter Provider-Discovery. Diese wird nur aktiv,
wenn ein tatsaechlich betriebener Pion/WebRTC–LiveKit-Media-Bridge-
Dienst per gueltigem `MediaGatewayUrl`, `EnableFirestormGateway=true`
und authentifizierter NexBus-Vertrauensbeziehung verbunden ist.
Das vorhandene OGLVoice-Webportal oder eine direkte LiveKit-`wss://`-
Adresse ist **kein** Medien-Gateway. Bis dahin faellt der Viewer
moeglicherweise auf Vivox mit leerer URL zurueck.
Nie eine falsche Vivox-Adresse konfigurieren, kein
`VoiceServerType=webrtc` ohne betriebsbereiten Gateway erzwingen.

## Neuer sicherer Preflight im Quellcode

```bash
cd /opt/developer
python3 tools/diagnostics/ogl_voice_pathfinding_preflight.py \
  --sim-config /opt/developer/bin/OpenSim.ini \
  --robust-config /opt/robust/bin/Robust.HG.ini
```

**Dateinamen anpassen**: Das im Produktiveinsatz tatsaechlich
geladene Robust-Config kann `Robust.ini` statt `Robust.HG.ini`
heissen. Mehrere `*.ini`-Includes werden vom Preflight **nicht**
automatisch aufgeloest. Die Beispiel-Konfiguration
`OpenSim.ini.example` ist kein Beweis fuer produktive Werte.
Das auf dem Linux-Desktop liegende `Firestorm.log` kann optional
durch `--viewer-log /pfad/zur/Firestorm.log` hinzugefuegt werden.
`--json` bietet strukturierte Ergebnisse. Das Tool ist **rein lesend**,
macht **keine** Netzwerkaufrufe und gibt **keine** Zugangsdaten aus.

Vor jedem Live-Enable:
1. **Media Bridge wirklich bereitstellen.** Go-Dienst
   `services/oglvoice-media` bauen, Umgebungsvariablen sicher setzen,
   LiveKit-Server/Schluessel, Node/Tenant ACL, STUN/TURN und UDP-ICE
   kontrollieren; `GET /healthz` am internen Dienst pruefen.
   Ein positives Healthz ist **noch kein** funktionierender
   Firestorm-Audiotest.
2. **Robust**: `[OGLVoice] Enabled=true`,
   `Mode=GridAuthority`, `EnableFirestormGateway=true`, echte
   `MediaGatewayUrl=https://<media-host>/internal/webrtc/v1/exchange`.
   URL muss ueber geschuetztes Netz/HTTPS auf den laufenden Gateway
   zeigen; keinen Platzhalter eintragen.
3. **Simulator**: `[NexVerseNodeAgent] Enabled=true`,
   die konfigurierte `PeerUrl`, der zum Gateway-ACL passende
   `NodeId` und `SharedKey` werden beim Start vom echten Prozess
   gelesen; lokale `[OGLVoice]` im GridManaged-Modus nicht unbedacht
   durch Standalone-Einstellungen ueberschreiben.
   `[OGLVoiceViewer] Enabled=true` oder Default.
4. **Geordnet Robust, Gateway und Simulator pruefen/starten**, nicht
   blind produktive Regionen neu starten.
   Auf `[OGL-VOICE]`-Meldungen achten. Der C#-Adapter protokolliert
   jetzt einmal pro Region, wenn der Provider beim CAPS-Seed
   nicht bereitsteht. Nach Spaet-Bereitstellung muss der Viewer ggf.
   erneut einloggen, da bereits ausgegebene CAPS nicht nachtraeglich
   automatisch erscheinen.
5. **Mit zwei Firestorm-Clients live testen:** WebRTC-CAPS, SDP-Offer/
   Answer, ICE-Konnektivitaet, beidseitiges Audio und Voice-Kugeln,
   danach HG/Parcel-/Regionswechsel pruefen.
   Nur eine funktionierende Medienverbindung beseitigt den
   **eigentlichen** Voice-Fehler.

## Neue Konsolenmeldungen

- `[OGL-VOICE]: No authenticated Firestorm WebRTC media provider ready ...`:
  Gateway/Robust-Discovery oder Session-CAPS nicht bereit.
- `[OGL-VOICE]: Firestorm WebRTC provider recovered ...`:
  nach Discovery-Neustart neue Logins erneut pruefen.
- `[OGL-PATH]: ... Terrain A* disabled ...`:
  nativer Pfadsucher in diesem Simulator nicht aktiv.
- `[OGL-PATH]: ... terrain A* configured ... RetrieveNavMeshSrc intentionally not advertised ...`:
  native Navigation aktiviert, aber Firestorm-NavMesh-Integration fehlt.
- `[OGL-PATH]: Terrain navigation ready ...`:
  nativer Terrain-Snapshot bereit; kein Firestorm-NavMesh.

## Abnahmegrenzen

Das neue Diagnose-PR bietet **Betriebsdiagnose und CI-Regressionen**.
Der Firestorm-NavMesh-Protokolladapter und eine vollstaendige
produktiv lauffaehige Voice-Infrastruktur sind damit **nicht**
implementiert oder freigegeben. Die Release-Gates aus
`RELEASE_TRAIN_0.9.3.10.md` bleiben weiterhin offen. Keine Stable-
Freigabe aus einem grünen CI-Build ableiten.
