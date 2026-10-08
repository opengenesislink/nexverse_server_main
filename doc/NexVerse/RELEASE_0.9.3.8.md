# OpenGenesisLINK 0.9.3.8 — Stable Release

**Freigabedatum:** 8. Oktober 2026  
**Status:** Stable  
**Runtime:** `OpenGenesisLINK 0.9.3.8`  
**Tag:** `v0.9.3.8`  
**Vorabversion:** `v0.9.3.8-rc1` (erfolgreich validiert)

OpenGenesisLINK 0.9.3.8 ist die erste Stable-Veröffentlichung der Entwicklungsstrecke 0.9.3.5–0.9.3.8 nach der zuletzt stabilen Version NexVerse 0.9.3.4. Die Grundlage bleibt OpenSimulator 0.9.3.0; NexVerse ist weiterhin der Name des virtuellen Grids.

## Neuerungen

### Suche und Entdeckung (0.9.3.8)
- **NexSearch:** Personen, Gruppen, Regionen, Parzellen, Places, Events, Classifieds, Experiences, kauf-/mietbares Land und Destinationen.
- **Places und Destination Guide:** Persistente Orts- und Veranstaltungsinformationen, Moderation, Kategorien, Collections, Popularität und Teleportlinks.
- **Land Portal:** Landangebote mit NV$-Preisen und Anbindung an den autoritativen Commerce-/Land-Dienst.
- **Viewer-Anbindung:** Optionale `NexSearch`, `NexPlaces`, `NexLandPortal` und `NexDestinationGuide` CAPS, ohne die Standard-Firestorm-Suche zu entfernen.

### Enthaltene Meilensteine 0.9.3.5–0.9.3.7
- **Inventar / OAR / IAR:** API- und Archivverwaltung, persistente Job Engine, Wartungs-Worker.
- **NexBus und NodeAgent:** Verteilter HMAC-geschützter Event-Transport, Node- und Regionszustände, regionsübergreifende Objekt- und Estate-Kommunikation mit `llRegionSayTo` und `llEstateSay`.
- **NV$ Economy / Banking / Land:** Autoritatives Double-Entry-Ledger, Wallets, Viewer-Zahlungen, Banking-/Commerce-/Landabläufe.
- **NexGroups und NexExperiences:** Gruppenverwaltung, Experience-Rechte und persistentes Experience-K/V.

## Abnahme

Der Grid-Betreiber hat alle zehn vereinbarten Live-Testkategorien als bestanden gemeldet. Insbesondere wurden bestätigt:

- Firestorm Login, LSL Bridge, Inventar, Gruppen, IM, Assets, Map, Search und Teleport.
- NV$-Kontostände, Zahlungen und operative Kontofunktionen.
- Hypergrid / Offline-IM und Regions-/Estate-Verwaltung.
- OAR-/IAR-Backup und Wiederherstellung.
- `llRegionSayTo()` PING/PONG und `llEstateSay()` in unterschiedlichen Regionen.
- **NexBus-Verbindung zwischen zwei getrennten Simulatorprozessen** erfolgreich geprüft.
- **RC1 erfolgreich im Live-Betrieb geprüft**, anschließende Freigabe durch den Grid-Betreiber am 8. Oktober 2026.

Diese Live-Tests sind **Betreiberangaben** und wurden nicht unabhängig über einen Remote-Zugriff dieses Release-Prozesses reproduziert. Die GitHub-CI prüft zusätzlich Build, Vertrags-/Regressionsprüfungen, World-API-, Firestorm-/Hypergrid-Runtime-Smoke-Tests und die NV$-Datenbankmatrix. Stable wird ausschließlich nach grüner **main**-CI auf den exakt geprüften Commit getaggt.

## Bekannte Restbeobachtungen

- Ein isolierter Live-Test der aktivierten Cross-Owner-Nachrichtenberechtigung wurde nicht ausdrücklich einzeln bestätigt.
- Eine ältere einzelne NexBus-Ablehnung mit `HTTP 400` wurde durch verbesserte Diagnose in PR #107 adressiert; die damalige konkrete Ursache ist nicht nachträglich ermittelt. Neue oder gehäufte Ablehnungen müssen untersucht werden.
- Die operator-bestätigte Karten-/Bootstabilität bedeutet nicht, dass sämtliche langfristigen Performance-Optimierungen implementiert oder benchmark-verifiziert sind.
- Die GitHub-Veröffentlichung aktualisiert **nicht** automatisch die laufende Installation auf `v72454`.

## Deployment-Hinweise

Den Server erst bei einem Wartungsfenster aktualisieren. Vorher Datenbanken, Estate-/Regions- und OpenSim-/Robust-Konfiguration, OAR/IAR sowie die Secrets-Umgebung `/etc/nexverse/nexverse.env` sichern.

```bash
cd /opt/robust
git status --short
git fetch origin main
git pull --ff-only origin main
./runprebuild.sh
./compile.sh
```

**Keine** bestehenden API-, NV$- oder NexBus-Keys durch Release-Installation neu generieren. Lokale INI-Anpassungen dürfen nicht ungeprüft überschrieben werden. Robust und Simulator anschließend kontrolliert neu starten; `/api/v1/version` muss `OpenGenesisLINK 0.9.3.8` anzeigen.

Das neue Stable-Tag `v0.9.3.8` wird niemals über einen bestehenden Tag geschrieben.
