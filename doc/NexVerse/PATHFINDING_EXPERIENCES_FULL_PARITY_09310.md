# OpenGenesisLINK 0.9.3.10 Dev – Pathfinding und Experiences: vollstaendige Integrationsabnahme

**Historische Entscheidung 10.10.2026:** Diese Seite dokumentiert das langfristige Vollparitaetsziel. **Aktualisierte Stable-Entscheidung 11.10.2026:** Die Firestorm-Havok-kompatible NavMesh-Ausgabe, komplette dynamische SL-Character-LSL-API und die eigene OGL-Viewer-Darstellung sind **auf spaetere Entwicklung verschoben und kein 0.9.3.10-Stable-Gate**. Fuer 0.9.3.10 verbindlich: nachweisbar korrekte **native** OGL-3D-Navigation/LSL-Teilmenge/NPC-Physik und vollstaendige NexExperiences-Einwilligung, LSL/KV und Firestorm-Live-Abnahme; Runtime und Sicherheit. OGLVoice bleibt deaktiviert und aus dem Scope.

**Nicht freigegeben:** Vorhandener Code oder gruenes CI beweist weder reale native Wegsuche/NPC-Bewegung noch Experience-Allow/Block/Forget mit zwei Firestorm-Avataren. Graue Firestorm-Pathfinding-Menues gelten als bewusst beibehaltene Kompatibilitaetsgrenze, **nicht** als Fehler des 0.9.3.10-Stable-Scopes. Keine Stable-Tags vor den Live-Gates.

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

## II. Langfristiger Backlog: vollstaendiges Pathfinding (nicht komplett 0.9.3.10-Gate)

Diese Aufgaben sind der **langfristige** Zielkatalog fuer volle SL-/Firestorm-Paritaet. Der 0.9.3.10-Freigabeumfang umfasst die dokumentierte sichere native Graph-/Terrain-/NPC-/statische-LSL-Teilmenge, nicht die Havok-Ausgabe. Insbesondere Punkt 3 sowie nicht implementierte dynamische Character-LSL-Operationen werden erst in spaeteren Versionen verpflichtend.

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

## Login replay safety for Experience CAPS

The new `NexExperienceCapLeaseRegistry` creates a fresh region+resident
lease whenever Firestorm negotiates Experiences capabilities. Every
Experience read/permission/role HTTP request checks this lease **and**
the current root avatar in the issuing region. Negotiating a second
session in the same region immediately revokes the old lease, even
before the HTTP framework has retired its old random bearer URLs.
Removal of the presence, root-to-child transfer, region shutdown and
simulator shutdown revoke associated leases. Consequently an old
`ExperiencePreferences` URL cannot regain authority after a logout
and later return to the same simulator.

The registry retains at most one active lease per region/resident.
This protects against accidental unbounded lifetime of historical
session capabilities and never sends any machine API key to the viewer.
Unit regression covers same-region relogin, independent avatars and
other-region isolation. Full real-Firestorm login/relogin tests are
still required before declaring release readiness.

## Experience CAP session and script lifecycle hardening (0.9.3.10 Dev)

- A random Experience CAP belongs to its *issuing scene*, not only an
  avatar UUID. All GetExperienceInfo, role-list, permission-list and
  Allow/Block/Forget requests verify a live, non-NPC, **root** agent in
  the same scene; a previously issued cap must return HTTP 410 after
  region departure even when the avatar is online elsewhere.
- Every scene subscribes to script reset/removal, avatar presence removal
  and root-to-child transfers. Pending consent requests are atomically
  canceled and native `OnScriptAnswer` handlers detached **without**
  posting an event into a reset/removed script. A timer still settles
  unanswered live requests with the 60-second timeout.
- Native Firestorm `ScriptQuestion` requests only the documented
  `JoinAnExperience` bit `0x2000`. Asking for controls, attachments,
  camera access or teleport permission as a side effect would mislead
  residents. A matching `ExperienceID` is still required.
- Firestorm Allow/Block/Forget is saved only by the
  avatar-bound `ExperiencePreferences` CAPS using the separate
  server-side `experiences:viewer:permissions` key. A standalone
  `ScriptAnswerYes` packet cannot grant persistent Experience rights.
- Parallel Experience scripts keep isolated pending requests; Deny only
  cancels its specific script. Allow/Block after central persistence
  resolves matching requests, with a second enabled/binding/land check.

**Status:** server-side consent lifecycle implementation with CI tests.
Acceptance with a real resident in Firestorm, scripts reset during
prompts, teleport/crossing and viewer logout is still mandatory before
production. This does not constitute proof of full SL Experience parity.

## Native Firestorm Experience consent – Testimplementierung

Der Client-Adapter unterstuetzt jetzt optional das tatsaechliche UDP-
`ScriptQuestion` mit `Experience.ExperienceID` und den sechs standardisierten
Experience-Permissions-Bits. Anders als die fruehere reine Agentenmeldung
kann Firestorm daraus den nativen `ScriptQuestionExperience`-Dialog
erzeugen. Ein einfaches `ScriptAnswerYes` reicht *nicht* fuer die
Dauerberechtigung: Nur ein durch die CAPS gebundener Bewohner gesendetes
`ExperiencePreferences`-Allow bzw. -Block mit erfolgreicher zentraler
Speicherung loest die positive LSL-Anfrage aus. Eine explizite
native **Deny**-Antwort mit `ScriptAnswerYes.Questions=0` beendet die
offene Anfrage unmittelbar mit `XP_ERROR_NOT_PERMITTED`. Alle Handler
werden bei Allow, Deny, Timeout oder Regionsabmeldung abgemeldet.

Fuer eine isolierte Entwicklerregion:
```ini
[NexExperiencesViewer]
    Enabled = true
    ApiKey = "${Environment|NEXVERSE_EXPERIENCES_API_KEY}"
    ViewerPermissionsApiKey = "${Environment|NEXVERSE_EXPERIENCES_VIEWER_PERMISSIONS_API_KEY}"
    FirestormReadCaps = true
    FirestormPermissionCaps = true
    ScriptPendingConsent = true
    NativeExperiencePrompt = true
```

Sicherung: NativeExperiencePrompt ist standardmaessig **false** und
setzt alle vorgelagerten Features und zwei verschiedene echte
API-Schluessel voraus. Der Viewer erhaelt die Schluessel nie. Ohne
geeigneten UDP-Client wird nur die bisherige Informationsmeldung
gesendet, niemals automatisch zugestimmt. Vor Freigabe muessen
Allow/Block, Timeout, Viewer-Neuanmeldung, Script-Reset, Parzellenrechte
und unberechtigte fremde Avatare live getestet werden.

## Warum Firestorm Pathfinding grau bleibt

Der *offizielle Firestorm-Quellcode* in
`indra/newview/llpathfindingmanager.cpp` prueft
`isPathfindingEnabledForRegion()` mit der Existenz einer
`RetrieveNavMeshSrc`-CAP-URL. OGL registriert diese absichtlich nicht:
der existierende Terrain-/Multi-Layer-A*-Graph ist **kein**
Firestorm-lesbares Havok-NavMesh mit Generation-/Status-/Objekt-/
Character-CAPS. Ohne korrektes binäres NavMesh-Protokoll darf keine
leere oder irrefuehrende Capability beworben werden. Das graue Menue
ist daher derzeit ein ehrlicher Indikator, nicht durch
`OGLPathfinding.Enabled=true` allein zu beheben.

Die naechsten Implementierungs-Blocker sind realer
Collision-Triangle-NavMesh-Provider, Versions-/Rebake-Logik,
`RetrieveNavMeshSrc`, `NavMeshGenerationStatus`,
`RegionObjects`, `TerrainNavMeshProperties`,
`CharacterProperties`, `AgentState` und deren
authentifizierte Live-Integration. Solange dies nicht abgeschlossen
ist, bleiben die Firestorm-Menues deaktiviert. Eine Viewer-Aenderung,
die nur das Menue entsperrt, waere keine funktionierende Navigation.

## IV. Release-Entscheidung und Vorgehen

- **Gate P (0.9.3.10):** Native verifizierte 3D-Graph-Routen, Dirty-Snapshot-/Epoch-Sicherheit, 3D-NPC-Wegpunkte, dokumentierte statische LSL-Abfragen und Physik-/Ressourcenbudgets in echter Region abgenommen; nicht unterstuetzte Character-APIs fail-closed. **Kein** Firestorm-Havok-NavMesh- oder eigener Viewer-Gate.
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
