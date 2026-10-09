# OpenGenesisLINK 0.9.3.10 – MANUELLE Abnahme

**Verfahren:** ausschliesslich von einem Menschen ausgefuehrte und bestaetigte Tests. Keine automatisierte Abnahme, keine automatischen Betriebsaenderungen, kein Stable-Tag durch dieses Dokument.

**Release-Kandidat:** OpenGenesisLINK **0.9.3.10 Dev** (intern gebuendelte Arbeiten aus 0.9.3.9 und 0.9.3.10). **Referenz fuer Rollback:** freigegebene **0.9.3.8 Stable**.

**Aktueller Abnahmezustand (09.10.2026): NICHT ABGENOMMEN.**
Der hinterlegte Desktop-Commander-Rechner ist offline, und reale Firestorm-, HG-, Voice-/Robust- und Lasttests koennen nicht bestaetigt werden. CI-Ergebnisse oder Quellcodeverfuegbarkeit sind **keine** manuelle Abnahme. Pathfinding-NavMesh und SL-kompatible Pathfinding-LSL-Kommandos sind laut Release-Train noch nicht vollstaendig umgesetzt: das ist ein funktionaler **Release-Blocker**.

## 1. Abnahmeblatt – vor Testbeginn ausfuellen

| Angabe | Eintrag |
|---|---|
| Datum/Uhrzeit mit Zeitzone | __________________ |
| Verantwortlicher Betreiber/Tester | __________________ |
| Git-Commit (exakt SHA) | __________________ |
| .NET-/Go-Build und Simulator/Robust-Stand | __________________ |
| Firestorm-Build (Beispiel Screenshot: 7.2.4.80712) | __________________ |
| Testsystem/Host (ohne Passwoerter) | __________________ |
| Robust/Grid-ID, HG-Modus | __________________ |
| Simulator A / Region, Simulator B / Nachbarregion | __________________ |
| LiveKit/SFU und OGLVoice Media Bridge (Host/Status) | __________________ |
| Lokaler Avatar A, B, C (pseudonymisiert) und HG-Gast D | __________________ |
| Backup-ID/Restore-Medium/letzter erfolgreicher Test | __________________ |
| Messgrundlage 0.9.3.8 und vereinbarte Toleranzen | __________________ |

**Statuscodes fuer jeden Einzelfall:**
- **BESTANDEN** – selber durchgefuehrt, Ist-Ergebnis entspricht der Erwartung und Nachweis ist hinterlegt.
- **FEHLGESCHLAGEN** – reproduzierbar abweichendes Verhalten. Bug/Log/Version dokumentieren.
- **BLOCKIERT** – Vorbedingung fehlt oder Funktion noch nicht implementiert; **niemals** als bestanden zaehlen.
- **NICHT GETESTET** – noch nicht manuell durchgefuehrt.

**Pro Testfall eintragen:** Status | Datum | Tester | Screenshot-/Video-ID | relevante Logstelle mit Uhrzeit | Fehler-/Issue-Link. Zugangsdaten, Voice-API-Keys, Bearer-Tokens, signierte Requests, private IMs und ungefilterte Bewohnerdaten duerfen **nicht** in das Protokoll.

## 2. Vorbereitungen – Pflicht vor Produktivaenderungen

- [ ] **M00**: Wartungsfenster und Testteilnehmer abgestimmt; Version und Commit von Robust, beiden Simulatoren, OGLVoice Go Gateway und Firestorm notiert. **Soll:** Alle Serverseiten passen zum gleichen Quellstand.
- [ ] **M01**: Vollstaendiges, pruefbar lesbares Backup von Inventar-, Asset-, User-/Grid- und relevanten Regiondaten; OAR/IAR falls fuer diese Umgebung notwendig; sichere Kopie aller lokalen INI-/Dienstkonfigurationen. **Soll:** Backup-ID bekannt, Restore-Ablauf getestet oder am separaten Testsystem reproduziert.
- [ ] **M02**: Datenbankmigrationen fuer Thumbnail-UUIDs kontrolliert **zuerst am zentralen Inventardienst**, dann Simulatoren aktualisiert. Keine ungenehmigte Datenbankloeschung. **Soll:** Bestandsitems und gespeicherte Outfits vorhanden, Spalten \`thumbnailID\` fuer Items/Folder migriert.
- [ ] **M03**: Provisorisches Staging mit **2 Simulatoren, 2 Regionen, 3 lokalen Viewern, 1 externem Hypergrid-Gast** vorbereitet. Firestorm-Spracheingang/-ausgang und Visualizer-Option im jeweiligen Viewer aktiviert. **Soll:** Eigene Stimme und alle fremden Stimmen separat testbar.
- [ ] **M04**: OGLVoice Media Bridge nur auf dem freigegebenen Testsystem starten, \`/healthz\` lokal auf Erreichbarkeit pruefen; Pion/LiveKit- und STUN-/TURN-Netzroute pruefen. Robust \`EnableFirestormGateway=true\` **erst nach** bestehendem Backup, HTTPS- und Node-ACL-Check. **Soll:** Kein Livemedium wird ungeprueft aktiviert; LiveKit- und Bridge-Endpunkt nicht verwechselt.

Beispiele fuer **manuell einzeln auszufuehrende** Read-only-Diagnosen (kein Testskript):

\`\`\`bash
git rev-parse HEAD
git status --short
systemctl status oglvoice-media --no-pager
curl --fail --show-error http://127.0.0.1:19098/healthz
\`\`\`

Die Dienstbezeichnung muss mit der realen systemd-Unit uebereinstimmen. Die lokale \`/healthz\`-Antwort beweist lediglich den laufenden Gateway-HTTP-Prozess – **keine** WebRTC-, LiveKit- oder Audiofunktion. Keine Geheimnisse mit \`systemctl cat\`/Environment-Dateien in Screenshots offenlegen.

## 3. Inventar und Outfit – manuelle Firestorm-Abnahme

### INV-01 Fehlermeldung InventoryThumbnailUpload beseitigt

1. Firestorm nach Serverupdate vollstaendig schliessen und frisch anmelden.
2. Vorhandenes, dem Test-Avatar gehoerendes Inventarobjekt auswaehlen, Snapshot/Thumbnail aufnehmen und hochladen.
3. **Soll:** Keine Meldung \`Regionsfaehigkeit "InventoryThumbnailUpload" konnte nicht abgerufen werden\`; ein einmaliger Bild-Upload wird vollstaendig abgeschlossen; das Bild ist am Inventargegenstand sichtbar.
4. Ausloggen, Viewer beenden, neu einloggen und dieselbe Item-UUID wieder aufrufen.
5. **Soll:** Thumbnail unveraendert sichtbar; Asset-Service kann die \`new_asset\`-Textur liefern; \`inventoryitems.thumbnailID\` ist nicht die Null-UUID. Datum und Thumbnail-UUID nur bei Bedarf pseudonymisiert protokollieren.

Ergebnis INV-01: **NICHT GETESTET** | Nachweis: __________

### INV-02 Gespeichertes Outfit in Firestorm

1. Bei bestehendem Benutzer das Buergerportal-API \`GET /api/v1/inventory/outfits/health\` **mit eigenem** Token (Scope \`inventory:read\`) manuell aufrufen. Antwort ohne Token sichern.
2. Falls nur \`my_outfits_folder_missing\`, explizit und einmalig \`POST /api/v1/inventory/outfits/ensure-folders\` mit \`inventory:write\` ausfuehren. **Nicht** bei einem nicht geklaerten Inventarfehler automatisch reparieren.
3. **Soll:** \`My Outfits\` vorhanden; \`Current Outfit\` bleibt unveraendert. Zweiter POST gibt \`created=false\` zurueck und erzeugt **keine** doppelten Ordner.
4. Firestorm: \`Aussehen -> Outfit-Galerie\` oeffnen, ein neues benanntes Test-Outfit mit mindestens einem Wearable und gegebenenfalls Attachment speichern. Optional Thumbnail anhaengen.
5. Vollstaendig ab- und anmelden.
6. **Soll:** Benanntes Outfit wieder sichtbar, aktive Kleidung unveraendert, keine unerwarteten Verknuepfungsfehler. **"Kein Outfit"** ist nur dann ein Fehler, wenn zuvor nachweislich ein gespeichertes Outfit existierte.

Ergebnis INV-02: **NICHT GETESTET** | Nachweis: __________

### INV-03 Schutz bestehender Kleidung

1. Vor/nach Upgrade Anzahl und IDs gespeicherter Outfit-Unterordner vergleichen.
2. Beim Buergerportal die geschuetzte Systemordner-Operation **mit einem Testbenutzer** versuchen, ohne bestaetigtes produktives Loeschziel zu verwenden.
3. **Soll:** \`Current Outfit\`/\`My Outfits\` lassen sich nicht durch Portal-Rename/Trash zerstoeren; keine Kleidung oder Link-Ziele veraendert. Kein automatischer Avatarreset.

Ergebnis INV-03: **NICHT GETESTET** | Nachweis: __________

## 4. OGLVoice – vier echte Avatar-Sitzungen

**Testrollen:** A, B und C verwenden getrennte lokale Firestorm-Logins und Mikrofone; D besucht als **HG-Gast eines anderen Grids** die Region. Der Test erfolgt bei ausdruecklich erlaubter Voice-Estate-/Parcel-Policy. Die gewaehlte Firestorm-Version und die Option zur Anzeige der Voice-Visualizer sind fuer A/B/C/D zu notieren.

### VOI-01 Bidirektionaler Audiopfad

1. A und B in dieselbe Testregion setzen. Beide Voice **aktivieren** und Mikrofon-/Lautsprecherzuordnung kontrollieren.
2. A spricht, B antwortet; abwechselnd muten und entmuten.
3. **Soll:** A und B hoeren sich gegenseitig klar und ohne dauerhaftes Echo. Log/Stats zeigen Firestorm ↔ Pion WebRTC ↔ LiveKit ↔ Pion ↔ Firestorm; weder 5xx noch wiederholte fehlgeschlagene ICE-Verhandlungen.
4. C dazu einloggen und Schritte wiederholen.

Ergebnis VOI-01: **NICHT GETESTET** | Nachweis: __________

### VOI-02 Graue Voice-Kugel bei ALLEN Avataren

1. A, B, C sichtbar nebeneinander aufstellen und 10 Sekunden schweigen.
2. Jeden Client einzeln beobachten, nicht nur die eigene Kugel.
3. **Soll:** Ueber **jedem voice-verbundenen Avatar** erscheint die graue/helle Voice-Kugel in allen Viewern, deren Visualizer eingeschaltet sind; kein Objekt/Prim noetig.
4. A redet, dann B, dann C. **Soll:** Sprechwellen/aktiver Pegel nur ueber dem richtigen Sprecher, bei Stille/Mute wieder graue Kugel; bei Voice-Verlassen verschwindet sie.
5. Nach D-HG-Beitritt identische Anzeige pruefen; jeder Client muss auch D als entfernten Sprecher sehen.
6. Visualizer in einem Viewer absichtlich deaktivieren: **Soll:** dort keine Anzeige erzwungen, Audio bleibt moeglich.

Ergebnis VOI-02: **NICHT GETESTET** | Nachweis: __________

### VOI-03 Echte simultane Sprache und Spatial-Audio

1. B und C sprechen **gleichzeitig**, A hoert zu und gibt wieder, **beide Stimmen getrennt erkennbar**.
2. A und B in dokumentierte Abstaende bringen: 1 m, 10 m, 30 m, 40 m, 50 m (entsprechend dem konfigurierten 40-m-Radius). Jeweils Audio und Position protokollieren.
3. A dreht sich um 180 Grad ohne Ortswechsel.
4. **Soll:** Pegel faellt mit zunehmender Entfernung, ab 40 m nach aktueller Mixerpolicy keine Stimme, Stereo-Links/Rechts-Verteilung wechselt mit Blickrichtung. Keine fremden Grids/Regionen unberechtigt in derselben Mischung.
5. Die Maximalzahlen an Quellen je Zuhoerer sind **CPU-Sicherungsgrenzen**, keine Einschränkung der NexVerse-Unlimited-Lizenz.

Ergebnis VOI-03: **NICHT GETESTET** | Nachweis: __________

### VOI-04 HG-Gast und Regionswechsel

1. D aus einem **anderen** OpenSim-/HG-Heimatgrid anmelden; in die Testregion teleportieren.
2. Soll: D hoert A/B/C und wird von ihnen gehoert; Kugel/Sprechwellen fuer D und alle lokalen Avatare sichtbar.
3. A und D in die angrenzende, auf anderem Simulator gehostete Region bewegen; Rueckkehr pruefen.
4. Soll: Alte Sitzung wird beendet, neue Sitzung autorisiert; keine dauerhaft doppelten Sprecher, keine nachhaengenden alten Voice-Kugeln, kein Avatar-/Room-Identitaetsverlust.
5. Test auf unberechtigter Parzelle wiederholen. Soll: Verbotene Sprachverbindung wird verwehrt und das alte Audio gestoppt.

Ergebnis VOI-04: **NICHT GETESTET** | Nachweis: __________

### VOI-05 Mute, PTT, Disconnect und Reconnect

1. In Firestorm Push-to-Talk einschalten: Nur bei gedrueckter Sprechtaste darf der Sprecher aktiv sein.
2. Mic-Mute und Deafen getrennt pruefen, anschliessend Stimme wieder aktivieren.
3. Internetverbindung fuer **einen Testclient** kurz unterbrechen (nicht den produktiven Grid-Host). Neue Sitzung nach Reconnect pruefen.
4. Voice-Dienst geordnet auf dem Testsystem neu starten; erneuten Voice-Verbindungsaufbau bestaetigen.
5. Soll: Sprecheranzeigen gehen zurueck auf still/weg; alte Media-Session wird nicht dauerhaft weiterverwendet; normale Chat-, Inventar- und Loginfunktionen bleiben erhalten.

Ergebnis VOI-05: **NICHT GETESTET** | Nachweis: __________

### VOI-06 Externe NAT-/TURN-Wege und Mandantentrennung

1. Mindestens einen Viewer aus einem **anderen WAN/NAT-Netz** verbinden, nicht nur LAN.
2. Soll: STUN/TURN/ICE liefert nutzbare Verbindung; Sprachmedien laufen, ohne dass der interne HTTP-Healthcheck faelschlich als ausreichend gilt.
3. Mit einem separaten Test-Mandanten/Node ueberpruefen, dass keine NexVerse-Unlimited-Rechte, fremden Raum-Mitglieder oder fremde Chat-/Voice-Medien uebernommen werden.
4. Gueltige und veraltete/replayte signierte Simulatorrequests ausschliesslich in isoliertem Testsystem pruefen. **Keine** echten Secrets/Tokens in Dokumente einfuegen.

Ergebnis VOI-06: **NICHT GETESTET** | Nachweis: __________

### VOI-07 Gruppen- und Direktanrufe

1. Mit mindestens drei Avataren einen Gruppenkanal testen, falls fuer 0.9.3.10 als Pflichtumfang versprochen.
2. Einen direkten Voice-Anruf und dessen Ende sowie Moderation testen.
3. Soll: Firestorm-Protokoll, Rechte und Gruppen-/Direktkanal funktionieren real. **Wichtig:** Der Release-Train meldet diese Funktionen noch nicht als abgeschlossen. Bis Implementierung + manuellem Test: **BLOCKIERT**.

Ergebnis VOI-07: **BLOCKIERT – FUNKTIONSUMFANG NOCH OFFEN** | Nachweis: Release-Train

## 5. Pathfinding und LSL – echte Region, kein Mock

### NAV-01 Terrain und NPC

1. Auf einer **Testregion** experimentelles \`[OGLPathfinding]\` und \`[OGLNpcNavigation]\` nur nach Ressourcenfreigabe aktivieren.
2. NPC ueber freie Wege bewegen, danach Hindernis, Wasser, steilen Hang, Terraforming und geaenderte Wegfuehrung pruefen.
3. Soll: Physisch plausibles und begrenztes Folgen ohne Teleport-Cheats, Kollisionsdurchdringung, Infinite-Loop oder Watchdog-Problem.
4. Aktuelle Grenze: Terrain-Snapshot und interner NPC-Follower sind experimentell; vollstaendiger NavMesh/Physik-Blocker. **Bis Implementierung + Livebeobachtung: BLOCKIERT.**

Ergebnis NAV-01: **BLOCKIERT – VOLLSTAENDIGE FUNKTION FEHLT** | Nachweis: Release-Train

### NAV-02 Pathfinding-LSL-Kompatibilitaet

1. In einer Testregion LSL-Referenzfaelle fuer \`llCreateCharacter\`, \`llDeleteCharacter\`, \`llNavigateTo\`, \`llPursue\`, \`llFleeFrom\`, \`llWanderWithin\`, \`llPatrolPoints\`, \`llGetClosestNavPoint\` und \`path_update\` ausfuehren.
2. Soll: Gueltige Signaturen, echte Script-zu-World-Navigation, Ereignisse und Rechte-/Kostenlimits; **keine** leeren Dummy-Stubs als Erfolg werten.
3. Aktueller Release-Train: diese LSL-Integration ist noch nicht umgesetzt. **BLOCKIERT**, nicht als Bestanden dokumentierbar.

Ergebnis NAV-02: **BLOCKIERT – IMPLEMENTIERUNG FEHLT** | Nachweis: Release-Train

## 6. Runtime, Datenintegritaet, Recovery und Freigabe

### OPS-01 Sicherheit und Daten

- [ ] Login/Logout, Ueberland-Teleport und HG-Teleport auf zwei Simulatoren manuell getestet.
- [ ] Bestehendes NV$-Konto/Transaktionen vor/nach Upgrade konsistent.
- [ ] Fremder Avatar erhaelt keinen Zugriff auf anderer Bewohner Inventar, Outfit-Thumbnail oder Voice-Raum.
- [ ] Keine Klartext-Secrets in Logs; eingeschraenkte Voice-/Admin-/API-Rechte geprueft.
- [ ] Estate-/Parcel-Voice deaktivieren und beobachten, ob alte Sessions tatsaechlich enden.

Ergebnis OPS-01: **NICHT GETESTET** | Nachweis: __________

### PERF-01 Baseline und manuelle Lastbeobachtung

Vergleiche **gleiche** Hardware, gleiche Region-/Objekt-/Scriptlast, gleiche Clientzahl und ein vorher festgelegtes Beobachtungsfenster 0.9.3.8 vs. 0.9.3.10:

| Messwert | 0.9.3.8 | 0.9.3.10 | vorher vereinbarte Grenze | Bewertung |
|---|---|---|---|---|
| CPU pro Simulator | | | | |
| RSS/Heap und GC-Pausen | | | | |
| Sim-Tick-/Frame-Latenz | | | | |
| Asset-/Inventar-Antwortzeit | | | | |
| Teleport-/HG-Uebergang | | | | |
| OGLVoice Paketverluste/Jitter/Verzoegerung | | | | |
| Voice-Gateway CPU/RAM je verbundenem Teilnehmer | | | | |
| Watchdog-Warnungen, Crash/Deadlock | | | | |

- Test mit **100+ verbundenen Avataren/Lastquellen** nur in freigegebenem Staging und mit dokumentierter Methode. Wenn nicht moeglich, **BLOCKIERT** – nicht fingierte Ergebnisse eintragen.
- Messgrenzen werden **vor dem Test** vom Betreiber festgelegt. Ohne Baseline/vereinbarte Grenze kein belastbarer Performance-Pass.

Ergebnis PERF-01: **NICHT GETESTET** | Nachweis: __________

### OPS-02 Backup, Rollback und Recovery

1. In einer isolierten Testumgebung einen kontrollierten Ausfall von Simulator oder Media-Gateway provozieren, wieder starten und Reconnect/Inventar pruefen.
2. Datenbank-/Asset-/Regionsbackup auf separatem Ziel wiederherstellen und einen Avatar sowie dessen Outfit-Thumbnail laden.
3. Rueckfall auf \`v0.9.3.8\` **nur** nach Rueckspielen einer dazu kompatiblen Inventar-/DB- und Binaerdatenkopie planen: Nach neuen Schema-/Protokollaenderungen ist **kein** blindes Binary-Downgrade erlaubt.
4. Soll: Keine irreparablen Daten oder liegengelassenen Sessions; dokumentierte RTO/RPO-Grenzen vom Betreiber eingehalten.

Ergebnis OPS-02: **NICHT GETESTET** | Nachweis: __________

## 7. Manuelles Abnahmeprotokoll (ausfuellen, nichts automatisch bewerten)

| Prueffall | Pflicht | Status / Tester / Nachweis |
|---|---|---|
| M00–M04 Vorbereitung/Backup/Rollout | Ja | **NICHT GETESTET** |
| INV-01 Thumbnail Upload & Relog | Ja | **NICHT GETESTET** |
| INV-02 Gespeicherte Outfits & Relog | Ja | **NICHT GETESTET** |
| INV-03 Schutz von Bestandskleidung | Ja | **NICHT GETESTET** |
| VOI-01 Echte WebRTC/LiveKit Zweiweg-Sprache | Ja | **NICHT GETESTET** |
| VOI-02 Graue Kugeln & fremde Sprecher sichtbar | Ja | **NICHT GETESTET** |
| VOI-03 Simultane Sprecher & Spatial Mixer | Ja | **NICHT GETESTET** |
| VOI-04 HG-Gast und Regionswechsel | Ja | **NICHT GETESTET** |
| VOI-05 PTT/Mute/Reconnect/Restart | Ja | **NICHT GETESTET** |
| VOI-06 NAT/TURN/Mandantentrennung | Ja | **NICHT GETESTET** |
| VOI-07 Group Voice / Direktanruf | Ja laut aktueller Roadmap | **BLOCKIERT – nicht fertig implementiert** |
| NAV-01 Reale Terrain-/NPC-Navigation | Ja laut aktueller Roadmap | **BLOCKIERT – NavMesh offen** |
| NAV-02 LSL Pathfinding + path_update | Ja laut aktueller Roadmap | **BLOCKIERT – fehlt** |
| OPS-01 Rechte, NV$, Login/HG | Ja | **NICHT GETESTET** |
| PERF-01 Baseline und Last | Ja | **NICHT GETESTET** |
| OPS-02 Backup/Restore/Rollback | Ja | **NICHT GETESTET** |

**Entscheidung (durch Freigabeverantwortlichen, nicht durch Software):**
- [ ] **NICHT FREIGEGEBEN** (Ausgangsstatus; Pflichtpunkte offen/blockiert).
- [ ] **RC1 NUR FUER MANUELLE WEITERE TESTS** (eigene ausdrueckliche Entscheidung, kein Stable).
- [ ] **STABLE FREIGEGEBEN** – *nur*, wenn jeder Pflichtfall als BESTANDEN mit Nachweis vorliegt, saemtliche Release-Gates im Release-Train geschlossen sind und keine kritischen Fehler mehr offen sind.

**Entscheidungsdatum:** __________

**Freigabeverantwortlicher / Unterschrift:** __________

**Referenz zu manuell gesicherten Belegen (Screenshots, Log-IDs, Tickets):** __________

**Bekannte offene Fehler / Abweichungen / genehmigte Ausnahmen:** __________

**Stable-Tag:** __________ (**leer lassen**, bis ausdrueckliche manuelle Freigabe vorliegt)

> Ein bestandener GitHub-Workflow, gruenes \`go test\` oder bestehende Dokumentation ersetzt **keinen** der oben genannten manuellen Abnahmetests. Diese Dokumentation erstellt und vergibt weder automatisch Teststatus noch GitHub-Releases.
