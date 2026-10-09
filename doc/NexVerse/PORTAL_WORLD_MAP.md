# OpenGenesisLINK Bürgerportal Weltkarte

### Buergerportal-Weltkarte (ab dem Portal-Map-Branch)

Das Stadtportal kann eine **eigene Navigation „Weltkarte“** anlegen und die
serverseitig bereitgestellte, interaktive Karte einbinden. Das Stadtportal selbst
ist ein separates Deployment und wird durch dieses Server-Repository **nicht
automatisch geändert**.

- `GET /api/v1/world-map/view` — HTML5-Kartenansicht mit Suche, Pan, Zoom,
  Regioneninformationen und konfigurierten Teleportlinks (ohne Bearbeitungsrechte)
- `GET /api/v1/world-map?min_x=1000&max_x=1011&min_y=1000&max_y=1011`
  — öffentliches Regionsfenster; Koordinaten sind **256-m-Rasterzellen**,
  maximal 16 × 16 Rasterzellen je Anfrage
- `GET /api/v1/world-map?q=Freiburg` — öffentliche Regionsnamensuche,
  mindestens zwei und höchstens 80 Zeichen, maximal 32 Treffer
- `GET /api/v1/world-map` — initialer Kartenausschnitt um eine
  konfigurierte Standardregion; falls keine existiert, über Regionssuche starten

Das JSON enthält ausschließlich Name, UUID, Rasterposition, Regionsgröße,
Reifegrad sowie ggf. Teleport-URI. **Keine** Simulator-/Node-IP, Server-URI,
Estate-Metadaten, Avatar-Positionsdaten oder Reservierungen. Offline-Regionen
werden ausgelassen, sofern der GridService die Online-Flags bereitstellt.

**Kartenbilder:** Die Anzeige versucht die vorhandenen OpenSimulator-
MapImageService-Kacheln `/map/map-1-{x}-{y}-objects.jpg` zu laden. Der
MapImageService muss dazu auf demselben öffentlich erreichbaren Host aktiv
oder über einen Reverse Proxy unter `/map/` erreichbar sein. Fehlen Kacheln,
bleiben das Regionsraster, die Beschriftungen und die Suche funktionsfähig.
Es werden keine Kartenbilder in der World API dupliziert.

**Teleport:** Die Portal-Karte verwendet denselben
`[NexDiscovery] TeleportBaseUri` wie die NexSearch. Ist er nicht
konfiguriert, wird kein ungültiger Teleport-Link erzeugt.

**Einbindung im Stadtportal (nach Freigabe des Server-Builds):**

```html
<!-- eigener Navigationslink „Weltkarte“ -->
<a href="/weltkarte">Weltkarte</a>
<!-- Inhalt der stadtportal-eigenen Route /weltkarte -->
<iframe
  src="https://hg.stadt-nexverse.de/api/v1/world-map/view"
  title="NexVerse Weltkarte"
  style="width:100%;height:75vh;border:0"
  loading="lazy"></iframe>
```

Wenn der Webserver Framing durch CSP/`X-Frame-Options` blockiert, ist
stattdessen ein normaler Link oder eine gleich-originige Reverse-Proxy-Route
einzurichten. Auch der `/map/`-Pfad muss zum World-API-Host passen. Die
Portalnavigation selbst liegt außerhalb dieses Repositories. Browser
sollen weder `regions:read`-Adminschlüssel noch Tokens in Kachel-URLs
platzieren.
