# OpenGenesisLINK 0.9.3.10 Dev – erste echte Physics-Raycast-Navigation

## Abgrenzung
Bislang basiert der 3D-Graph auf einer Schnittstelle für **extern** nachgewiesene
Oberflächen. `OglVerifiedRaycastSurfacesModule` ist nun ein erster eigener
optionaler Lieferant, der tatsächliche **Physik-Kollisionskontakte** auf
Terrain und statischen Prims/Meshes misst statt nur sichtbare Bounding-Boxes
in begehbare Ebenen umzudeuten. Er implementiert
`IOglVerifiedLayeredSurfaceSource`; der bereits vorhandene
`OglTerrainNavigationModule` ruft ihn beim Neuaufbau auf.

Bei einer Region von 256 x 256 Metern und Rastergröße 4 Meter gibt es
4096 mögliche Spalten. Die Berechnung begrenzt Spalten auf 4096, Ray-Treffer
auf 16 und auf maximal drei Höhenebenen je Spalte. Pro Fläche werden
physikalische Nachweise im Zellmittelpunkt und an vier Stützpunkten
gefordert. Verbindungen auf gleicher Ebene erfordern zusätzlich einen
Kollisionsnachweis an der gemeinsamen Zellgrenze **desselben statischen
Physikobjekts**. Andere Objekte und andere Höhenebenen werden nicht
ohne explizites Portal verbunden.

Die Physics-API wird mit gefilterten `RayCastFiltered`-Abfragen auf
Land plus nichtphysikalische (statische) Prims begrenzt.
Bewegte, Phantom- und Volume-Detect-Objekte gelten nicht als Laufuntergrund.
Das Backend muss gefilterte Raycasts wirklich unterstützen. Bei
fehlender Unterstützung, Timeout, beschädigten Ray-Daten, zu vielen
Flächen oder zu großen Regionen bleibt der Navigator **nicht bereit**.
Terrain-/Objektänderungen invalidieren den bestehenden Snapshot; erst
ein erfolgreiches erneutes Sampling schaltet die internen Wege wieder frei.

## Nur für isolierte Entwicklung

```ini
[OGLPathfinding]
    Enabled = true
    CellMeters = 4
    UseVerifiedLayeredSurfaces = true
    PhysicsRaycastLayeredSurfaces = true
    PhysicsRaycastMaxBuildSeconds = 30
    LayeredMaxStepMeters = 0.6
    FirestormNavMeshCaps = false
```

Vorher eine unveränderte Produktionsregion sichern und den Test auf
`OGL Developer Gen1` durchführen. Mit `Enabled = false` ist der
Provider vollständig deaktiviert. Der Simulator braucht eine
funktionierende gefilterte Raycast-Physik, z.B. ubOde; manche
BulletSim-Konfigurationen melden Support, liefern aber keine
gefilterten Kontakte. Dort schlägt die neue Abfrage kontrolliert fehl.

Die ubOde-Landstrahlstrecke ist auf 60m begrenzt. Der Provider tastet
deshalb nur 52m aus einem Startpunkt 40m über der jeweiligen
Terrainhöhe ab. Höher liegende Gebäude und Sonderfälle sind in
diesem Schritt **nicht** abgedeckt und müssen später durch
segmentiertes Ray-Sampling erfasst werden.

## Offene Sicherheits- und Release-Gates

Das aktuelle Sampling bestätigt **lokalen Laufuntergrund**, nicht
die kontinuierlich freie 3D-Kapselbahn des gesamten Charakters.
Vor einer allgemeinen Character-/NPC-Laufsteuerung muss zusätzlich
eine echte Kapsel-/Box-Sweep-Kollisionsprüfung entlang jedes
Segments erfolgen. Ohne diese Freiraumprüfung sind schmale
Durchgänge, überstehende Decken und Wände nicht sicher navigierbar.

Es werden noch **keine** Treppen, Rampen, Leiter- oder Aufzugsportale
automatisch angelegt. Auch von zwei aneinanderliegenden,
unterschiedlichen statischen Mesh-Objekten wird in dieser Version
kein Übergang angenommen (bewusste Fehlerminimierung).

Das Modul erzeugt einen nativen serverseitigen Raster-/A*-Graphen,
keinen Firestorm/Havok- oder Recast-PolyNavMesh-Payload.
`RetrieveNavMeshSrc` darf daher trotz
`FirestormNavMeshCaps`-Bridge **nicht** aktiviert werden;
das Firestorm-Menü bleibt ohne echten separaten Publisher grau.

## Geprüfte Bausteine

`OglVerifiedCollisionSurfaceRegression` testet:
Terraingeschoss und Brücke, Trennung der Höhenebenen,
fehlenden Kollisionsnachweis an einer Zellgrenze,
unverbundene statische Physikobjekte, beschädigte Ray-Daten,
zu viele Stockwerke, VAR-Sampling-Budget und nicht
angrenzende fingierte Nachbarverbindungen.

Der nächste Entwicklungsschritt ist ein konfigurierbarer
**Avatar-/NPC-Capsule-Sweep** mit sicherer Abfrage pro Wegsegment,
anschließend geprüftes Hinzufügen echter Treppen-/Rampenkontakte
und schließlich ein offenes Detour-NavMesh für den
OpenGenesisLINK-Viewer. OGL Voice bleibt zurückgestellt.
