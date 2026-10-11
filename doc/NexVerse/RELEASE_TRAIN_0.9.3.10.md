# OpenGenesisLINK 0.9.3.10 – Runtime-, Pathfinding- und Experiences-Release-Train

**Aktive Entwicklung:** `OpenGenesisLINK 0.9.3.10 Dev`  
**Letzte veröffentlichte Stable-Version:** `OpenGenesisLINK 0.9.3.8` (Tag `v0.9.3.8`)  
**Freigabeziel:** `OpenGenesisLINK 0.9.3.10 Stable`; **keine separate Stable-Version 0.9.3.9**.  
**Status:** begonnen; KEIN RC und KEINE Stable-Freigabe. Ein grüner Build ersetzt keine Live-Abnahme.

**Ueberholte Scope-Entscheidung vom 10.10.2026 (siehe Aktualisierung unten):** `0.9.3.10 Dev` konzentriert sich auf die bereits begonnenen **.NET-8-Runtime-Modernisierungen, Pathfinding (NavMesh, NPC/Character, Firestorm-Schnittstelle und LSL) und vollstaendige NexExperiences-Integration (Viewer-CAPS, Zustimmung/Revoke, LSL, zentrale Persistenz)**. Der frueher fuer 0.9.3.9 eingeplante **OGLVoice-Funktionsumfang ist vollstaendig in einen spaeteren, noch nicht terminierten Arbeits-/Release-Train verschoben**. Es gibt weiterhin **keine separate 0.9.3.9 Stable-Version**. Bereits entwickelter Voice-Code bleibt unveraendert im Repository, jedoch standardmaessig abgeschaltet; vorhandene CI-/Sicherheits-Regressionen bleiben erhalten. **Weder OGLVoice-Produktivabnahme noch Media-Gateway-Installation sind 0.9.3.10-Freigabebedingungen.**

**Scope-Aktualisierung 11.10.2026 (verbindlich):** 0.9.3.10 liefert native, auf real geprueften Kollisionsoberflaechen beruhende OpenGenesisLINK-Navigation als **opt-in** Serverfunktion und NexExperiences fuer Firestorm. Eine vollstaendige Havok-kompatible Firestorm-NavMesh-Darstellung (und Character-/Dynamic-Pathfinding-LSL-Paritaet) wird mit dem eigenen Viewer in einem **spaeteren, noch nicht terminierten Release** fortgefuehrt. Sie ist **KEIN 0.9.3.10-Stable-Gate**. Kein leeres oder gefaelschtes `RetrieveNavMeshSrc`, keine Freischaltung grauer Firestorm-Menues. Native Wegsuche, NPC-Physikmotor und die tatsaechlich angebotenen LSL-Funktionen benoetigen trotzdem Code-/Sicherheits- und reale Regionsabnahmen; nicht implementierte Second-Life-LSL-Funktionen werden nicht als fertig bezeichnet. Experiences inklusive Zustimmung, Widerruf, zentraler Persistenz und Firestorm-Livetests **bleiben** verpflichtend. OGLVoice bleibt ausgeschlossen.

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
- [x] **Firestorm-NavMesh auf spaeteren Viewer-Train verschoben:** `RetrieveNavMeshSrc` benoetigt echte Firestorm-kompatible Binaerdaten und wird nicht vorgetaeuscht. Graue Firestorm-Pathfinding-Menues sind fuer 0.9.3.10 eine **dokumentierte Viewer-Einschraenkung und kein Stable-Blocker**. Native OGL-Navigation sowie die tatsaechlich implementierten LSL-/NPC-Funktionen muessen unabhaengig davon getestet werden.
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

**Scope-Abgrenzung:** Die offenen Aufgaben unten beschreiben teilweise die langfristige Vollparitaet. Die Firestorm-Havok-NavMesh-Kette, noch nicht implementierte dynamische SL-Character-LSL-APIs und die spaetere OGL-Viewer-Integration sind **keine** zwingenden 0.9.3.10-Stable-Funktionen. Fuer Stable zwingend bleiben korrekte Fail-closed-3D-Navigation, Versions-/Dirty-Handling, sichere native NPC-Bewegung im explizit unterstuetzten Umfang und regionale Live-/Lastabnahme. Bei fehlender Physik-Unterstuetzung opt-in ausschalten, nicht zertifiziert ausgeben.

- [x] Bounded A*-Grid-Foundation als C#-Quellcode in `NexVerse.Core`; automatisierter Runtime-Test mit Hindernissen, Eckensperre, Budget und Snapshot-Rebuild.
- [ ] Region-NavMesh-Kacheln auf Basis von Terrain, Höhen, Neigung, Wasser, statischen Meshes und Walkability-Policies generieren.
- [x] **Experimenteller Terrain-Navigation-Snapshot:** initialer echter `Scene.Heightmap`-Adapter mit Wasser- und Steigungsprüfung, optionalen Hindernis-Masken im C#-Kern, interner Regionsschnittstelle und `OnTerrainTainted`-Invalidierung samt zeitgesteuertem Neuaufbau. Standardmäßig deaktiviert (`[OGLPathfinding] Enabled = false`); keine Mesh-/Physik-Kollisionsgarantie.
- [ ] Reale Region-/Avatar-Livetests und CPU-/GC-Profiling fuer Snapshot-Kopien und Rebuilds nach Terraforming; keine automatische NPC-/Script-Nutzung vor Abnahme.

- [ ] Editierbares Gelände, rezzed/deleted/verschobene Hindernisse und Dirty-Tile-Rebuild.
- [ ] Linksets, Treppen, Türen, Off-mesh-Links und path-cost areas; keine physikalisch ungültigen Abkürzungen.
- [x] **Mehrschichtiger Graphkern als Dev-Teilbaustein:** `OglLayeredNavGraph` unterstuetzt getrennte Hoehenebenen und explizit gepruefte Kanten/Portale. Die mittlerweile integrierten opt-in Physics-Raycast-Oberflaechen, geprueften Treppen-/Rampen-Uebergaenge und Native-LSL-/NavGraph-Anbindungen bleiben experimentell und muessen live validiert werden. **Kein Firestorm-Havok-NavMesh** und kein pauschaler Nachweis fuer jede Mesh-, Linkset- oder Tuergeometrie. Dokumentation: `doc/NexVerse/LAYERED_NAVIGATION_CORE_09310.md`.
- [ ] Agent-spezifische Navigation: Radius, Höhe, Steigung, erlaubte Bereiche und dynamische Kollisionen.
- [ ] NPC-/Character-Komponenten und kontrollierte Bewegungssteuerung mit Tick-/CPU-Budgets.
- [x] **Interner, experimenteller NPC-Wegpunkt-Follower:** `OglNpcPathFollowerModule` nutzt `INPCModule.MoveToTarget` und prueft NPC-Eigentuemerrechte/Quoten/Timeout. Seit dem 11.10.2026-Branch fordert der Follower eine 3D-hoehengepruefte Route an (bei Opt-in aus zertifiziertem Multi-Layer-Graph), prueft Wegpunkte in XYZ und verwechselt Boden nicht mit einer Bruecke bei gleichen XY. Opt-in `[OGLNpcNavigation] Enabled=false`. **Noch kein LSL-Character-Binding und keine echte NPC-/Mesh-/Last-Live-Abnahme.**

- [ ] LSL-API-`llCreateCharacter`, `llDeleteCharacter`, `llNavigateTo`, `llPursue`, `llFleeFrom`, `llWanderWithin`, `llPatrolPoints` und `path_update` gemäß geprüfter aktueller SL-Signaturen; keine Stubs als erledigt zählen.
- [x] **Teilimplementierung `llGetClosestNavPoint` (Dev, Terrain-only):** vorhandener Terrain-Snapshot liefert einen begrenzten naechstbegehbaren Punkt und YEngine erreicht die Funktion ueber das neue native Regionsinterface; Optionen/Radius werden validiert, nicht unterstuetzte Character-/Dynamic-Mesh-Abfragen liefern leere Liste. **Nicht** als voller SL-/Multi-Layer-NavMesh- oder Live-Firestorm-Nachweis zu verstehen.
- [ ] Region-/Parzellenrechte, Script-Quota, Missbrauchsschutz und Lastgrenzen.
- [ ] Deterministischer CI-Regressionssatz und laufende Region mit tatsächlich bewegtem NPC und Live-Viewer-Abnahme.
- [ ] Region-Crossing für Pathfinding als definierte Sicherheits-/Migrationsanforderung prüfen; bei nicht erfülltem Cross-Sim-Support ehrlich als Einschränkung dokumentieren.

Der A*-Rasterkern ist **nur die erste Navigationsgrundlage**, keine vollständige Zusage der oben genannten Funktionen.

## D.1 NexExperiences – volle LSL-/Firestorm-/Resident-Funktion (neu im 0.9.3.10-Scope)

- [x] **Bestehende Grundlage aus 0.9.3.7:** Verwaltungs-/Berechtigungs-/Script-Binding-/K/V-Autoritaet, authentifizierte World API, Region-Adapter und wesentliche LSL-Funktionen inklusive YEngine-Events. Diese Checkliste betrifft die **zusaetzliche vollstaendige Viewer-/Live-Paritaet**, nicht eine pauschale Ruecknahme bereits implementierten Codes.
- [x] **Sicherheits-Hardening (Dev):** Deaktivierte Experiences verweigern auch bereits gebundenen Scripts jede K/V-Operation; Service-API-Schluessel mit nur `experiences:manage` duerfen keine frei waehlbaren Resident-Owner/-Akteure imitieren. Runtime-Regression und CI-Guards vorgesehen.
- [ ] **Firestorm-CAPS und UI:** Bereits implementiert: `GetExperienceInfo` und `FindExperienceByName` (opt-in lesender Metadaten-Adapter); `GetExperiences` und `ExperiencePreferences` mit avatar-gebundenem, gesondert geschuetztem Allow/Block/Forget (opt-in); jetzt ebenfalls **lesende** `AgentExperiences`, `GetAdminExperiences`, `GetCreatorExperiences` und `GroupExperiences` fuer Firestorms Listen. Standard bleibt `FirestormReadCaps=false` und `FirestormPermissionCaps=false`. Offen: produktive Einwilligungsdialoge bei Script-Anfragen, neue Experience anlegen/kaufen/bearbeiten im Viewer, gruppenbezogene Mitgliedschafts-/Rechtepruefung fuer weitergehende Schreibfunktionen, korrekte UI-/Live-Abnahme und alle bislang nicht umgesetzten Verwaltungscapabilities. **Kein Stable-Nachweis**.
- [x] **Bewohner-Selbstverwaltung als opt-in Dev-Backend:** Isolierter scope `experiences:viewer:permissions` (nur admin-vergebener Service-Key), zentrale `SetOwnResidentPermission`-Operation mit Audit und Persistenz, `GetExperiences` + `ExperiencePreferences` fuer Firestorm mit Allow/Block/Forget und nur zum aktiven Avatar gebundenen CAPS. Default `FirestormPermissionCaps=false`; Live-Verifikation und Script-Ausloesung stehen aus.
- [x] **Teilfunktion: explizit freizugebende, wartende LSL-Einwilligung:** Optionales `ScriptPendingConsent=false` standardmaessig; Script-Anfragen mit Berechtigungsstatus `none` werden nur bei aktivem Agent und passender Parcel-/Scriptbindung zeitlich begrenzt zwischengespeichert. Nach ausdruecklichem Allow/Block ueber die bereits vorhandene Firestorm-`ExperiencePreferences`-CAP erfolgt zentrale Neupruefung und das passende LSL-Ereignis. Timeout, Quoten, Region-Shutdown und getrennte Bewohner sind abgesichert. Bei `NativeExperiencePrompt=true` kann der Server mittlerweile den echten optionalen Firestorm-`ScriptQuestionExperience`-Prompt senden; **ein Viewer-`ScriptAnswerYes` ohne authentifizierte `ExperiencePreferences`-Persistierung ist niemals ein Allow**. Ohne native Transport-Unterstuetzung bleibt der Hinweis rein informativ. Echte Firestorm-Tests sind offen.
- [ ] **Echte Einwilligung (Live-Abnahme offen):** Die native opt-in `ScriptQuestionExperience`-Transportschicht, avatar-gebundene `ExperiencePreferences`-Persistierung, `experience_permissions(_denied)`, CAP-Lease-Revoke und Script-Reset-/Relog-Cleanup sind im Dev-Code implementiert und CI-getestet. **Noch kein bestaetigter durchgehender Firestorm-Allow/Block/Forget-Ablauf mit zwei realen Avataren**; keine automatische Berechtigung unbekannter Residents.
- [ ] **Vollstaendige LSL-Paritaet:** Experience-KV-`dataserver`-Events, `llGetExperienceDetails`, `llAgentInExperience`, XP_ERROR-Codes, Ratenlimits, Scriptidentitaet/-besitzer, deaktivierte Experiences, Fehler/Timeouts und Landrechte im echten YEngine-/Viewer-Test.
- [ ] **Persistenz/Betrieb:** Der bestehende JSON-Store ist nicht als transaktionaler Multi-Robust-Cluster belegt. Migrations-/Single-Writer- oder SQL-Konzept, konsistente K/V-Updates und Restart-/Backup-/Recovery-Test fuer produktiven Grid-Betrieb.
- [ ] **E2E-Abnahme:** Zwei Residents, eine Besitzer-Experience, ein fremdes Objekt, Allow/Block/Forget, disabled/revoked, Parcel-/Estate-Limits, gruppenbasierte Rollen, HG-Gast, Viewer-Floater, Script-Dataserver und Neustarts auf mehreren Simulatoren.
- Referenz: `doc/NexVerse/PATHFINDING_EXPERIENCES_FULL_PARITY_09310.md`.

## E. Freigabe-Gates

Die folgenden Gates ersetzen fruehere pauschale Anforderungen an Firestorm-Havok-NavMesh und die gesamte Second-Life-Character-API. Keine Checkliste wird allein durch diesen Dokumentationswechsel als bestanden markiert.

- [ ] **Gate 1 – Functional:** Der explizit unterstuetzte **native** OpenGenesisLINK-Navigationsumfang (verifizierte 3D-Oberflaechen, A*-Routen, Snapshot-Invalidierung, 3D-NPC-Wegpunkte, vorhandene statische LSL-Wegsuche) muss mit echten Physikkollisionen und in einer Live-Region korrekt und fehlersicher laufen; nicht unterstuetzte SL-Character-APIs bleiben dokumentiert/deaktiviert. **Zusaetzlich NexExperiences** mit Firestorm-Viewer-CAPS, echter Resident-Zustimmung/Widerruf, LSL/KV, zentraler Rechte-Persistenz und Live-Firestorm-Test. **Firestorm-Havok-NavMesh, die zukuenftige eigene Viewer-Darstellung und OGLVoice sind nicht Teil dieses Gates.**
- [ ] **Gate 2 – CI:** Release-Build, bestehende Hypergrid-/Login-/NexBus-/NV$-/Inventory-Regressionen und neue Pathfinding-/NavMesh-/Experience-/LSL-/Permission-Tests gruen. Bereits aktive Voice-CI bleibt als Regression fuer eingecheckten Code erhalten, ohne neue Voice-Features als Abnahmebedingung zu setzen.
- [ ] **Gate 3 – Sicherheit:** Navigator-/NPC-/LSL-Region- und Estate-Berechtigungen sowie Experience-Resident-Zustimmung, Scriptbindung, Owner/Admin-Delegation, Land-/HG-Rechte, K/V-Quoten und Missbrauchslimits sowie unveraenderte Login-/Token-/Secret-Sicherheitsgrenzen pruefen; keine neue Voice-Produktivfreigabe.
- [ ] **Gate 4 – Performance:** Messwerte gegen 0.9.3.8, keine neue relevante Speicher-/CPU-/GC-/Netzwerk-Regression, Watchdog bleibt aktiv.
- [ ] **Gate 5 – Operator:** Native OGL-Wegsuche/NPC-Livetests auf realen 3D-Oberflaechen (auch Boden/Bruecke, Treppe/Rampe, gesperrte Kanten, Terraforming/Hindernis-Rebuild) mit funktionierender Bewegung und dokumentierten physikalischen Grenzen; echte Experience-Viewer-/LSL-Zustimmungs-/Widerrufs-Tests mit zwei Bewohnern, Firestorm, Multi-Simulator-/HG-Regressionen, Neustart/Failover und Backup/Rollback. **Kein Firestorm-NavMesh-Menue- oder Voice-Gate.**
- [ ] **Gate 6 – Release:** `0.9.3.10 RC1` mit dokumentierten Einschränkungen; erst nach sämtlichen zwingenden Gates `0.9.3.10 Stable` taggen.

**Nicht freigeben**, solange der vereinbarte native OGL-Navigationsumfang mit realen Regions-/NPC-/LSL-Tests **oder** die vollstaendige NexExperiences-Viewer-/LSL-/Einwilligungsintegration nicht gemaess Gate 1 abgenommen ist. **Firestorm-Havok-NavMesh und OGLVoice sind ausdruecklich keine 0.9.3.10-Stable-Blocker.** CI allein ersetzt Live-Abnahme nicht.

## F. Kompatibilität und Releasepolitik

- Die stabile Referenz `v0.9.3.8` bleibt verfügbar; kein Verschieben eines Tags.
- Die aktive Produktversion bleibt `OpenGenesisLINK 0.9.3.10 Dev` fuer Runtime, Pathfinding und Experiences; keine oeffentliche Stable-Verwechslung.
- Keine separate Veröffentlichung `0.9.3.9 Stable`.
- OGLVoice bleibt standardmaessig inaktiv und ist ausdruecklich auf einen spaeteren Entwicklungsabschnitt vertagt. Vorhandenen Code, Tests und Sicherheitsgrenzen beibehalten; keine produktive Gateway-Aktivierung zur Erfuellung dieses Release-Trains.
- Keine unechte Pathfinding-LSL-Kompatibilität bewerben oder unterstützte LSL-Funktionen als blind funktionierende Stubs implementieren. Firestorm bleibt ohne realen Havok-kompatiblen NavMesh-Provider im Pathfinding-Menue grau; OGLRetrieveNavGraph/1 ist ein getrennter offener Viewer-Vertrag.
- Alle zusätzlichen Features nach 0.9.3.10 gehören zu späteren Release-Trains.
