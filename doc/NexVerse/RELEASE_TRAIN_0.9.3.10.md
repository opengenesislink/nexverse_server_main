# OpenGenesisLINK 0.9.3.10 – Runtime-, Pathfinding- und Experiences-Release-Train

**Aktive Entwicklung:** `OpenGenesisLINK 0.9.3.10 Dev`  
**Letzte veröffentlichte Stable-Version:** `OpenGenesisLINK 0.9.3.8` (Tag `v0.9.3.8`)  
**Freigabeziel:** `OpenGenesisLINK 0.9.3.10 Stable`; **keine separate Stable-Version 0.9.3.9**.  
**Status:** begonnen; KEIN RC und KEINE Stable-Freigabe. Ein grüner Build ersetzt keine Live-Abnahme.

**Scope-Entscheidung vom 10.10.2026:** `0.9.3.10 Dev` konzentriert sich auf die bereits begonnenen **.NET-8-Runtime-Modernisierungen, Pathfinding (NavMesh, NPC/Character, Firestorm-Schnittstelle und LSL) und vollstaendige NexExperiences-Integration (Viewer-CAPS, Zustimmung/Revoke, LSL, zentrale Persistenz)**. Der frueher fuer 0.9.3.9 eingeplante **OGLVoice-Funktionsumfang ist vollstaendig in einen spaeteren, noch nicht terminierten Arbeits-/Release-Train verschoben**. Es gibt weiterhin **keine separate 0.9.3.9 Stable-Version**. Bereits entwickelter Voice-Code bleibt unveraendert im Repository, jedoch standardmaessig abgeschaltet; vorhandene CI-/Sicherheits-Regressionen bleiben erhalten. **Weder OGLVoice-Produktivabnahme noch Media-Gateway-Installation sind 0.9.3.10-Freigabebedingungen.**

## A. Ausgangslage und erledigte Vorarbeiten

- 0.9.3.8 Stable veröffentlicht und unverändert als Downgrade-/Rollback-Basis erhalten.
- Pull Request #114: UDP-Watchdog-Leerlauf-Korrektur in `main`.
- Pull Request #115 und spaetere OGLVoice-Branches: vorhandene Architektur-, Discovery- und Media-Gateway-Bausteine bleiben fuer den spaeteren Voice-Train erhalten; kein produktiver Voice-Livetest abgenommen.
- Erste 0.9.3.10-Entwicklungsänderungen: geerbte Mono-/ThreadPool-Maximalbegrenzung aus Simulator-Startup entfernt; .NET-8-Poolwerte werden protokolliert. Produktive Vergleichs-/Lastmessungen offen.
- Pathfinding-Grundlage: deterministischer A*-Rasterkern, experimenteller Scene-Heightmap-Adapter und interner NPC-Wegpunkt-Follower mit CI-Tests. **Noch kein Firestorm-kompatibles NavMesh, keine umfassende Mesh-/Physikkollision, keine produktiv abgenommene NPC-Bewegung und keine vollstaendige Pathfinding-LSL-API.**

## A.1 Firestorm Viewer – Inventar-Thumbnail und Outfit-Diagnose

- [x] **InventoryThumbnailUpload Dev-CAP:** Zweistufige LLSD-Upload-URL und JPEG2000-Binaerupload nach Firestorm-Vertrag, mit Owner-/Root-Agent-Pruefung, einmaligen 60-s-Uploadern, 1-MiB-Begrenzung und persistenter Textur-Asset-Speicherung. Unter `[ClientStack.LindenCaps] Cap_InventoryThumbnailUpload = "localhost"` automatisch registriert. Schließt den fehlenden CAP-Namen im Firestorm-Seed.
- [x] **INV01 – offizielle Dev-Implementierung abgeschlossen; bekannte Firestorm-Kompatibilitaetseinschraenkung akzeptiert (10.10.2026):** `InventoryThumbnailUpload`, Asset-Speicherung, persistente Item-/Ordner-Thumbnail-UUIDs und `FetchInventory2`/`FetchInventoryDescendents2` sind serverseitig implementiert. Firestorm 7.2.4.80712 kann das Thumbnail in der getesteten OpenSim/OpenGenesisLINK-Konfiguration hochladen und nach Inventar-Cache-Neuaufbau anzeigen. **Bekannte Einschraenkung:** Beim **Ersetzen** eines bestehenden Outfit-Ordner-Snapshots aktualisiert Firestorm die Vorschau nach normalem Neustart mit bestehendem Inventarcache nicht zuverlaessig (Standardordnersymbol). Ein einmaliges Loeschen des **Inventarcaches** behebt die Anzeige fuer das gespeicherte neue Bild. Kein DB-Datenverlust. Die genaue Verursachung von Server-Versionsabgleich gegenueber Viewer-Cache ist nicht abschliessend bewiesen; die Einschraenkung gilt **nur fuer diese getestete Kombination**, nicht pauschal fuer alle Firestorm-Versionen oder OpenSim-Grids. INV01 ist **als Known Limitation geschlossen, nicht als Cache-Fix bestanden**. #127/#128 sind gemergt. Diese akzeptierte Einschraenkung ist kein eigener Stable-Blocker mehr; andere Release-Gates bleiben unveraendert.
- [x] **Dauerhafte Inventar-/Outfit-Thumbnail-Verknuepfungen im Code:** Neues `ThumbnailID` in `InventoryItemBase` und `InventoryFolderBase`, XInventory-Konverter und Robust-Simulator-HTTP-Connectoren (auch bei getrennt gehostetem Inventardienst), Besitzer-gebundene Speicherung nach erfolgreichem Bild-Upload, additive MySQL/MariaDB-, PostgreSQL- und SQLite-Migration sowie Firestorm-konforme `thumbnail.asset_id`-LLSD-Serialisierung in Item- und Folder-Fetch. **MariaDB-Persistenz und Abruf nach Cache-Neuaufbau live nachgewiesen; vollstaendige Datenbank-Rollout-/Backup-Pruefung fuer Stable separat offen.**
- [x] **Idempotenz-Fix Inventar-Systemordner:** Das bisherige `GetSystemFolders`-Filter `type > 0` schloss den gueltigen `Texture`-Typ 0 aus und erstellte bei erneutem `CreateUserInventory` einen zusaetzlichen `Textures`-Ordner. Filter korrigiert und durch den SQLite-Laufzeittest abgesichert. Bereits vorhandene Duplikate werden **nicht** automatisch geloescht.
- [x] **Firestorm Outfit-Gallery Server-Fix (Dev):** `My Outfits` wird fuer neue Residenten (inkl. HG-Suitcase-Inventar) idempotent als eigener Systemordner provisioniert. Fuer bestehende Residenten: `GET /api/v1/inventory/outfits/health` (`inventory:read`) mit COF-/Saved-Outfit-/defekten Link-Checks ohne Datenveraenderung, `POST /api/v1/inventory/outfits/ensure-folders` (`inventory:write`) legt ausschliesslich den fehlenden `My Outfits`-Systemordner an. Systemordner vor Portal-Loeschen/Verschieben geschuetzt; Thumbnail-UUIDs im Portal-Inventar sichtbar. SQLite-Laufzeitregression unter CI. **Gespeicherter Outfit-Eintrag in der Firestorm-Galerie nach Inventar-Cache-Neuaufbau live sichtbar; COF/`Kein Outfit` bleibt separater offener Abnahmepunkt.**
- [ ] **Firestorm Appearance "Kein Outfit" live abnehmen:** Current-Outfit-/My-Outfits-Ordner, gespeicherte Outfits, vorhandene Links und Avatar-Appearance vor Ort pruefen. Kein automatischer, potentiell zerstoererischer Reset existierender Nutzeroutfits.
- [x] **Viewer-Thumbnail-E2E mit dokumentierter Einschraenkung abgeschlossen:** Neuer Snapshot, MariaDB-Persistenz, Versionsanstieg, Anzeige nach Cache-Neuaufbau und fehlerhafte Aktualisierung nach normalem Relog reproduziert. **Kein** bestandener automatischer Cache-Refresh-Test; Fehler als Firestorm-7.2.4-OpenSim-Kompatibilitaetslimit akzeptiert. `Kein Outfit`/COF ist davon **nicht** abgenommen.

## A.2 Firestorm-Log: Voice/Pathfinding fehlende Capabilities (10.10.2026)

- [x] **Analyse und Debugbarkeit:** Firestorm 7.2.4.80712 meldet
  `RetrieveNavMeshSrc` als fehlend und faellt beim Voice-Account-Setup
  auf Legacy-Vivox mit leerer POST-URL zurueck.
  Eigene Servermeldungen fuer ausgeschaltete/noch nicht bereite
  OGLVoice-Provider und nativen A*-Status sowie ein schluesselfreies
  read-only-Konfigurations-Preflight plus CI-Tests dokumentiert.
  Details: `doc/NexVerse/FIRESTORM_VOICE_PATHFINDING_LOG_20261010.md`.
- [ ] **Aktiver Pathfinding-Freigabeblocker:** `RetrieveNavMeshSrc`
  benoetigt ein echtes Firestorm-kompatibles NavMesh mit gueltigen
  binären Daten, Versions-/Status-Endpunkt und gepruefter Abnahme;
  der OGL-Terrain-A*-Snapshot allein leistet das noch nicht.
  Keine Platzhalter-CAPS oder fingierten Erfolge.
- [x] **Voice bewusst verschoben:** Der beobachtete Firestorm-Vivox-
  Fallback bleibt als bekannter Zustand dokumentiert. Die noch
  fehlende produktive OGLVoice-/Pion-/LiveKit-Integration ist
  **kein Blocker fuer 0.9.3.10**, weil die Funktion aus dem Scope
  entfernt wurde. Kein fingierter Vivox- oder WebRTC-Endpunkt.

## B. OGLVoice – zurueckgestelltes Arbeitspaket (nicht Teil von 0.9.3.10)

**Status: PAUSIERT, Stand 10.10.2026.** Diese Checkliste ist als technische Projektuebergabe archiviert und wird erst in einem spaeteren, eigenstaendig zu definierenden Release-Train weiterbearbeitet. Keiner der offenen Voice-Punkte unten ist eine Freigabebedingung fuer 0.9.3.10. Kein Gateway-Rollout, kein Provider-/WebRTC-Enable und keine erzwungene Vivox-Ersatzkonfiguration. Bereits implementierte Module, Tests und Dokumentation bleiben erhalten; bestehende Sicherheitsstandards und CI-Regressionen werden nicht abgeschaltet.

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

- [ ] LSL-API-`llCreateCharacter`, `llDeleteCharacter`, `llNavigateTo`, `llPursue`, `llFleeFrom`, `llWanderWithin`, `llPatrolPoints` und `path_update` gemäß geprüfter aktueller SL-Signaturen; keine Stubs als erledigt zählen.
- [x] **Teilimplementierung `llGetClosestNavPoint` (Dev, Terrain-only):** vorhandener Terrain-Snapshot liefert einen begrenzten naechstbegehbaren Punkt und YEngine erreicht die Funktion ueber das neue native Regionsinterface; Optionen/Radius werden validiert, nicht unterstuetzte Character-/Dynamic-Mesh-Abfragen liefern leere Liste. **Nicht** als voller SL-/Multi-Layer-NavMesh- oder Live-Firestorm-Nachweis zu verstehen.
- [ ] Region-/Parzellenrechte, Script-Quota, Missbrauchsschutz und Lastgrenzen.
- [ ] Deterministischer CI-Regressionssatz und laufende Region mit tatsächlich bewegtem NPC und Live-Viewer-Abnahme.
- [ ] Region-Crossing für Pathfinding als definierte Sicherheits-/Migrationsanforderung prüfen; bei nicht erfülltem Cross-Sim-Support ehrlich als Einschränkung dokumentieren.

Der A*-Rasterkern ist **nur die erste Navigationsgrundlage**, keine vollständige Zusage der oben genannten Funktionen.

## D.1 NexExperiences – volle LSL-/Firestorm-/Resident-Funktion (neu im 0.9.3.10-Scope)

- [x] **Bestehende Grundlage aus 0.9.3.7:** Verwaltungs-/Berechtigungs-/Script-Binding-/K/V-Autoritaet, authentifizierte World API, Region-Adapter und wesentliche LSL-Funktionen inklusive YEngine-Events. Diese Checkliste betrifft die **zusaetzliche vollstaendige Viewer-/Live-Paritaet**, nicht eine pauschale Ruecknahme bereits implementierten Codes.
- [x] **Sicherheits-Hardening (Dev):** Deaktivierte Experiences verweigern auch bereits gebundenen Scripts jede K/V-Operation; Service-API-Schluessel mit nur `experiences:manage` duerfen keine frei waehlbaren Resident-Owner/-Akteure imitieren. Runtime-Regression und CI-Guards vorgesehen.
- [ ] **Firestorm-CAPS und UI:** `GetExperienceInfo` und `FindExperienceByName` als **optionaler, lesender Dev-Adapter** mit serverseitig gehaltenem `experiences:script`-Schluessel, begrenztem Backend und LLSD umgesetzt. Opt-in `[NexExperiencesViewer] FirestormReadCaps = false` bleibt Standard; Viewer-Livetest steht aus. Schreibende CAPS, `GetExperiences`, `AgentExperiences`, `ExperiencePreferences`, `GroupExperiences`, `GetAdminExperiences`, `GetCreatorExperiences` sowie komplette Owner-/Admin-/Contributor-Listen und Experience-Floater stehen weiterhin aus.
- [ ] **Echte Einwilligung:** `llRequestExperiencePermissions` muss Agent-Prompt/Allow/Block/Forget und `experience_permissions(_denied)` authentifiziert, dauerhaft und widerrufbar unterstuetzen; **keine** implizite Genehmigung fuer unbekannte Residents.
- [ ] **Vollstaendige LSL-Paritaet:** Experience-KV-`dataserver`-Events, `llGetExperienceDetails`, `llAgentInExperience`, XP_ERROR-Codes, Ratenlimits, Scriptidentitaet/-besitzer, deaktivierte Experiences, Fehler/Timeouts und Landrechte im echten YEngine-/Viewer-Test.
- [ ] **Persistenz/Betrieb:** Der bestehende JSON-Store ist nicht als transaktionaler Multi-Robust-Cluster belegt. Migrations-/Single-Writer- oder SQL-Konzept, konsistente K/V-Updates und Restart-/Backup-/Recovery-Test fuer produktiven Grid-Betrieb.
- [ ] **E2E-Abnahme:** Zwei Residents, eine Besitzer-Experience, ein fremdes Objekt, Allow/Block/Forget, disabled/revoked, Parcel-/Estate-Limits, gruppenbasierte Rollen, HG-Gast, Viewer-Floater, Script-Dataserver und Neustarts auf mehreren Simulatoren.
- Referenz: `doc/NexVerse/PATHFINDING_EXPERIENCES_FULL_PARITY_09310.md`.

## E. Freigabe-Gates

- [ ] **Gate 1 – Functional:** Echtes Region-NavMesh, Firestorm-kompatible `RetrieveNavMeshSrc`-/Status-CAPS, Character-/NPC-Bewegung und Pathfinding-LSL samt `path_update`; **zusaetzlich NexExperiences-Viewer-CAPS, Resident-Zustimmung/Revoke, zentrale Rechte/Persistenz und Experience-LSL im Live-Firestorm-Test**. OGLVoice ist nicht Teil dieses Gates.
- [ ] **Gate 2 – CI:** Release-Build, bestehende Hypergrid-/Login-/NexBus-/NV$-/Inventory-Regressionen und neue Pathfinding-/NavMesh-/Experience-/LSL-/Permission-Tests gruen. Bereits aktive Voice-CI bleibt als Regression fuer eingecheckten Code erhalten, ohne neue Voice-Features als Abnahmebedingung zu setzen.
- [ ] **Gate 3 – Sicherheit:** Navigator-/NPC-/LSL-Region- und Estate-Berechtigungen sowie Experience-Resident-Zustimmung, Scriptbindung, Owner/Admin-Delegation, Land-/HG-Rechte, K/V-Quoten und Missbrauchslimits sowie unveraenderte Login-/Token-/Secret-Sicherheitsgrenzen pruefen; keine neue Voice-Produktivfreigabe.
- [ ] **Gate 4 – Performance:** Messwerte gegen 0.9.3.8, keine neue relevante Speicher-/CPU-/GC-/Netzwerk-Regression, Watchdog bleibt aktiv.
- [ ] **Gate 5 – Operator:** Native Pathfinding-Livetests in realer Region mit bewegtem NPC, Terraforming/Hindernissen sowie echte Experience-Viewer- und LSL-Zustimmungs-/Revoke-Livetests mit zwei Bewohnern; Firestorm und Multi-Simulator-/HG-Regressionen, Restart/Failover sowie Backup/Rollback durch Betreiber bestaetigt. Keine verpflichtenden Voice-/Parcel-Audio-Tests.
- [ ] **Gate 6 – Release:** `0.9.3.10 RC1` mit dokumentierten Einschränkungen; erst nach sämtlichen zwingenden Gates `0.9.3.10 Stable` taggen.

**Nicht freigeben**, solange Region-NavMesh, Firestorm-Pathfinding-Anbindung, Character-/NPC-Livebewegung und LSL-Navigation **oder** die vollstaendige NexExperiences-Viewer-/LSL-/Einwilligungsintegration nicht gemaess Gate 1 abgenommen sind. **Das OGLVoice-Gateway ist aufgrund der ausdruecklichen Scope-Verschiebung kein 0.9.3.10-Stable-Blocker.** CI allein ersetzt die Pathfinding-Livetests nicht.

## F. Kompatibilität und Releasepolitik

- Die stabile Referenz `v0.9.3.8` bleibt verfügbar; kein Verschieben eines Tags.
- Die aktive Produktversion bleibt `OpenGenesisLINK 0.9.3.10 Dev` fuer Runtime, Pathfinding und Experiences; keine oeffentliche Stable-Verwechslung.
- Keine separate Veröffentlichung `0.9.3.9 Stable`.
- OGLVoice bleibt standardmaessig inaktiv und ist ausdruecklich auf einen spaeteren Entwicklungsabschnitt vertagt. Vorhandenen Code, Tests und Sicherheitsgrenzen beibehalten; keine produktive Gateway-Aktivierung zur Erfuellung dieses Release-Trains.
- Keine unechte Pathfinding-LSL-Kompatibilität bewerben oder unterstützte LSL-Funktionen als blind funktionierende Stubs implementieren.
- Alle zusätzlichen Features nach 0.9.3.10 gehören zu späteren Release-Trains.
