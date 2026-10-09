# OGLVoice – zentrale WebRTC-Sprachplattform (Architekturentwurf)

**Zielmeilenstein:** OpenGenesisLINK 0.9.3.9 Dev  
**Status:** Architektur und Implementierungsplan; **noch nicht im OpenGenesisLINK-Server implementiert**.  
**Vorhandene Grundlage:** Ein bestehender, separat betriebener NexVoice/LiveKit-Dienst kann weiterentwickelt werden; dessen tatsächliche Quellcode-/API-Kompatibilität muss vor Übernahme geprüft werden.  
**Bestehende Webpräsenz:** https://voice.stadt-nexverse.de/ (OGLVoice-Verwaltung und späteres Mandantenportal; bestehende Seiteninhalte nicht Teil dieses Dokuments).

## 1. Produktziel

**OGLVoice** ersetzt die im Server entfernten Vivox-/FreeSwitch-Implementierungen durch einen selbst betriebenen, **zentralen, mandantenfähigen WebRTC-Voice-Dienst**. Das operative Prinzip entspricht einem externen zentralen Voice-Provider: Eine ganze virtuelle Welt kann einen einzigen Voice-Provider verwenden; es wird **kein Voice-Medienserver je Simulator** verlangt.

- **Robust-/HG-Grid:** Voice-Authority und Mandant werden zentral in **Robust.HG.ini / Robust.ini** ausgewählt. Alle kompatiblen OpenGenesisLINK-Simulatoren mit integriertem OGLVoice-Regionmodul ermitteln den Provider automatisch über einen **authentifizierten** Robust-Dienst (Auto-Discovery; nicht aus ungesicherten Client-Metadaten).
- **Standalone:** Eigene Voice-Konfiguration in **OpenSim.ini**; dieselbe zentrale OGLVoice-Infrastruktur kann trotzdem genutzt werden.
- **Besucher aus Hypergrid:** HG-Avatare im Ziel-Grid nehmen ohne lokale NexVerse-Kontoanlage am örtlichen Voice teil, sofern sie durch das Ziel-Grid authentifiziert sind, der Viewer WebRTC unterstützt und Voice in Estate/Parcel zulässig ist.
- **NexVerse als First-Party-Mandant:** **UNLIMITED** für Avatare, Simulatoren, Regionen, Voice-Sessions, Kanalanzahl und HG-Gäste; keine künstlichen abrechnungsbezogenen Quoten. Reale Kapazität hängt weiterhin von Hardware, Bandbreite und Media-Clustern ab.
- **Dritt-Grids:** Isolierte Tenants mit späteren tarifabhängigen Grenzen und Abrechnung. Es gibt bis zur Freigabe keine fiktiven Preise oder vertragliche Zusagen.

Der bestehende Gridbetrieb und Firestorm/Hypergrid dürfen bei deaktiviertem OGLVoice nicht beeinflusst werden. Eine zentrale Konfiguration macht eine **nicht angepasste** OpenSimulator-Installation nicht automatisch voice-fähig: erforderlich sind passende Viewer-CAPS und der OGLVoice-Simulatoradapter.

## 2. Komponenten

1. **OGLVoice Control Plane** (separater Dienst): Tenant Registry, Provider-Discovery, Sitzungs-/Raumverwaltung, Berechtigungen, kurzlebige Credentials, Limits, Zählung, Admin-API, Revocation, Audit und später Billing.
2. **LiveKit Media Plane**: WebRTC SFU, Audio-Tracks, Signalisierung für OGLVoice-kompatible Clients, dynamische Lastverteilung, STUN/TURN (z. B. Coturn), Reconnect und Clusterbetrieb. Keine LiveKit-Admin-Keys an Viewer.
3. **OGLVoice Robust Connector**: eine vertrauenswürdige Grid-Authority für das ganze Grid, signierte/geprüfte Service-Discovery für Simulatoren, registrierte Grid-/Region-Identitäten und Grid-weite Group-/Direct-Voice-Sitzungen.
4. **OGLVoice Region Module (C#)**: Scene-/Estate-/Parcel-Voice-Policies, aktive Anwesenheit und Agent-Sitzung prüfen, Capability-Registrierung, Region/Parcel-Room, Avatar-Positionsupdates und Wechselereignisse. Konfiguration im Gridmodus standardmäßig vom Robust beziehen.
5. **OGLVoice Viewer Compatibility Gateway**: Unterstützung des konkreten Firestorm-/Second-Life-WebRTC-CAPS-/Signalisierungsvertrags. Protokoll-Adaption **muss** nachgewiesen werden: Firestorms WebRTC-Voice-Signalisierung ist nicht automatisch mit dem LiveKit-Client-SDK-Protokoll kompatibel. Möglicherweise sind ein eigenständiger Signalisierungs-/Media-Adapter oder Änderungen am zukünftigen OGL-Viewer erforderlich.
6. **OGLVoice Admin/Citizen API**: Tenant- und Gridverwaltung, Providerverbindungen, Nutzungs-/Gesundheitsstatus, Moderation und ggf. Nutzerpräferenzen. Provisionierung aus dem Viewer nur über authentifizierte Simulator-CAPS; nicht direkt über Admin-Tokens.
7. **Optionaler Federation Gateway**: Später gridübergreifende, von beiden Parteien ausdrücklich autorisierte Gruppen- und Direktgespräche. Dies ist **nicht** erforderlich, damit ein HG-Besucher während seines Aufenthalts im lokalen Ziel-Grid am regionalen Voice teilnimmt.

### Medienweg

Viewer/WebRTC-Client ↔ OGLVoice-Signalisierung/LiveKit ↔ andere Viewer.  
Robust/Simulator ↔ OGLVoice Control Plane nur für Identity, Raumzuordnung, Policy und Provisionierung.  
**Audio darf nicht durch Robust geroutet werden.** Der Robust-Dienst ist Steuerungsinstanz, nicht Medienrelay.

## 3. Discovery und Vertrauensmodell

**Gridmodus:** [OGLVoice] wird am Robust für den Grid-Tenant aktiviert. Die Simulatoren erhalten ausschließlich öffentliche Provider-URLs und kurzlebige, simulatorgebundene Service-Credentials bzw. eine autorisierte Provisionierungs-API über einen geschützten internen Service. Der öffentliche `get_grid_info`-Endpunkt darf unvertrauliche Fähigkeiten und Provider-URL anzeigen, **ist aber keine vertrauenswürdige Quelle für API-Geheimnisse**. Auch `GridCommon.ini` darf nur einen Bootstrap zum bekannten Robust benötigen; keine Tenant-API-Secrets pro Region kopieren.

**Standalone:** [OGLVoice] in der lokalen OpenSim.ini. Eine Installation kann sich als eigener Grid-Tenant registrieren und erhält streng beschränkte maschinelle Credentials. Standalone darf keinen Fremd-Tenant per frei wählbarem Namen `nexverse` impersonieren.

**Fallback:** Wenn Robust oder OGLVoice nicht verfügbar ist, bleibt die Region erreichbar. Voice wird sichtbar als nicht verfügbar signalisiert oder versucht kontrollierte Wiederverbindung; keine ungesicherte automatische Ausweichroute auf unbekannte Provider.

### Beispielkonfiguration – ZIELENTWURF, NOCH NICHT GÜLTIGE IMPLEMENTIERTE INI

```ini
; Robust.HG.ini / Robust.ini – ein Grid, viele Simulatoren
[OGLVoice]
    Enabled = true
    Mode = GridAuthority
    ServiceUrl = "https://voice.stadt-nexverse.de"
    TenantId = "nexverse"
    CredentialSource = "environment:OGLVOICE_GRID_CREDENTIAL"
    AutoProvisionRegions = true
    IncludeHypergridGuests = true
```

```ini
; OpenSim.ini – Standalone-Betrieb oder explizite Sonderkonfiguration
[OGLVoice]
    Enabled = true
    Mode = Standalone
    ServiceUrl = "https://voice.stadt-nexverse.de"
    TenantId = "external-grid-identifier"
    CredentialSource = "environment:OGLVOICE_STANDALONE_CREDENTIAL"
    IncludeHypergridGuests = true
```

`Mode = Auto/GridManaged` ist als Runtime-Standard für Grid-Simulatoren vorgesehen: Wird ein vertrauter Robust verbunden und OGLVoice dort freigeschaltet, übernimmt der Simulator automatisch dessen Einstellungen. Lokale Overrides sind nur explizit und berechtigungsgeprüft gestattet. Die endgültigen INI-Schlüssel müssen mit implementiertem Adapter, Prebuild, OpenAPI und Regressionstests synchron festgelegt werden. Diese Beispiele **nicht** unverändert produktiv einspielen.

## 4. Hypergrid-Besucher

- **Authentifizierung:** Das **Ziel-Grid** bestätigt via bestehender Hypergrid-/Gatekeeper-/Agent-Sitzung, dass der fremde Avatar sich tatsächlich in der Region befindet. Bloße Avatar-UUID oder vom Browser gesendete HomeURI reichen nicht.
- **Gastidentität:** Kanonische Home-Grid-Identität + ursprüngliche Avatar-ID (mit Bezug zur aktuellen Grid-Agent-Sitzung). Eine temporäre lokale UUID darf nicht mit permanenten lokalen Avataren oder fremden Grids kollidieren.
- **Voice-Join:** Viewer fragt Region-CAPS ab → Simulator prüft Region, Estate/Parcel und HG-Präsenz → OGLVoice erteilt kurzlebigen raumgebundenen Token → Viewer erscheint in regionalem Voice.
- **Abreise:** Logout, Teleport, Regionwechsel, Ban oder Rights-Änderung widerrufen den bisherigen Raumzugriff; Sitzungen haben TTL und werden serverseitig überwacht. Kein stillschweigender Zugriff auf fremde private Räume.
- **Aufenthalt im fremden Grid:** Voice-Regeln des **besuchten** Grids bestimmen den lokalen Sprachraum. Für Gespräche über zwei unabhängige Grids hinweg ist eine separate **opt-in Federation**, beiderseitige Vertrauensprüfung und Protokollverhandlung erforderlich.
- **Providerfremde Viewer:** Wenn ein Viewer die benötigte WebRTC-Voice-Capability nicht unterstützt, muss das sichtbar und sicher fehlschlagen; kein verstecktes Vivox-Fallback.

## 5. Kanäle und Spatial Voice

- **Region Voice** und **Parcel Voice** mit bestehenden Estate-/Parcel-Flags und Mute/Moderation.
- **Spatial Voice** mit serverautoritativer Avatarposition/Rotation und passender Audio-Spatialization im Viewer oder einem dedizierten Mischer. LiveKit als SFU implementiert **nicht automatisch** die gesamte räumliche Klangberechnung: Positionsupdates, Hörweite, Stereo/3D-Panning und Skalierungsgrenzen sind getrennt zu entwickeln und zu messen.
- **Group Voice** mit bestehender NexGroups-/Groups-V2-Mitgliedschaft und Powers.
- **Direktanruf, Ad-hoc und Konferenzen** unabhängig von einer einzelnen Region; explizites Anruf-Consent und Block-/Mute-Regeln.
- **Regionwechsel ohne Gesprächsabbruch:** Raumwechsel vorbereiten, neue Berechtigung validieren und erst danach alte Raumzuordnung verlassen (make-before-break, sofern Client-/Protokollvertrag es unterstützt).

### 5.1 Verbindliche Voice-Kugel über **allen** sprechfähigen Avataren

**Definition of Done, keine optionale Dekoration:** In Firestorm und im künftigen OGL-Viewer soll über **jedem in der jeweiligen sichtbaren Voice-Umgebung verbundenen Avatar** (nicht nur über dem eigenen Avatar) die klassische **grau/weiße Voice-Kugel** am Kopf zu sehen sein. Sobald dieser Avatar spricht, müssen andere Viewer **an dessen Kugel** dynamisch die Sprachaktivität erkennen können. Auch fremde Hypergrid-Avatare müssen exakt gleich behandelt werden.

- **Bereit/verbunden:** graue/weiße ruhige Kugel oberhalb der Kopfposition des jeweiligen Avatars; Sprech- bzw. Mikrofonberechtigung je nach Status. Keine gefälschte Kugel bei gar nicht verbundener Voice-Sitzung.
- **Spricht:** die Kugel des **tatsächlichen Sprechers** zeigt animierte Sprachwellen (typisch grün); deren Intensität folgt einem geglätteten, wirklichen Audiopegel/VAD-Signal. Ein roter Übersteuerungsindikator ist optional und darf nicht aus unbelegten Werten abgeleitet werden.
- **Schweigt/Push-to-Talk losgelassen/Mikrofon gemutet:** Wellen verschwinden zeitnah; die verbundene Kugel bleibt sichtbar. Eine lokale Stummschaltung durch den Zuschauer darf keine falschen Sprechdaten für andere erzeugen.
- **Voice getrennt/Region verlassen/anderer, nicht sichtbarer oder nicht teilbarer Voice-Kanal:** veraltete Anzeige und Speaking-Signal werden zeitnah entfernt. Teilnehmer anderer privater Gespräche werden nicht irrtümlich als lokaler Region-Sprecher angezeigt.
- **Alle Teilnehmer:** eigener Avatar, andere lokale Avatare und HG-Gäste; pro betrachterseitigem Viewer dieselbe serverbestätigte Zuordnung `voice_participant_id ↔ home_grid_identity/avatar_id ↔ aktuelle regionale Agent-ID`. Andere Viewer erhalten nicht bloß einen lokalen Self-Mikrofonpegel, sondern den **roombasierten, teilnehmerspezifischen Remote-Speaking-Status**.
- **Datenfluss:** LiveKit-Audio-/Active-Speaker- bzw. Track-Pegel-Informationen werden von der OGLVoice-Media-/Signalisierungsebene an **alle berechtigten Zuschauer im gleichen Sprachraum** vermittelt; der Viewer zeichnet Orb/Wellen selbst an der korrekten Avatar-Kopfposition. Das ist **kein Inworld-Prim**, kein LSL-Objekt und keine durch die Region verschickte Partikeltextur.
- **Aktualität:** kurze, begrenzte Speaking-Updates (Ziel-Latenz unter 500 ms bei normalem Netz), geglättete Pegel, automatische Silence-/Disconnect-Timeouts und Reconnect-Synchronisierung. Keine dauerhafte Speicherung von Sprachpegeln zur Profilbildung.
- **Viewer-Präferenz:** Firestorm besitzt eine vom Anwender abschaltbare Einstellung „Show voice visualizers over avatars“. OGLVoice muss vollständige Präsenz-/Sprechdaten kompatibel liefern, **kann aber die persönliche Viewer-Einstellung nicht serverseitig erzwingen**. Im eigenen OGL-Viewer standardmäßig aktiv und benutzerseitig deaktivierbar; Barrierefreiheit berücksichtigen.
- **Wichtiges Kompatibilitätsgate:** Wenn der unveränderte Firestorm über seine WebRTC-Schnittstelle Remote-Speaker-Zustände nicht aus dem OGLVoice-Gateway erhält, ist die Funktion **nicht erfüllt**, auch wenn Mikrofon und Audio schon funktionieren. Voice-Provisionierung, Remote-Teilnehmerliste, Pegel/Speaking und Darstellung müssen gemeinsam mit mindestens zwei getrennten Viewer-Instanzen abgenommen werden.

**Abnahmetest:** A und B in derselben Region, beide Voice an: A sieht Kugeln über A **und B**, B über B **und A**. Spricht A, sehen **beide** die Wellen an **A**, aber nicht fälschlich an B; beim Wechsel der Sprecher umgekehrt. Mit C als Hypergrid-Gast erscheinen drei korrekt zugeordnete Kugeln und dessen Sprache auf den anderen beiden Viewern. Wiederholen nach Teleport, Parcel-/Regionwechsel, Reconnect und PTT/Mute; bei deaktivierter Visualizer-Präferenz ist das Nichtanzeigen ausdrücklich erwartbar.

## 6. Mandanten und Tarife

### NexVerse (systemseitig fest gebundener First-Party-Tenant)

`tenant = nexverse`, `plan = unlimited`: keine konfigurierbaren Lizenzgrenzen für aktive Avatare, Simulatoren, Regionen, Sprachkanäle, Voice-Minuten oder HG-Gäste. **Kein frei wählbares Profil bei Tenant-Registrierung**; Privileg wird nur durch vertrauenswürdige OGLVoice-Betreiberkonfiguration vergeben. Schutz vor DoS, Missbrauch, übergroßen Paketen und kapazitätsbedingten Ausfällen bleibt aktiv.

### Externe Tenants (später)

Paketmodell mit möglichen Grenzen: registrierte Simulatoren/Regionen, gleichzeitige Voice-Teilnehmer, parallele Räume, Sitzungen und Nutzungsumfang. Zählung nach **gastgebendem Tenant**: HG-Besucher werden zur aktiven Nutzung des besuchten Grids gerechnet. Transaktionssichere Limits ohne Soft-Bypass bei parallelen Joins; Rate-Limits, Audit, Plan-Upgrade, Blockierung und nachvollziehbares Metering. **Keine Preisliste festgelegt.**

Tenant-Isolation in Token, Room-ID, Teilnehmeridentität, Abrechnung und Support. Keine Raumkollisionen bei gleichen Regionen-UUIDs auf verschiedenen Grids. PII sparsam, kurze Speicherung, datenschutzgerechte Löschung.

## 7. Viewer-Protokoll und Kompatibilitätsgate

2026 unterstützen moderne Firestorm-Versionen Second-Life-WebRTC-Voice. Das bedeutet **nicht**, dass LiveKit-Token und -Signalisierung direkt vom unveränderten Firestorm akzeptiert werden. Vor einem Produktivversprechen sind anhand des tatsächlich verwendeten Firestorm-Builds zu validieren:

- CAPS wie `ProvisionVoiceAccountRequest`, `ParcelVoiceInfoRequest` und WebRTC-`VoiceSignalingRequest`, einschließlich korrektem `voice_server_type`, LLSD, SDP/ICE und Fehlermeldungen;
- kompatible Signalisierung und Media-Transportschicht zu LiveKit oder ein expliziter Protokoll-Gateway;
- sichtbare Speaking-Indikatoren, Positionen, Mikrofon/Push-to-Talk, Mute, individuelle Lautstärke und Gerätewahl;
- Gruppen-/P2P-Gespräche, HG-Guests und Sprachkanalwechsel ohne falsche Berechtigungsübernahme.

Das langfristige eigene OGL-Viewer-Projekt kann die LiveKit-/OGLVoice-Signalisierung direkt unterstützen; für Firestorm ist die konkrete Kompatibilität zuerst zu beweisen.

## 8. Implementierungsreihenfolge für 0.9.3.9

1. Bestand des bestehenden NexVoice-Dienstes/LiveKit-Deployments und des Firestorm-Builds prüfen; Architekturspike zu LLSD-CAPS, WebRTC-Signalisierung und möglichem Media-Gateway.
2. OGLVoice Control Plane mit Tenant-Registry, NexVerse-Unlimited, serverseitigem Planmodell, Auth, Room-/Session-Management und getrennten Audit/Metrics aufbauen.
3. C#-Robust-Connector und Simulator-Regionmodul; Auto-Discovery für beliebig viele Simulatoren pro Grid, Standalone-Konfiguration und fail-closed Privilegienmodell.
4. Lokale Avatar-/HG-Gast-Identität, Presence, Join/Leave, Parcel-Policy und Timeouts/Revocation realisieren.
5. Firestorm-Protokollgateway und LiveKit-Audio unter zwei verschiedenen Viewer-/Simulatorprozessen integrieren; räumliche Audiofunktion separat abnehmen.
6. Gruppen, P2P, Konferenzen, Region- und Teleportübergänge, Moderation und Rechte.
7. Portal `voice.stadt-nexverse.de` an die verwaltende OGLVoice-API anbinden; externe Pakete später ohne Einfluss auf NexVerse Unlimited ergänzen.
8. CI + Live-Abnahme inklusive HG-Gast, Multi-Node, Reconnect, Audio-Mute, Token-Missbrauch, Cross-Tenant-Isolation, Last/Skalierung, Ausfall und Backup.

## 9. Abnahmekriterien

- **Einmalige zentrale Konfiguration:** Mehrere OGLVoice-fähige Simulatorprozesse übernehmen die vertrauenswürdige Robust-Konfiguration ohne duplizierte Voice-Secrets.
- **Standalone:** Direkt konfigurierte Region funktioniert unabhängig von Robust.
- **Hypergrid:** Besucher ohne lokales Gridkonto werden im Ziel-Grid korrekt authentifiziert und in dessen Region-Voice hör-/sichtbar; bei Teleport/Logout endet alte Berechtigung.
- **NexVerse Unlimited:** Keine abrechnungsbezogene Teilnehmer-/Regionen-/Simgrenze in Tests; Sicherheits- und physische Kapazitätsgrenzen weiterhin wirksam.
- **Dritt-Tenants:** Trennung, Limits, Metering und klare Fehlerantwort bei überschrittener Quote.
- **Firestorm:** Native WebRTC-Voice-Funktionen mit nachgewiesenem Signalisierungs-/Media-Handshake statt nur funktionierendem Browser-Demo.
- **Sicherheit:** Keine OGLVoice-Administrator-Schlüssel im Viewer/CAPS, HMAC/OAuth/mTLS für Servicekommunikation, kurzlebige Tokens, Replay-Schutz, serverseitige Rechteprüfung.
- **Betrieb:** Monitoring, Redundanzoption, Recovery und dokumentierter Rollback; vollständige Deaktivierung hat keine Auswirkungen auf Text-IM/Region/Hypergrid.

## 10. Bezug zur Plattform-Roadmap

Primäre Implementierung: **0.9.3.9 – OGLVoice**. Federierte private Gespräche zwischen voneinander unabhängigen Grids und internationale Abrechnung bleiben spätere Erweiterungen. Bereits geplante **NexFederation** kann OGLVoice-Trust- und Discovery-Kontrakte später übernehmen.

OGLVoice ist der Plattform-/Dienstenamen; **NexVerse** bleibt der unbegrenzte Betreiber-Mandant. Bestehende NexVoice-Verweise werden erst dann umbenannt, wenn die Migration auf getesteten Quellcode/Services tatsächlich erfolgt.
