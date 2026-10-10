# OpenGenesisLINK 0.9.3.10 Dev – Pathfinding und Experiences: vollstaendige Integrationsabnahme

**Entscheidung 10.10.2026:** Zwei verbindliche Funktionsbereiche fuer den aktuellen Release-Train: (1) Pathfinding einschliesslich realem NavMesh, LSL und Firestorm; (2) NexExperiences einschliesslich Viewer-CAPS, Benutzerzustimmung, persistenten Rechten und LSL. Runtime-/Sicherheitsabnahme laeuft weiter. **OGLVoice ist aus diesem Release-Scope verschoben und bleibt standardmaessig deaktiviert.**

**Nicht freigegeben:** Weder vorhandener Quellcode noch ein gruenes CI sind ein Nachweis, dass Firestorm in einer echten Region das NavMesh rendern kann oder Bewohner Experiences erlauben/widerrufen koennen. Keine neuen Stable-Tags vor den unten stehenden Live-Gates.

## I. Vorhandener, verifizierbarer Quellcode

### Pathfinding

- `NexVerse/Core/Pathfinding/OglGridPathfinder.cs`: bounded, deterministische A*-Raster-Routenplanung mit Diagonal-/Eckensperre und CPU-Budget.
- `NexVerse/Core/Pathfinding/OglTerrainNavigationSnapshot.cs`: Terrain/Steigung/Wasser, optionaler Hindernis-Callback; **keine** rekonstruierten statischen Mesh-Kollisionen, Tueren oder mehrschichtigen Etagen.
- `NexVerse/RegionModules/Pathfinding/OglTerrainNavigationModule.cs`: Scene.Heightmap, opt-in per `[OGLPathfinding]`, Terrain-Taint, Snapshots; seit dem aktuellen Hardening zeitlich konsistente Snapshot-Epochen bei konkurrierendem Terraforming/Rebuild/A*-Abfrage.
- `NexVerse/RegionModules/Pathfinding/OglNpcPathFollowerModule.cs`: opt-in NPC-Follower ueber vorhandene OpenSim-Physik/`INPCModule.MoveToTarget`, Besitzerpruefung, Routenquoten und Timeout. **Keine** volle SL-Pathfinding-Character-/LSL-Paritaet.

### Experiences

- `NexVerse/Core/Experiences/NexExperienceStore.cs`: zentraler JSON-basierter Verwaltungs- und K/V-Store mit Owner/Admin/Contributor, Resident- und Ortsrechten, Scriptbindung, Audit und Mengenbegrenzungen. Derzeit **Einzeldatei-Store**; Multi-Writer-/Ausfall-Failover und SQL-Transaktionen sind separat zu bewerten.
- `NexVerse/Server/Api/NexExperiencesApi.cs`: berechtigungsgeschuetzte World-API-Verwaltung und scriptgebundene K/V-Operationen; Hardening untersagt Identitaets-Impersonation mit nur `experiences:manage`-Servicekeys ohne `admin:*`.
- `NexVerse/RegionModules/Experiences/NexExperienceModule.cs`: opt-in API-Adapter ohne direkten Regionszugriff auf zentralen Datenbestand.
- `OpenSim/Region/ScriptEngine/Shared/Api/Implementation/LSL_Api.cs`: bereits mehrere Experience-LSL-Funktionen, K/V-Dataserver und `experience_permissions(_denied)`-Events. **Bisher keine vollstaendige, vom Viewer vermittelte Zustimmungs-/Widerrufsinteraktion.**
- Deaktivierte Experiences duerfen nach dem Hardening keine K/V-Lese-/Schreiboperationen trotz vorhandener Scriptbindungen mehr ausfuehren.

## II. Noch zu implementieren: echtes vollstaendiges Pathfinding

1. **Region-NavMesh-Backend:** Begehbare 3D-Kacheln mit Zell-/Agentenradius, Terrain, Hang-/Wasserfilter, statischen Prim-/Mesh-Kollisionen, Etagen/Treppen, dynamischen Hindernissen und expliziten Off-Mesh-Links. Keine Wege durch Waende oder nicht begehbare Bereiche. Bounding/Budget fuer VAR-Regionen.
2. **Konsistente Updates:** Generation/Epoch, versionierte Snapshots, asynchrone Dirty-Tile-Rebuilds nach Terraforming, Rezzing/Loeschen/Verschieben und Parzellen-/Physikaenderungen, stabile Nebenlaeufigkeit sowie Save/Restore-Strategie fuer NavMesh-Caches.
3. **Firestorm-Protokoll:** Ueber tatsaechliche `Scene`-Capabilities ein *korrektes* `RetrieveNavMeshSrc` mit `NavMeshGenerationStatus` anbieten; genaue LLSD-/binäre Payloads mit Firestorm-Quellcode sowie An-/Abmeldung testen. Weitere betroffene Pathfinding-CAPS (`RegionObjects`, `ObjectNavMeshProperties`, `TerrainNavMeshProperties`, `CharacterProperties`, `AgentState`) erst bei tatsaechlich implementierter Semantik aktivieren. **Kein Dummy-Havok-/leer-Mesh-Payload.**
4. **Character-/NPC-Lifecycle:** Owner-/Estate-/Parcel-Rechte, Radius/Collision- und Speed-Policies, Move/Stop, Steering, Stuck-Detection, Race und Rebuild-Handling, Revoke bei Loeschen/Teleport; kontrolliertes Cross-Region-Verhalten.
5. **LSL + Events:** `llCreateCharacter`, `llDeleteCharacter`, `llNavigateTo`, `llPursue`, `llFleeFrom`, `llWanderWithin`, `llPatrolPoints`, `llGetClosestNavPoint` und das echte `path_update`-Event nach Second-Life-kompatiblen Signaturen/Fehlercodes. Keine inert-kompilierenden Stubs als erledigt kennzeichnen.
6. **Regression & Live:** deterministische Unit-/Property-/Stress-Tests fuer Wegsuche, Multi-Layer, Hindernisse, geblockte Diagonalen, 4er-VAR, Terraforming/Rebuild-Races, Rechte, Fehlercodes; anschliessend reale Firestorm-/LSL-/NPC-Testregion. Metriken fuer CPU/GC/Region-Ticks dokumentieren.

## III. Noch zu implementieren: volle NexExperiences-/Firestorm-Kompatibilitaet

1. **Viewer-CAPS:** Firestorm verlangt u.a. `GetExperienceInfo`, `FindExperienceByName`, `GetExperiences`, `AgentExperiences`, `ExperiencePreferences`, `GroupExperiences`, `GetAdminExperiences` und `GetCreatorExperiences`. Authentifizierte per-Agent-LLSD-Handler und die je Route tatsaechlich erwarteten Query-/Body-/Antwortformate pruefen; nicht implementierte CAP-Namen nicht vorgetaeuscht bewerben.
2. **Resident-Zustimmung:** Echter Agent-gebundener Berechtigungsprompt; pro Experience `Allow`/`Block`/`Forget`/Revoke, mit entsprechender Avatar- und Experience-ID, keine Fremd-Impersonation, keine automatische Zustimmung bei `llRequestExperiencePermissions`. Firestorm-Experience-Floater/Profil zeigt die Listen konsistent und persistent. Re-Login, anderer Simulator und Hypergrid-Identitaeten beachten.
3. **Rollen/Script-Identitaet:** Owner/Group/Admin/Contributor, an echte Script-Inventar-/Objekteigentuemer gebundene Experience-Zuordnung, keine uebertragene Scriptbindung auf fremde Besitzer ohne erneute Pruefung. Estate-/Parcel-Allow/Block und Region-Moderationsrechte mit *echten* Regionsidentifikatoren durchsetzen.
4. **LSL-Verhalten:** Vorhandene Experience-LSL-Funktionen systematisch gegen aktuelle SL-Signaturen, Result-Listen, XP_ERROR-Codes, Timeout-/Throttle-Verhalten und Script-Event-Zustellung pruefen; K/V-Calls mit bewaehrtem Request-ID-/Dataserver-Protokoll asynchron absichern. Deaktiviertes/gesperrtes Erlebnis verweigert mutierende und lesende Skriptoperationen.
5. **Zentrale Persistenz:** Revisions-/Migrations-/Recoverystrategie fuer den heutigen JSON-Store; bei Multi-Robust-Betrieb echte transaktionale zentrale SQL-Autoritaet und CAS-/Audit-/Backup-/Restore-Pruefung statt Dateikonflikte.
6. **Sicherheit & Live:** Zwei Bewohner und zwei Simulatoren, ein Owner und ein unberechtigter Avatar, Allow/Block/Revoke, Experience-Profilsuche/-pflege, K/V-CAS-Races, Neustart, berechtigtes und missbraeuchliches Script, HG-Gast, Logs/Quoten; danach Firestorm-UI + Live-LSL mit dokumentierten Logs.

## IV. Release-Entscheidung und Vorgehen

- **Gate P:** Pathfinding-LSL/Character/NavMesh/Firestorm/Live-Region **alle** bestanden.
- **Gate E:** Experience-Viewer-CAPS/Einwilligung/LSL/KV/Persistenz/Rechte/Live-Viewer **alle** bestanden.
- **Gate R:** Release-Build, bestehende Runtime-/Login-/Inventory-/NexBus-/HG-Regressionen und produktiver Last-/Rollback-Nachweis.
- **OGLVoice explizit ausgeschlossen.** Vorhandener Voice-Code bleibt unveraendert und standardmaessig inaktiv.

Bis zum nachweislichen Bestehen von P, E und R bleibt die Version `OpenGenesisLINK 0.9.3.10 Dev`. Dies ist eine **Implementierungs- und Abnahme-Checkliste, kein Erledigt-Bericht**.

### Reproduzierbare, sichere Developer-Vorbereitung

```bash
# Quellcode und CI-Guard pruefen (ohne Produktivwerte umzuschreiben).
cd /opt/developer
python3 tools/ci/verify_ogl_terrain_navigation.py
python3 tools/ci/verify_nexgroups_experiences.py

# Konfiguration nur LESEND pruefen. Falsche Dateinamen anpassen.
python3 tools/diagnostics/ogl_voice_pathfinding_preflight.py \
  --sim-config bin/OpenSim.ini \
  --robust-config /opt/robust/bin/Robust.HG.ini
```

`[OGLPathfinding]` und `[NexExperiencesViewer]` auf dem produktiven Grid erst nach gezieltem Developer-Test aktivieren. API-Schluessel, Datenbank-Zugangsdaten und Token nie in Chat/CI-Logs ausgeben.
