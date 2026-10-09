# OpenGenesisLINK 0.9.3.10 – gemeinsamer Release-Train

**Aktive Entwicklung:** `OpenGenesisLINK 0.9.3.10 Dev`  
**Letzte veröffentlichte Stable-Version:** `OpenGenesisLINK 0.9.3.8` (Tag `v0.9.3.8`)  
**Freigabeziel:** `OpenGenesisLINK 0.9.3.10 Stable`; **keine separate Stable-Version 0.9.3.9**.  
**Status:** begonnen; KEIN RC und KEINE Stable-Freigabe. Ein grüner Build ersetzt keine Live-Abnahme.

Das Release bündelt den gesamten geplanten Meilenstein **0.9.3.9** (OGLVoice und Runtime) mit **0.9.3.10** (Pathfinding). 0.9.3.9 gilt erst als interner Entwicklungs-Checkpoint, wenn sein Funktionsumfang vollständig ist. Die Produktversion der gemeinsam entwickelten Branches ist `0.9.3.10 Dev`.

## A. Ausgangslage und erledigte Vorarbeiten

- 0.9.3.8 Stable veröffentlicht und unverändert als Downgrade-/Rollback-Basis erhalten.
- Pull Request #114: UDP-Watchdog-Leerlauf-Korrektur in `main`.
- Pull Request #115: zentrale OGLVoice-/LiveKit-Architektur, Robust/Standalone-Discovery, NexVerse Unlimited, HG-Gast-Voice und Anzeige der Voice-Kugeln über **allen** Teilnehmern dokumentiert; noch keine lauffähige Voice-Integration.
- Erste 0.9.3.10-Entwicklungsänderungen: geerbte Mono-/ThreadPool-Maximalbegrenzung aus Simulator-Startup entfernt; .NET-8-Poolwerte werden protokolliert. Produktive Vergleichs-/Lastmessungen offen.
- Pathfinding-Grundlage: testbarer deterministischer A*-Rasterpfadsucher mit begrenztem Arbeitsaufwand, Traversability-Snapshot, Hindernissen und Schutz vor diagonalem Corner-Cutting. **Noch kein NavMesh, keine Scene-/Physics-Anbindung, keine NPC-Steuerung und keine LSL-Implementierung**.

## B. Arbeitspaket OGLVoice (aus 0.9.3.9)

- [ ] Bestehenden NexVoice-/LiveKit-Quellcode einschließlich Credentials/Signalisierung sicher prüfen; keine Produktivschlüssel kopieren.
- [ ] OGLVoice Central Control Plane: Tenant-Registry, Auth, Räume, Tokens, Nutzungszählung, Revoke und Audit.
- [x] **Sicherer JWT-Kern und Robust-Token-Authority:** serverseitiges LiveKit-HS256-Signing, kurzlebige raumgebundene Tokens, Explicit-Node-ACL und signierte Simulator-Anfragen; standardmaessig deaktiviert. **Noch kein automatischer Viewer-CAPS-/Region-Join, kein LiveKit-Medien-Gateway und keine Quoten-/Revocation-Control-Plane.**
- [x] **Region-seitige lokale Pruefung:** aktuelle Root-Agent-/Circuit-Sitzung, Estate-/Parcel-Voice, Guest-Herkunft und NPC-Ausschluss; noch nicht an Firestorm/LiveKit angeschlossen.
- [ ] NexVerse als nicht selbst zuweisbaren First-Party-`unlimited`-Tenant, keine künstlichen User-/Sim-/Region-/Minutenlimits.
- [ ] Dritt-Grid-Isolation und konfigurierbare, atomar durchgesetzte Plan-Limits (Abrechnung später).
- [x] **Discovery v1:** C#-Robust-Authority und signierte/authentifizierte Simulator-Provider-Discovery mit bestehendem NexBus-SharedKey, Standalone-Modus und Replay-Abwehr; **nur Provider-Metadaten**, keine LiveKit-/Viewer-Sessions.
- [ ] **Produktivabnahme der Discovery:** gesonderter Zwei-Simulator-/TLS-/Proxy-Test im echten Grid ohne lokale Voice-Schluessel; keine Admin-Secrets im Region-INI.
- [ ] Standalone-Modus über `OpenSim.ini` und getrennte Simulator-Service-Credentials.
- [ ] HG-Gast-Präsenz und Home-Grid-Identität, Rechte des besuchten Grids, Session-TTL und Leave/Teleport-Revoke.
- [x] HG-Identitaetskern: kanonische Heim-Grid-Origin + Original-Avatar-UUID (SHA-256), C#-Regressionstests. **Noch keine authentifizierte HG-Session-/Voice-Anbindung.**
- [ ] Firestorm-WebRTC-CAPS-/Signalisierungs-/Media-Gateway mit funktionierendem Audio, nicht nur Browser-Demo.
- [x] **Erster realer Go Media-Gateway-Daemon:** Pion v4 terminiert SDP/ICE/DTLS/SRTP/SCTP, LiveKit Go-SDK publiziert Firestorm-Opus-RTP und leitet den ersten empfangenen fremden Opus-Track zum Viewer; echte LiveKit-Roster-/Speaking-Datenkanael-Meldungen, Node-Tenant-HMAC/Replay, Quoten, Session-Reaper. Eigenes Go-CI mit lokaler Pion-WebRTC-SDP-Verhandlung. **Noch kein simultaner Multi-Speaker-/Spatial-Audio-Mixer, kein echter Zwei-Firestorm-Plus-HG-Livetest, keine Stable-Freigabe.**
- [x] **Multi-Speaker-Opus-Spatial-Mixer (Dev):** eigene libopus-Decoder je LiveKit-Track, 20-ms-Stereo-Mix/Opus-Encoder je Firestorm-Zuhoerer, 40-m-Hoergrenze, Distanzdaempfung/Panning/Clipping, begrenzte Jitter-Puffer/Quellen, signierte Positions-/Heading-Updates aus `ScenePresence` und Eventweitergabe ueber LiveKit. Vollstaendige Race-/PCM-/RTP-/Audio-CI erforderlich; kein Nachweis im produktiven Grid.
- [ ] Operative LiveKit-/STUN-/TURN-Konfiguration, echtes Firestorm-Audio im Grid mit mehreren Sprechern, HRTF/Occlusion und Multi-Region-Audio, durchgaengige HG-Guest-Voice-Anzeige, sichere Revoke-Pipeline sowie Quota- und Belastungsabnahme.
- [x] **Firestorm-LLSD-CAPS/SDP/ICE-Adapter:** `ProvisionVoiceAccountRequest`, `VoiceSignalingRequest`, `VoiceServerType=webrtc` bei signierter und ausdruecklich freigeschalteter MediaGatewayUrl; signierte, beschraenkte JSON-Media-Exchange und .NET-Protokoll-Regressionen. Default `EnableFirestormGateway=false`. **Eine tatsaechliche kompatible SDP/DTLS/SRTP/SCTP-/LiveKit-Media-Bridge existiert dadurch noch NICHT.**
- [x] **Remote-Speaker-Wire-Format:** Firestorm-konforme JSON-Nachrichten `j`/ `p` / `v` / `l` nach wahrer Avatar-UUID samt Tests. **Noch keine LiveKit-Mediaquelle, die diese Nachrichten in echte WebRTC-Datenkanaele sendet.**
- [ ] Spatial-/Parcel-Voice mit Autorität des Simulators, Echtzeitpositionen und Distanz-/Richtungsmodell.
- [ ] Eigene **graue Voice-Kugel über allen hör- und sichtbar verbundenen Avataren**; Remote-Speaking-Wellen auf dem tatsächlichen Sprecher, inklusive HG-Gästen; Viewerpräferenz respektieren.
- [ ] Group Voice, Direktanruf, Push-to-Talk, Mute, Moderation, Parcel-/Regionwechsel.
- [ ] Multi-Simulator-/HG-/Firestorm-Tests, Token-Tamper-/Cross-Tenant-Tests, Last- und Ausfalltests.

Details: [OGLVOICE_ARCHITECTURE.md](OGLVOICE_ARCHITECTURE.md).

## C. Arbeitspaket Runtime (aus 0.9.3.9)

- [x] Historische `MONO_THREADS_PER_CPU`-Abfrage und ihre irreführende Startmeldung entfernt.
- [x] Feste, aus Mono-/Framework-Zeiten geerbte `ThreadPool.SetMaxThreads`-Begrenzung entfernt, .NET-8-Runtime verwaltet Worker-/IOCP-Grenzen selbst.
- [x] Betroffene Startmeldungen mit OGL-Präfix und deutschen Konsolentexten versehen.
- [ ] Vergleichsmessung vor/nach unter identischer Multi-Region-Last: CPU, Heap, GC-Pausen, Pool-Threads, Warteschlange, Paket-Queue, Tick-Latenz und Watchdog-Warnungen.
- [ ] Startup, Burst-Last, Wiederverbindung, Asset- und HG-Teleport-Stress mit simulierten und echten Clients abnehmen.

## D. Arbeitspaket Pathfinding (aus 0.9.3.10)

- [x] Bounded A*-Grid-Foundation als C#-Quellcode in `NexVerse.Core`; automatisierter Runtime-Test mit Hindernissen, Eckensperre, Budget und Snapshot-Rebuild.
- [ ] Region-NavMesh-Kacheln auf Basis von Terrain, Höhen, Neigung, Wasser, statischen Meshes und Walkability-Policies generieren.
- [x] **Experimenteller Terrain-Navigation-Snapshot:** initialer echter `Scene.Heightmap`-Adapter mit Wasser- und Steigungsprüfung, optionalen Hindernis-Masken im C#-Kern, interner Regionsschnittstelle und `OnTerrainTainted`-Invalidierung samt zeitgesteuertem Neuaufbau. Standardmäßig deaktiviert (`[OGLPathfinding] Enabled = false`); keine Mesh-/Physik-Kollisionsgarantie.
- [ ] Reale Region-/Avatar-Livetests und CPU-/GC-Profiling fuer Snapshot-Kopien und Rebuilds nach Terraforming; keine automatische NPC-/Script-Nutzung vor Abnahme.

- [ ] Editierbares Gelände, rezzed/deleted/verschobene Hindernisse und Dirty-Tile-Rebuild.
- [ ] Linksets, Treppen, Türen, Off-mesh-Links und path-cost areas; keine physikalisch ungültigen Abkürzungen.
- [ ] Agent-spezifische Navigation: Radius, Höhe, Steigung, erlaubte Bereiche und dynamische Kollisionen.
- [ ] NPC-/Character-Komponenten und kontrollierte Bewegungssteuerung mit Tick-/CPU-Budgets.
- [x] **Interner, experimenteller NPC-Wegpunkt-Follower:** `OglNpcPathFollowerModule` nutzt das bestehende `INPCModule.MoveToTarget`/Physik statt Teleports; prueft NPC-Eigentuemerrechte, aktive Region-Navigation, CPU-/Routenlimits, Fortschritt/Timeout und Stop bei Terraforming. Opt-in `[OGLNpcNavigation] Enabled=false`. **Noch kein LSL-Binding, kein echter Live-Test, keine Mesh-Hindernis-/NavMesh-Paritaet.**

- [ ] LSL-API-`llCreateCharacter`, `llDeleteCharacter`, `llNavigateTo`, `llPursue`, `llFleeFrom`, `llWanderWithin`, `llPatrolPoints`, `llGetClosestNavPoint` und `path_update` gemäß geprüfter aktueller SL-Signaturen; keine Stubs als erledigt zählen.
- [ ] Region-/Parzellenrechte, Script-Quota, Missbrauchsschutz und Lastgrenzen.
- [ ] Deterministischer CI-Regressionssatz und laufende Region mit tatsächlich bewegtem NPC und Live-Viewer-Abnahme.
- [ ] Region-Crossing für Pathfinding als definierte Sicherheits-/Migrationsanforderung prüfen; bei nicht erfülltem Cross-Sim-Support ehrlich als Einschränkung dokumentieren.

Der A*-Rasterkern ist **nur die erste Navigationsgrundlage**, keine vollständige Zusage der oben genannten Funktionen.

## E. Freigabe-Gates

- [ ] **Gate 1 – Functional:** OGLVoice in Robust/HG **und** Standalone; HG-Gäste in Voice; eigene/Remote-Voice-Kugeln; echter Firestorm-WebRTC-Audioverkehr; NavMesh/LSL/Character vollständig.
- [ ] **Gate 2 – CI:** Release-Build, bestehende Hypergrid-/Login-/NexBus-/NV$-/Inventory-Regressionen und neue Voice-/Navigationstests grün.
- [ ] **Gate 3 – Sicherheit:** Cross-Tenant-Isolation, HG-Identität, Tokens, Permission-Revocation, LSL-Ressourcenlimits, Secrets/Audit.
- [ ] **Gate 4 – Performance:** Messwerte gegen 0.9.3.8, keine neue relevante Speicher-/CPU-/GC-/Netzwerk-Regression, Watchdog bleibt aktiv.
- [ ] **Gate 5 – Operator:** Multi-Simulator-Live-Test mit zwei Firestorm-Clients, HG-Gast, Parcel-Wechsel, NPC in der Region, Restart/Failover und Backup/Rollback vom Betreiber bestätigt.
- [ ] **Gate 6 – Release:** `0.9.3.10 RC1` mit dokumentierten Einschränkungen; erst nach sämtlichen zwingenden Gates `0.9.3.10 Stable` taggen.

**Nicht freigeben**, solange das reale Firestorm-OGLVoice-Gateway oder die Region-/LSL-Navigation fehlen. Weder ein grüner Quellcode-Test noch der vorhandene externe NexVoice-Webdienst beweisen diese beiden Integrationen.

## F. Kompatibilität und Releasepolitik

- Die stabile Referenz `v0.9.3.8` bleibt verfügbar; kein Verschieben eines Tags.
- Der kombinierte Entwicklungszweig meldet `OpenGenesisLINK 0.9.3.10 Dev`; keine öffentliche Stable-Verwechslung.
- Keine separate Veröffentlichung `0.9.3.9 Stable`.
- OGLVoice standardmäßig inaktiv, bis die vollständige sichere Provider-/Viewer-Integration abgenommen ist.
- Keine unechte Pathfinding-LSL-Kompatibilität bewerben oder unterstützte LSL-Funktionen als blind funktionierende Stubs implementieren.
- Alle zusätzlichen Features nach 0.9.3.10 gehören zu späteren Release-Trains.
