# OpenGenesisLINK 0.9.3.10 Dev: Firestorm NavMesh CAPS bridge

## Was jetzt tatsaechlich implementiert ist

`OglFirestormNavMeshCapsModule` bietet einen getrennten, ausschliesslich
lesenden Firestorm-Pathfinding-Transport. Firestorm
`LLPathfindingManager::isPathfindingEnabledForRegion` aktiviert
Pathfinding-Menues **nur**, wenn `RetrieveNavMeshSrc` in den
Regions-Capabilities angeboten wird.

Unter `[OGLPathfinding]` existiert deshalb neu:

```ini
[OGLPathfinding]
    Enabled = false
    FirestormNavMeshCaps = false
```

**FirestormNavMeshCaps NICHT auf einem produktiven Simulator aktivieren.**
Diese Option allein aktiviert das Viewer-Menue weiterhin **nicht**:
zusaetzlich werden ein aktueller OGL-Regions-Navigations-Snapshot
und ein gesonderter vertrauenswuerdiger
`IOglFirestormNavMeshSource` benoetigt. Der eigentliche
Collision-Triangle-Extraktor / Havok-kompatible NavMesh-Erzeuger fehlt
noch; die CAPS werden ohne Publisher absichtlich **nicht** beworben.

## Firestorm-Protokoll

Der Viewercode erwartet fuer `NavMeshGenerationStatus` (GET) ein
LLSD-Map mit `region_id` (UUID), `version` (Integer >= 0)
und `status` (`pending|building|complete|repending`).
`RetrieveNavMeshSrc` (POST) erwartet eine LLSD-Map mit
`navmesh_version` und `navmesh_data` (LLSD Binary).
Die Bytes sind gzip/zlib-komprimiert und werden in
`llpathfindingnavmesh.cpp` vor der eigentlichen Darstellung
dekomprimiert.

Die Bridge meldet `complete` nur, wenn ein echter kompatibler
Publisher bereits ein vollstaendiges Snapshot erzeugt hat.
Zusatz-CAPS mit ausschliesslich lesenden Antworten:

- `RegionObjects` (GET), UUID -> Object-LLSD
- `TerrainNavMeshProperties` (GET), Terrain-LLSD
- `CharacterProperties` (GET), UUID -> Character-LLSD
- `AgentState` (GET), `can_modify_navmesh=false`

`ObjectNavMeshProperties` (PUT) und `NavMeshGenerationStatus` (POST
`command=rebuild`) sind **nicht** implementiert. Auf der Bridge
gibt es keine automatische Objektmodifikation und kein fingiertes
Rebake.

Jede Capability erhaelt eine zufaellige URL pro Avatar-Session.
Die Handler stellen fest, ob Avatar und Region noch aktiv sind.
Bei dirty/fehlendem Graphen, nicht vorhandener Quelle oder
abweichendem Navigationszustand antworten sie mit HTTP 503.

## Anforderungen an den noch fehlenden Publisher

Ein zusaetzliches Addon implementiert
`IOglFirestormNavMeshSource.TryCaptureFirestormSnapshot(Scene,...)`
und registriert sich als Regionsmodul. Es muss nachweislich:

1. Begehbare Geometrie aus **echten Kollisionsmeshes/Terrain**
   unter Pruefung von Slope, Radius, Hoehe, Raycasts und dynamischen
   Objekten gewinnen.
2. Ein **tatsaechlich von Firestorm interpretierbares** NavMesh
   erzeugen und komprimieren; ein beliebig gezipptes Raster,
   Recast-Blob oder selbstdefiniertes JSON reicht nicht.
3. Ein monoton voranschreitendes `Version` je Region samt
   Aenderungs-/Rebake-Lifecycle bereitstellen.
4. Dazu konsistente Character-/Object-/Terrain-Listen nach
   Firestorm-Feldkontrakt liefern.
5. Jede Aenderung an realer Geometrie epoch-sicher invalidieren.

`OglFirestormNavMeshTransport` begrenzt vor dem Publish die
komprimierte Groesse auf 8 MiB und die Dekompression auf 32 MiB,
akzeptiert gzip/zlib, berechnet SHA-256 und kopiert seine Bytes.
Es **verifiziert nicht**, ob der entpackte proprietaere Payload
physikalisch korrekt oder tatsaechlich Havok-kompatibel ist.

## Firestorm-Viewer-Einschraenkung

Im oeffentlichen Firestorm-Repository existiert unter
`indra/llphysicsextensionsos/LLPathingLibStubImpl.cpp`
nur die quelloffene Stub-Implementierung:
`getInstance()=NULL` und
`extractNavMeshSrcFromLLSD()=LLPL_NOT_IMPLEMENTED`.
Das betrifft Builds ohne funktionierende Physics-Extension.
Eine kompatible Server-CAP alleine kann diese
Renderer-Beschraenkung nicht beheben. Eine langfristige Alternative
ist, den eigenen OpenGenesisLINK-Viewer mit einem quelloffenen
Recast/Detour-Meshformat und dediziertem Renderer auszuruesten.

## Akzeptanzkriterien / noch offen

- Unerlaubter Viewer darf nie fremde Character- oder Linkset-Daten
  abfragen.
- Ohne verifizierte Publisher-Daten bleibt Firestorm grau.
- Mit gueltigem Publisher muss Firestorm
  `RetrieveNavMeshSrc` lesen, entpacken und den Mesh-Renderer
  ohne Fehler initialisieren koennen.
- Linksets und Characters benoetigen reale Objektzuordnung.
- Native LSL Character-Lifecycle, Rebake und Object/NavMesh-Mutationen
  bleiben eigene Release-Gates.
- Live-Test auf OGL Developer Gen1 mit Firestorm-Variante samt
  aktivierter Pathing-Bibliothek erforderlich.

**Status:** verifizierte CAP-Transportgrundlage, kein vollstaendiges
Viewer-Pathfinding und keine Stable-Freigabe. OGL Voice bleibt
zurueckgestellt.
