# OpenGenesisLINK 0.9.3.10 Dev – Mehrschichtiger Navigationskern

## Implementierter Baustein

`NexVerse/Core/Pathfinding/OglLayeredNavGraph.cs` bietet einen **deterministischen, begrenzten Graphen fuer getrennte begehbare Hoehenebenen** (Boden, Steg, Bruecke, 2. Stockwerk). Jede Navigationszelle hat eine X-/Y-Rasterkoordinate, eine **Ebenen-ID**, eine 3D-Hoehe, einen Walkability-Status und einen maximal zulaessigen Agentenradius.

- Innerhalb derselben Ebene sind horizontale und diagonale Nachbarn nur unter einer konfigurierten maximalen Stufenhoehe verbunden; diagonales Schneiden unpassierbarer Ecken ist ausgeschlossen.
- **Unterschiedliche Ebenen werden niemals aufgrund identischer XY-Koordinaten verbunden.** Nur explizit vorgegebene und zuvor von einem zukuenftigen Scene-/Physics-Adapter **validierte** Portale duerfen Rampen, Treppen, Lifte oder Off-Mesh-Wege verbinden. Portale koennen einseitig oder beidseitig sein.
- Die A*-Suche verwendet 3D-Euklidische Distanz, deterministisches Tie-Breaking, pro Agentenradius gepruefte Zellen und konfigurierbare Budgets; es werden keine veralteten/mutierbaren Scene-Geometriedaten waehrend des Suchlaufs gelesen.
- Harte Obergrenzen: 65.536 Navigationsknoten, 32.768 Portale, 512 Pfadknoten, maximal 100.000 explorierte Knoten je Aufruf, 0,125–5 m Agentenradius.

## Gepruefte Beispiele

Die C#-Regression in `tools/ci/OglTerrainNavigationRegression/Program.cs` prueft:
- Erdgeschoss und hochgelegene Bruecke ueber der **gleichen XY-Flaeche**: keine erfundene vertikale Abkuerzung
- Verbindung erst ueber explizite Treppen-Portale, dabei auch Einbahn-Lifte
- Steigung, Cliffs, zu breite Agenten und blockierte diagonale Ecken
- Begrenzung der CPU-Arbeit und Ablehnung ungueltiger bzw. doppelter Knoten oder Portale

## Native Regionsabfragen: neue Anbindung (nach PR #141)

Die Abfragen `TryFindClosestSurface` und `TryFindWorldPath` arbeiten jetzt
vollstaendig in 3D und liefern fuer gepruefte Oberflaechen einen
world-space-Pfad mit begrenzten Kosten und SL-kompatiblen Fehlercodes.
Bei gleicher X/Y-Position waehlt der Query aufgrund des Z-Abstands
die richtige Ebene. Getrennte Etagen werden **ausschliesslich** ueber
bereitgestellte verifizierte Portale miteinander verbunden.

`OglTerrainNavigationModule` kann bei aktivierter Option
`UseVerifiedLayeredSurfaces = true` einen vertrauenswuerdigen
`IOglVerifiedLayeredSurfaceSource`-Regionsprovider abfragen.
Dieser erstellt einen **unveraenderlichen** `OglLayeredNavGraph` aus
real nachgewiesenen Collision-Surfaces und geprueften Off-Mesh-Portalen.
Der Regions-Snapshot wird zusammen mit dem Terrain-Snapshot
epoch-gebunden veroeffentlicht. Terrain-/Objektaenderungen sperren
alte Anfragen bis zum Neuaufbau. Bei fehlendem, fehlerhaftem oder
regionsfremdem Provider-Graphen schlaegt die Navigation geschlossen
fehl, statt im Viewer vermeintliche Etagenwege zu melden.

```ini
[OGLPathfinding]
    Enabled = false
    UseVerifiedLayeredSurfaces = false
    LayeredMaxStepMeters = 0.6
```

**Die Option nicht auf Produktion einschalten.** In diesem Schritt
wurde die Anbindung samt 3D-Abfrage, aber noch **kein konkreter**
Collision-Mesh-/Physics-Provider implementiert. Der normale
Terrain-Snapshot bleibt bei Default-Konfiguration unveraendert.
`RetrieveNavMeshSrc` wird weiterhin nicht beworben.

## Weiterhin offene Release-Blocker

1. Scene-/Physics-Adapter, der echte Mesh-Triangulation bzw.
   nachweislich begehbare Kollisionsflaechen abtastet, vertikalen
   Agenten-Freiraum sicherstellt und verifizierte Portale erstellt.
2. Reale Character-/NPC-LSL-Lifecycle-Integration mit
   `llCreateCharacter`, `llNavigateTo`, `path_update` usw.
3. Korrekte Firestorm-Havok-NavMesh-CAPS und echte Viewer-Abnahme.
4. Live-Tests auf `OGL Developer Gen1`, mehrere Etagen und
   Regionen, Last-/Race-Profiling, Permissions und Rollback.

**0.9.3.10 bleibt Dev. Keine volle 3D-NavMesh-/LSL-Paritaet und keine Stable-Freigabe.**
