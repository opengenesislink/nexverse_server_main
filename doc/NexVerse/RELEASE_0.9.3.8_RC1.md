# OpenGenesisLINK 0.9.3.8 RC1 — Release Candidate

**Status:** Vorabversion (Release Candidate), **keine Stable-Freigabe**.  
**Datum:** 8. Oktober 2026  
**Produkt:** OpenGenesisLINK (NexVerse bleibt die Grid-/Welt-Instanz)  
**Runtime:** `OpenGenesisLINK 0.9.3.8 RC1`

## Highlights

- **NexSearch:** Suchfunktion für Personen, Gruppen, Regionen, Parzellen, Places, Events, Classifieds, Experiences und Landangebote.
- **Places, Land Portal und Destination Guide:** Persistente Ortsdaten, moderierte Ziele, Collections, NV$-Landangebote, Reifegradfilter und Teleportlinks.
- **Firestorm-Kompatibilität:** Optionale Viewer-CAPS für Suche und Orte; die vorhandene Second-Life-kompatible Suche bleibt erhalten.
- **NV$-Economy und Banking:** Vom Betreiber bestätigte Viewer-Kontostände, Testzahlungen, Live-Aktualisierung und weiterführende Zahlungstests.
- **NexBus / LSL:** Authentifizierte HMAC-Übertragung, NodeAgent, `llRegionSayTo()`-Fallback, Estate-weite `llEstateSay()`-Zustellung, sichereres Fehlerlogging bei abgewiesenen Events.
- **Weitere bestandene Live-Prüfungen (Betreiberangaben):** Firestorm Login/LSL Bridge, Hypergrid/Offline-IM, Gruppen und Experiences, Rechte, Inventar/Objekte/Land, Karten/Boot, Suche sowie Backup/Restore.

## Abnahme und Nachweise

Der Grid-Betreiber bestätigte **alle zehn Abnahmekategorien** am 8. Oktober 2026. Insbesondere wurden gemeldet:

- `llRegionSayTo()` PING/PONG: **bestanden**.
- `llEstateSay()` zwischen Regionen: **bestanden**.
- NexBus-Transport **zwischen zwei getrennten Simulatorprozessen**: **bestanden (vom Betreiber ausdrücklich bestätigt)**.
- Live-Anzeige und Aktualisierung des NV$-Kontostands nach Zahlung: **bestanden**.
- Firestorm, Hypergrid, Offline-IM und weitere Grundfunktionen: **bestanden**.

Die Live-Befunde sind **vom Betreiber berichtet, nicht durch dieses Release-Verfahren selbst auf dem Produktionsserver reproduziert**. Die GitHub-CI führt zusätzlich eigenständige Build-, Smoke- und Regressionstests aus.

### Bekannte Einschränkungen / Restbeobachtung

- Ein explizit dokumentierter **Cross-Owner-Test** (`AllowCrossOwnerObjectMessages = true`) liegt nicht separat vor.
- Eine frühere einzelne NexBus-Antwort `HTTP 400` wurde gemeldet. PR #107 verbessert die Diagnostik, aber der ältere Fehler wurde nicht rückwirkend aufgeklärt; ein wiederholter Fehler wäre erneut zu prüfen.
- Die genaue Implementierung sämtlicher geplanten MapTile-/Bootzeitoptimierungen ist **nicht als abgeschlossen nachgewiesen**; die beobachtete Stabilität und Bootzeit wurden für RC1 vom Betreiber akzeptiert.
- Die Live-Installation auf dem Grid `v72454` ist **nicht automatisch durch die GitHub-Veröffentlichung aktualisiert**.
- Diese Version ist ein **Prerelease** und noch keine formelle Stable-Version.

## CI-Gate und Versionsidentität

- Der GitHub-Release Candidate wird nur aus einem **grünen `NexVerse CI`-Push-Build auf `main`** veröffentlicht.
- GitHub-Tag: `v0.9.3.8-rc1`.
- Versionsquelle: `OpenSim/Framework/VersionInfo.cs` mit `VersionNumber = "0.9.3.8"` und `VERSION_FLAVOUR = Flavour.RC1`.
- World API: `GET /api/v1/version` → `OpenGenesisLINK 0.9.3.8 RC1`.
- Die CI testet u.a. Release-Build, Economy-Datenbankmatrix, World-API-Smoke, Hypergrid-Login, Firestorm und NexBus-Regressionsverträge.

## Betrieb und Rollback

Vor einem Produktivupdate die lokale `bin/OpenSim.ini`, Regionskonfigurationen, Datenbanken sowie `/etc/nexverse/nexverse.env` sichern. **Keine bestehenden API-Schlüssel oder NexBus-Secrets neu erzeugen.**

Code auf dem Zielhost nach `git pull --ff-only origin main` lokal mit `./runprebuild.sh` und `./compile.sh` erstellen und Robust vor den Simulatoren neu starten. Lokale Änderungen an konfigurationskritischen Dateien vorher sichern; niemals ungeprüft überschreiben.

Für Stable später eine getrennte Freigabe mit `Flavour.Release` und eigenem Tag `v0.9.3.8` erstellen.
