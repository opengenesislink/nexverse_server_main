# OpenGenesisLINK 0.9.3.10 RC1 – Operator-Abnahme (noch OFFEN)

Status: **Entwicklungs-/Testanleitung; kein RC1-Tag und keine Stable-Freigabe.**
Aktualisiert: 11.10.2026. Freigabe-Gates: [RELEASE_TRAIN_0.9.3.10.md](RELEASE_TRAIN_0.9.3.10.md).

## Verbindlicher Scope

- **0.9.3.10:** .NET-8-Runtime, native OGL-3D-A*-Graph-Navigation, sichere
  opt-in NPC-Physikwegpunkte, wirklich implementierte statische
  Pathfinding-LSL-Teilmenge und vollstaendige Firestorm-/LSL-NexExperiences
  inklusive persistenter Allow/Block/Forget-Rechte.
- **Spaeter:** Havok-kompatibles Firestorm `RetrieveNavMeshSrc` mit kompletten
  UI-Menues, eigener OGL-Viewer/Renderer und fehlende dynamische SL-Character-
  Pathfinding-LSL-Funktionen; auch OGLVoice gehoert nicht in diese Freigabe.
- **Sicherheit:** Keine gefaelschten NavMesh-CAPS, keine automatisch genehmigten
  Experiences, keine echten Secrets in Testprotokollen. Bis Abschluss der
  Live-Abnahme bleiben neue Features ausserhalb einer isolierten
  Entwicklungsregion ausgeschaltet.

## 1. Vorbereitung und beweissicheres Protokoll

1. Notiere Server-/Simulator-Version und den exakten Git-Commit, Firestorm
   Version, `dotnet --info`, eingesetzte Physics Engine, Region-ID/Dimension,
   Datenbanktyp, Robust-/Simulator-Topologie und Datum. Keine Tokens loggen.
2. Sichere die produktive Datenbank, das Inventar, den Experience-Store und
   die Region-INI. Bestaetige **Wiederherstellbarkeit** in einer Kopie.
3. Starte Robust und eine isolierte Developer-Region mit gleichem Build.
   Aktiviere die Testmodule erst nach Kontrollblick auf ihre Config-Defaults.
4. Fuehre den bestehenden Build und alle Tests aus; mindestens:

   ```bash
   python3 tools/ci/verify_stable_release_gate.py
   python3 tools/ci/verify_ogl_npc_navigation.py
   python3 tools/ci/verify_ogl_terrain_navigation.py
   python3 tools/ci/verify_nexgroups_experiences.py
   dotnet run --configuration Release --project tools/ci/OglNpcNavigationRegression/OglNpcNavigationRegression.csproj
   dotnet run --configuration Release --project tools/ci/OglTerrainNavigationRegression/OglTerrainNavigationRegression.csproj
   ```

   Die vollstaendige GitHub Actions `NexVerse CI` muss am gleichen Commit
   gruen sein. **CI ist notwendig, aber nie hinreichend fuer Stable.**

## 2. Native Navigation (nicht Havok-/Firestorm-NavMesh)

`[OGLPathfinding] Enabled=true` und `[OGLNpcNavigation] Enabled=true`
nur in der isolierten Developer-Region. Fuer Physics-gepruefte Ebenen die
separaten dokumentierten `UseVerifiedLayeredSurfaces` /
`PhysicsRaycastLayeredSurfaces` Schalter nur einsetzen, wenn die installierte
Physiksoftware gefilterte Raycasts korrekt unterstuetzt. Ohne Provider
muss der Pfad **fail-closed** bleiben; niemals auf einen erfundenen Floor
zurueckfallen.

| ID | Nachweis / Versuch | Erwartetes Ergebnis |
| --- | --- | --- |
| NAV01 | NPC-Besitzer startet eine gueltige Route auf begehbarem Terrain | Motor `MoveToTarget`, begrenzte Wegpunkte, Ankunft und Stop ohne Teleport |
| NAV02 | Eine Bruecke liegt exakt ueber begehbarem Boden | Boden-NPC darf keine Bruecken-Wegpunkte allein wegen gleicher XY-Koordinaten konsumieren |
| NAV03 | Verifizierte, begehbare Treppe/Rampe zu erhoehter Ebene | Nur physikalisch gepruefte Kanten/Portale werden durchlaufen |
| NAV04 | Unverbundene Ebenen / Wand / Luecke / gesperrte Kante | Keine erfundene Route; Anfrage scheitert sicher |
| NAV05 | Terraforming, Prim-Rez, Loeschung/Verschiebung waehrend Route | Epoch/Dirty wird erneuert; alte Route laeuft nicht unbemerkt weiter |
| NAV06 | Unberechtigter NPC-Aufrufer, NPC geloescht, Quota erreicht | Keine Bewegung, keine Erhoehung von Berechtigungen oder ungebundene Job-Zahl |
| NAV07 | `llGetStaticPath` und `llGetClosestNavPoint` auf Terrain/verifizierter Ebene | Unterstuetzte Signaturen/Fehlercodes und endliche Wegpunkte; kein stummer SL-Character-Stub |
| NAV08 | 4er-VAR-Region, Burst-Routen und mehrminuetiger Betrieb | Dokumentierte CPU-/Heap-/GC-/Tick-Budgets ohne neue relevante Regression |

**Wichtig:** Die vorhandene NPC-Route ist ein experimenteller interner Service,
kein im Viewer verfuegbares Character- oder dynamisches LSL-SDK.
Ein reproduzierbarer autorisierter Aufruf von `IOglNpcRouteService.TryNavigate`
aus dem Developer-Testharness und echte Beobachtung in Firestorm sind fuer
NAV01–NAV06 erforderlich. Ohne solchen Live-Nachweis: **FAILED / NOT RUN**.
Das Firestorm-Pathfinding-Menue bleibt erwartungsgemaess grau.

## 3. Experiences im realen Firestorm

Administrativ zwei **getrennte** API-Keys bereitstellen: einen normalen
Experience-Service-Key und ausschliesslich
`experiences:viewer:permissions` fuer den Viewer-Permissions-Proxy. Keine
Klartextwerte in Git, Chat, Logs oder diese Tabelle schreiben. In der
Developer-Region nach Backup die Konfiguration aus
`PATHFINDING_EXPERIENCES_FULL_PARITY_09310.md` verwenden:

```ini
[NexExperiencesViewer]
    Enabled = true
    ApiKey = "${Environment|NEXVERSE_EXPERIENCES_API_KEY}"
    ViewerPermissionsApiKey = "${Environment|NEXVERSE_EXPERIENCES_VIEWER_PERMISSIONS_API_KEY}"
    FirestormReadCaps = true
    FirestormPermissionCaps = true
    ScriptPendingConsent = true
    NativeExperiencePrompt = true
```

Das eigene Test-Prim kann
`tools/diagnostics/ogl_experience_firestorm_smoke.lsl` verwenden.
Eine echte Experience muss vorher bewusst angelegt und dem Script
berechtigt zugeordnet sein. Ein zweiter Bewohner darf keine Admin-Rechte haben.

| ID | Firestorm-/Server-Versuch | Erwartetes Ergebnis |
| --- | --- | --- |
| EXP01 | B bittet erstmals per Script um Berechtigung | Native Anfrage bzw. expliziter gesicherter Fallback; **keine** automatische Zustimmung |
| EXP02 | B waehlt Allow im Viewer | Persistentes `ExperiencePreferences` Allow und genau passendes `experience_permissions` |
| EXP03 | B waehlt Block bzw. Deny | Korrekte Fehlermeldung/`experience_permissions_denied`, keine Teilberechtigung |
| EXP04 | B fuehrt Forget aus, meldet sich ab und wieder an | Geloeschtes Recht bleibt geloescht, kein alter Grant |
| EXP05 | Script Reset/Remove im offenen Zustimmungsdialog | Anfrage wird abgebrochen, kein veraltetes LSL-Event |
| EXP06 | B teleportiert auf zweite Region oder loggt aus | Vorherige Experience-CAPS scheitern; keine alten Leases erneut gueltig |
| EXP07 | Zwei gleichzeitige Scripts und zwei Avatare | Deny, Timeout, Allow sind script-/avatar-isoliert |
| EXP08 | Unbefugter Avatar/HG-Gast, gesperrte Parzelle, deaktivierte Experience | Verweigerung trotz alter URLs und Script-Bindung |
| EXP09 | K/V create/read/update-CAS/delete nach Restart von Robust und Simulatoren | Korrekte Werte, Ausfall-/Backup-/Wiederherstellungsnachweis |
| EXP10 | Zwei Robust-Instanzen bzw. produktive zentralisierte Topologie | Nachweis eines einzigen autoritativen Writers bzw. transaktionalen Stores – **nicht** aufgrund JSON-Datei allein behaupten |

EXP01–EXP10 gelten erst als **PASS**, wenn HTTP-/Viewer- und LSL-Ergebnis,
Persistenz nach Re-Login, Berechtigungskontrolle sowie saubere Logs
zusammen dokumentiert sind. Die CI-Tests zu Lease-Revoke, Role-CAPS und
Timeouts ersetzen diese Ende-zu-Ende-Pruefung nicht.

## 4. Weiterhin offene allgemeine Stable-Gates

- **APP01:** Firestorm Appearance `Kein Outfit`/Current Outfit/Links
  mit Bestandsnutzer pruefen, ohne Outfits automatisch zu zerstoeren.
- **INV01:** Akzeptierte bekannte Einschraenkung: Austausch eines
  Outfit-Ordner-Thumbnails wird in Firestorm 7.2.4 mit bestehendem
  Inventarcache teils nicht aktualisiert; Cache-Neuaufbau zeigt das
  gespeicherte neue Asset. **Kein Nachweis einer Fehlerbehebung.**
- **RUNTIME:** Gegen v0.9.3.8 messen: CPU, RAM/Heap, GC, Scheduler-/Region-Ticks,
  Login, Teleport/HG, Inventar, NexBus, NV$, Watchdog und Logwarnungen.
- **RECOVERY:** Backup, Update, Restart, Rollback auf **unveraendertes**
  `v0.9.3.8`, kontrollierte Datenmigration/Restore, Secrets getrennt
  und Rollback bei nicht rueckwaertskompatibler Schemaaenderung.
- **RELEASE:** Gruener identischer Build-Commit, Maintainer-Review,
  ausgefuellte NAV-/EXP-/RUNTIME-Resultate, dokumentierte Known Limitations,
  danach erst RC1-Tag. Stable erfordert gesonderte Betreiber-Abnahme.

## 5. Ergebniserfassung

Fuer **jede** Fall-ID: Datum, Region-ID, Server-/Viewer-Build, Testverantwortliche,
`PASS` / `FAIL` / `NOT RUN`, anonymisierter Logausschnitt, beobachtetes
Viewer-/LSL-Verhalten, Reproduktionsschritte, offene Issue/PR-Nummer.
Fehlende Nachweise sind **NOT RUN**, nie pauschal PASS.

**Aktueller Stand bei Erstellung: alle Live-Operator-Fall-IDs NOT RUN.**
Fuer 0.9.3.10 wird weder ein RC1 noch ein Stable-Tag durch diese
Dokumentation automatisch freigegeben.
