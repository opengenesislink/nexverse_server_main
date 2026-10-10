# OpenGenesisLINK 0.9.3.10 Dev – `llGetStaticPath` auf nativem Terrain

**Scope:** Das experimentelle, standardmaessig deaktivierte `[OGLPathfinding]`-Modul stellt ueber `IOglNativeTerrainQuery` einen begrenzten statischen A*-Terrainpfad bereit. Die LSL-Funktion `llGetStaticPath(vector start, vector end, float radius, list params)` liefert Wegpunkt-Vektoren gefolgt von einem SL-kompatiblen ganzzahligen Statuscode. `llGetClosestNavPoint` steht ebenfalls fuer die Terrainoberflaeche bereit.

Das ist **keine** multi-layer- oder mesh-basierte NavMesh-Engine. Dynamische Prim-Kollisionen, bewegliche Hindernisse, Charaktertyp-Routen, Pathfinding-Character-Bewegung und die Firestorm-CAPS `RetrieveNavMeshSrc` / `NavMeshGenerationStatus` bleiben offene Release-Gates. Bei nicht vorhandenem oder waehrend Terraforming invalide gewordenem Snapshot meldet `llGetStaticPath` `PU_FAILURE_NO_NAVMESH` statt erfundene Wegpunkte auszugeben.

## Sicherheits- und Lastgrenzen

- `radius` muss zwischen 0,125 und 5 m liegen.
- `params` unterstuetzt derzeit nur `[CHARACTER_TYPE, CHARACTER_TYPE_NONE]` (oder leere Liste). Andere Character-Typen werden explizit abgelehnt.
- Ein Weg darf hoechstens 256 Zell-Wegpunkte umfassen; der bestehende konfigurierte A*-CPU-Suchrahmen gilt.
- Ungueltige Ziel-/Startorte geben die SL-Fehlercodes 3 bzw. 2, nicht gefundene Wege 4 und fehlerhafte Optionen `PU_FAILURE_OTHER` zurueck.
- Externe Agenten-, Avatar- oder Physics-Positionen werden nicht ungeprueft mutiert; die Funktion berechnet nur Wegpunkte.

## Developer-Live-Test

NUR auf dem isolierten Developer-Simulator mit passender `OpenSim.ini` testweise aktivieren (Code muss zuvor gebaut und der Prozess kontrolliert neugestartet sein):

```ini
[OGLPathfinding]
    Enabled = true
    CellMeters = 4
    MaxSlopePerMeter = 0.65
    MaxExpandedNodes = 20000
    RebuildSeconds = 30
```

LSL-Testskript in einen Testprim legen (auf ebenem, nicht ueberflutetem Terrain):

```lsl
default
{
    touch_start(integer count)
    {
        vector from = llGetPos();
        vector to = from + <16.0, 0.0, 0.0>;
        list result = llGetStaticPath(from, to, 0.5, []);
        integer status = llList2Integer(result, -1);
        llOwnerSay("Static terrain route status: " + (string)status);
        llOwnerSay("Waypoints: " + llList2CSV(result));
    }
}
```

**Erwartet:** Auf geeignetem, zusammenhaengendem Terrain folgt auf die Wegpunkte Status `0`. Liegt ein Punkt ausserhalb oder zu weit von der Terrainoberflaeche, erscheinen `PU_FAILURE_INVALID_START` (2) oder `PU_FAILURE_INVALID_GOAL` (3). Nach `[OGLPathfinding] Enabled=false` ist die erwartete Antwort `[9]`.

**Abnahme:** Build/CI beweisen nur Quellcode-Wiring und deterministische Kernlogik. Der Benutzer muss LSL-Kompilierung, Scriptlaufzeit, Terrain-Edits waehrend einer Anfrage, Hindernisse, CPU-Ticks und Firestorm-Log getrennt in der Developer-Region testen. `0.9.3.10 Stable` ist dadurch **nicht** freigegeben.
