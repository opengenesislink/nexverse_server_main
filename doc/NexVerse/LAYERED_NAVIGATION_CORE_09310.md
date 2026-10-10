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

## Wichtig: Integration noch offen

Der mehrschichtige Kern **interpretiert keine Mesh-Dreiecke selbst** und ist **noch nicht** als aktiver Regions-Navigator an `Scene`, Physics oder die OpenSim-LSL-API angeschlossen. Der aktuell nutzbare `IOglNativeTerrainQuery` und `llGetStaticPath` verbleiben beim bisherigen, terrainbasierten Snapshot. Mit dieser Trennung kann der neue Kern getestet werden, ohne begehbare Stockwerke, Treppen oder Durchgaenge aus beliebigen Mesh-AABBs zu erfinden.

### Notwendige naechste Stufe
1. Echte begehbare Flaechen aus Physics-/Mesh-Kollisionstriangulation oder nachweislich begehbaren Kollisionsflaechen gewinnen; Ebenen/Steigung/Agentenhoehe/Randkanten validieren.
2. Verifizierte Treppen, Tueren, Off-Mesh-Portale und vertikale Freiraeume bilden; Terraforming und Prim-/Mesh-Updates versioniert invalidieren.
3. `OglLayeredNavGraph` an den asynchronen Regions-Snapshot, Character-Steuerung, komplette Pathfinding-LSL-Funktionen und die Firestorm-NavMesh-CAPS anbinden.
4. Test auf `OGL Developer Gen1` mit Erdgeschoss/Bruecke/Treppe und echten Firestorm-/NPC-Kollisionen, CPU/GC/Tick-Profiling und Rechtepruefungen.

**Keine Freigabe als vollstaendiges 3D-NavMesh und keine Stable-Freigabe aus dem CI-Build.**
