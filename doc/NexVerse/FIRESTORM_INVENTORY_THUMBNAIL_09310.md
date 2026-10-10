# OpenGenesisLINK 0.9.3.10 – Firestorm Thumbnail/Outfit Fehlerbehebung

## Screenshot-Befund (Firestorm Release x64 7.2.4.80712)

**Fehler:** `Regionsfähigkeit "InventoryThumbnailUpload" konnte nicht abgerufen werden.`

**Getrennt sichtbarer Zustand:** `Aussehen > Outfit-Galerie > Kein Outfit` und Inventarordner `Avatar von Sleimer`. Die fehlende Thumbnail-CAP ist **kein Beweis**, dass der Avatar kein Outfit hat oder das Inventar korrupt ist.

## Umsetzung

Der Server registriert `InventoryThumbnailUpload` beim CAPS Seed. Hypergrid-Besucher duerfen damit bewusst **keine** lokalen Assets mit ihrer fremden Home-Grid-Inventardatenbank verknuepfen; solche Uploads muessen im Heimat-Grid erfolgen. Firestorm sendet zuerst eine LLSD-POST-Anfrage mit `item_id` oder `category_id` und bekommt `state=upload,uploader=<Einmal-URL>`. Anschliessend sendet der Viewer die JPEG2000-Datei an den Einmal-Uploader. Nach Besitzer-/Sitzungspruefung speichert OpenGenesisLINK eine **permanente** Texture-Asset-ID und aktualisiert das `ThumbnailID`-Feld des betroffenen Items/Outfit-Ordners. Firestorm erhaelt `state=complete,new_asset=<UUID>`.

Die neuen `thumbnailID`-Spalten und die bisher fehlende Inventar-Konvertierung werden fuer drei DB-Engines bereitgestellt:
- MariaDB/MySQL `InventoryStore` Migration v8
- PostgreSQL `InventoryStore` Migration v11
- SQLite `XInventoryStore` Migration v3

Die Zuordnung wird auch ueber die **Robust-HTTP-Inventar-Connectoren** transportiert. `FetchInventory2` und `FetchInventoryDescendents2` liefern die von Firestorm erwartete LLSD-Struktur `thumbnail: {asset_id:<UUID>}`.

## Nachbesserung: Bild nach erneuter Anmeldung verschwunden

**Betreiberbefund:** Die `InventoryThumbnailUpload`-Fehlermeldung ist behoben und
der Upload funktioniert. Das Thumbnail verschwindet jedoch nach erneutem Login.
Damit ist **INV01 nicht bestanden**.

**Nachgewiesener Codepfad:** Bestehende OpenSim-Viewer-Updates (`UpdateItem`,
`UpdateFolder`) besitzen im Legacy-Paket keine Thumbnail-UUID. Im bisherigen
Servercode wurde das fehlende Feld beim Konvertieren als `UUID.Zero` behandelt
und konnte den zuvor gespeicherten Wert wieder auf Null setzen. Der Fix
behaelt bei diesen Updates die bestehende `ThumbnailID` bei und laesst
eine explizit neue, nicht-leere Thumbnail-UUID zu.

**Wichtige Grenze:** Das Legacy-Protokoll kann "kein Feld vorhanden" und
"Vorschaubild absichtlich entfernen" nicht unterscheiden. Das Speichern einer
Null-UUID ueber dieses alte Update entfernt nun **kein** Thumbnail; fuer ein
explizites Loeschen ist ein eigener authentifizierter API-Endpunkt erforderlich.

**Neuer Regressionstest:** Reale SQLite-Datenbank, Erstellen eines Items und
eines gespeicherten Outfit-Ordners, Speichern beider Vorschaubilder, simulierte
Legacy-Updates ohne Thumbnail-UUID, erneutes Oeffnen der Datenbank ueber einen
neuen Inventardienst sowie `FetchInventory2`-LLSD mit
`thumbnail.asset_id`. Auch der Systemordner `My Outfits` wird getestet.

**Manuelle Pruefung nach Update:** Bei neuem Testthumbnail Item- bzw.
Folder-UUID festhalten und die Datenbank als Benutzer mit Leserechten pruefen:

```sql
SELECT inventoryID, thumbnailID
FROM inventoryitems
WHERE inventoryID = 'ITEM-UUID';

SELECT folderID, thumbnailID, version
FROM inventoryfolders
WHERE folderID = 'FOLDER-UUID';
```

Wenn `thumbnailID` nach dem Upload Null ist: zuerst den Schreibpfad
(Server-/Robust-Versionsgleichheit, Migrationen und Logs) untersuchen.
Wenn die UUID in der Datenbank erhalten ist, aber Firestorm das Bild
nicht zeigt: `FetchInventory2` bzw. `FetchInventoryDescendents2`,
Viewer-Inventarcache und Asset-Abfrage untersuchen. Ein Neuaufbau des
Viewer-Caches kann den Fehler eingrenzen, **ersetzt aber keinen Serverfix**.
Keine Loeschung und kein generelles Inventar-Reset.

## Live-Regression am 10.10.2026 – Cache-Neuaufbau als Workaround

Bestaetigter reproduzierbarer Ablauf mit Firestorm 7.2.4.80712:
1. Ordner `Avatar von Sleimer` erhaelt Snapshot-Vorschaubild ueber
   `InventoryThumbnailUpload`. Asset/Ordner-UUID persistieren in MariaDB.
2. Nach normalem Viewer-Neustart zeigt die Outfit-Galerie das leere
   Standard-Ordnersymbol, obwohl `inventoryfolders.thumbnailID` gesetzt bleibt.
3. Nach `Netzwerk & Dateien -> Verzeichnisse -> Inventar-Cache loeschen`
   und erneutem Firestorm-Start erscheint die erwartete Textur.
4. Ein weiterer normaler Neustart **ohne** Cache-Loeschung zeigt die
   vorhandene Textur weiterhin.
5. Ersetzt der Betreiber die Ordner-Vorschau durch einen **neuen Snapshot**,
   ist die neue UUID serverseitig gespeichert und der Ordner steigt von
   Version 22 auf 24, aber nach normalem Neustart fehlt das Bild wieder.
   Im SQL-Join steht die Parent-Version bei 6; ihr vorheriger Wert wurde
   nicht separat gemessen.
6. Ein erneuter einmaliger Inventar-Cache-Neuaufbau stellt auch den zweiten,
   neuen Snapshot in der Outfit-Galerie sichtbar wieder her.

**Bewertung:** Persistenz und erneuter Bild-/Ordner-Abruf funktionieren
nach Cache-Neuaufbau, **Cache-Aktualisierung bei Thumbnail-Austausch
bleibt FEHLGESCHLAGEN**. INV01 nicht abgenommen. Ein automatischer
Cache-Neuaufbau fuer jeden Login, Datenbank-Version-Hacks oder eine
pauschale Cache-Loeschung werden nicht als Fix eingesetzt.

Relevante Firestorm-Quellen (Bestands-Codeanalyse):
- `LLInventoryModel::loadSkeleton` uebernimmt im Login nur
  `name/folder_id/parent_id/version/type_default`; Thumbnail-ID
  stammt aus lokalem Cache, sofern Folder-Versionen uebereinstimmen.
- `BGFolderHttpHandler::processData` fuehrt `gInventory.updateCategory`
  fuer per `FetchInventoryDescendents2` gelieferte Kindordner nur aus,
  wenn `!gInventory.isCategoryComplete(id)`. Dadurch koennen
  Cache-Zustaende Aktualisierungen blockieren; der konkrete Client-Zustand
  beim Fehler ist noch nicht direkt geloggt.
- `LLFloaterSimpleSnapshot` setzt nach HTTP-`state=complete`
  `setThumbnailUUID` lokal und markiert `LLInventoryObserver::INTERNAL`.
  Live-End-to-End-Zustellung/Cache-Speicherung ist dadurch nicht bewiesen.

**Naechste zielgerichtete Diagnose:** Bei weiterem Screenshot-Update
protokollieren, ob `FetchInventoryDescendents2` den Parent-Folder
anfordert, mit welcher Version, und ob
`categories[*].thumbnail.asset_id` die aktuelle DB-UUID enthaelt.
Parallel Viewer-Cache-Version und `isCategoryComplete` im Debug-Log
kontrollieren. Erst dann entscheiden, ob serverseitig ein kompatibles
Refresh-Signal moeglich ist oder ein Viewer-Fix benoetigt wird.
`PR #128` liefert nur Upload-Readback und Regressionen,
**keinen nachgewiesenen Cache-Fix**.

## Opt-in Server-Diagnose fuer INV01: Login / Upload / Descendants (PR #128)

Die Log-Analyse des **normalen** Firestorm-Neustarts vom 10.10.2026
(15:23 MESZ) zeigt `69 categories and 252 items from cache`, danach
`Validate ... valid: 1`. Nach Cache-Loeschung wurden zuvor keine
Inventardaten aus dem lokalen Cache uebernommen und die neue
Snapshot-Vorschau erschien korrekt. Das beweist den Unterschied im
Viewer-Cache-Verhalten, aber nicht, welche Versionsnummer Firestorm
fuer den einzelnen Ordner empfaengt.

Fuer den naechsten Live-Test gibt es daher eine **standardmaessig
deaktivierte**, nur fuer **eine** Ordner-ID aktivierbare Tracefunktion.
Die Environment-Variable vor dem Start der jeweiligen Prozesse setzen:

```bash
export OGL_THUMBNAIL_TRACE_FOLDER_ID=329b83dc-f53e-167f-17c3-ce52d9af98ca
```

Beim Systemd-Dienst stattdessen `Environment=OGL_THUMBNAIL_TRACE_FOLDER_ID=...`
ueber ein lokales Drop-in konfigurieren; keinen produktiven Dienst
unbeabsichtigt stoppen. Damit die Werte ankommen, muessen **Robust und
betroffener Simulator jeweils mit der Variable starten**. Die Variable
nicht als globale Dauer-Konfiguration uebernehmen und nach dem Test
wieder entfernen. Es werden weder Sitzungs-Token noch komplette
Inventory-LLSD-Antworten in den Trace geschrieben.

Die Ereignisse sind an `[INVENTORY THUMBNAIL TRACE]` erkennbar:

- `UPLOAD_BEFORE` (Simulator): alte UUID / Ordner- und Elternversion.
- `UPLOAD_AFTER` (Simulator): nach serverseitigem Readback gespeicherte
  neue UUID / Ordner- und Elternversion.
- `LOGIN_SKELETON` (Robust/LoginService): Ordner-/Elternversion, UUID
  aus Inventardienst; das aktuell ausgelieferte Login-Skeleton
  enthaelt **kein** Thumbnail-Feld.
- `FETCH_CHILD` (Simulator): Version und `thumbnail.asset_id` des
  Zielordners, wenn sein Elternordner ueber
  `FetchInventoryDescendents2` angefordert wurde.
- `FETCH_SELF` (Simulator): ein Abruf des Zielordnerinhalts.
  **Achtung:** Dieser Self-Fetch liefert nicht zwangslaeufig
  Thumbnail-Metadaten der Kategorie selbst; dafuer ist der
  `FETCH_CHILD` beim Parent relevant.

Geordneter Test:
1. Trace fuer dieselbe bestehende Ordner-ID aktivieren und Dienste
   kontrolliert mit neuem Build starten.
2. Neues Snapshot-Vorschaubild zuweisen; `UPLOAD_BEFORE/AFTER`
   im Simulator-Protokoll sichern.
3. Viewer normal **ohne Inventar-Cache-Loeschung** neu starten;
   `LOGIN_SKELETON` aus Robust-Log sichern.
4. Outfit-Galerie oeffnen und `FETCH_CHILD` bzw. `FETCH_SELF` im
   Simulator-Protokoll sichern; notieren, ob die Vorschau erscheint.
5. **Kein** Reset, keine Datenbankmodifikation. Danach Trace abschalten.

Diagnose-Entscheidung:
- `LOGIN_SKELETON` hat keine oder alte Version/UUID: Robust-
  Inventarconnector, Login-Skeleton-Datenquelle und DB-Auslieferung pruefen.
- `LOGIN_SKELETON` aktuell, aber **kein `FETCH_CHILD`**: Firestorm
  verzichtet auf Parent-Refresh; Cache-Versionssemantik untersuchen.
- `FETCH_CHILD` sendet **alte UUID**: Simulator-/Robust-Fetch oder
  inkonsistente Inventar-Readbacks untersuchen.
- `FETCH_CHILD` sendet **aktuelle UUID**, Viewer zeigt trotzdem
  Standardicon: Viewer-`updateCategory`, Thumbnail-Zuordnung und
  lokales Inventarcache-Speichern diagnostizieren; serverseitiges
  `UpdateFolder` allein ist dann nicht die Loesung.

Die SQLite-Regression verifiziert jetzt auch das **zweite** Ersetzen
eines Outfit-Snapshots (UUID, Child-Version, Parent-Version, neuer
Inventardienst). Die Regression simuliert **keinen Firestorm-Cache**.

## Zusatzbefund vom 10.10.2026: Item funktioniert, Ordner verliert Thumbnail

Der Betreiber hat ein Inventargegenstand-Vorschaubild nach vollstaendigem
Firestorm-Neustart positiv bestaetigt. Beim Inventarordner funktioniert
der Upload, das Vorschaubild fehlt jedoch nach vollstaendigem Firestorm-Neustart.
Die Console-Meldung `[AVFACTORY]: Received texture update` ist eine Avatar-
Texture-Meldung, **kein** Thumbnail-Upload-/Persistenznachweis.

Der Upload-CAP verifiziert bei Ordnern nun vor `state=complete` ueber einen
zweiten `InventoryService.GetFolder(owner, folderID)`, ob die exakt hochgeladene
Thumbnail-Asset-UUID gespeichert und wieder abrufbar ist. Bei fehlender
Referenz: HTTP 503 und gezielte `[INVENTORY THUMBNAIL]`-Warnung;
**kein** falsches `complete`. Bei Erfolg: INFO mit Ordner-UUID,
Thumbnail-UUID, Version und Parent-ID. Die neue Pruefung beweist nur den
synchronen Inventar-Readback, **keinen** erfolgreichen Relog oder
Asset-Download. Ordner-/Item-Tests bleiben separat.

Read-only-SQL-Diagnose auf der tatsaechlich vom zentralen Robust verwendeten
Inventardatenbank (Ordner-UUID aus Inventar-API oder Serverlog einsetzen):

```sql
SELECT folderName, folderID, parentFolderID, type, version, thumbnailID
FROM inventoryfolders
WHERE folderID = 'ECHTE-ORDNER-UUID';
```

Befundmatrix:

- `thumbnailID` ist Null/leer: Schreibpfad, Robust-Build, Migration und
  Folge-Updates untersuchen; Viewer-Cache ist noch keine Erklaerung.
- `thumbnailID` ist gesetzt, aber bereits nach Relog wieder Null:
  Nachtraegliche Updates/abweichender Inventardienst ueberschreiben Daten.
- `thumbnailID` bleibt gesetzt: Elternordner-Version sowie
  `FetchInventoryDescendents2` `categories[*].thumbnail.asset_id`
  kontrollieren. Firestorm uebernimmt Kategorien aus dem Fetch und kann
  veraltete Inventarcaches verwenden. Gezielter Viewer-Cache-Neuaufbau
  ist **nur ein Diagnoseschritt**, keine Loeschung der Serverdaten.
- LLSD enthaelt die richtige UUID, aber Bild bleibt leer: Bild-Asset
  (`AssetType.Texture`, JP2/J2C) ueber den Asset-Dienst pruefen.

Die Firestorm-Login-`inventory-skeleton` dient dem anfänglichen
Ordnermodell; der untersuchte Firestorm-Code uebernimmt dort kein
`thumbnail`-Feld. Deshalb wird die Login-Antwort hier **nicht**
blind um ein solches Feld erweitert. Der Ordner-Child-Fetch liefert
die Zuordnung bereits unter `thumbnail.asset_id`.

## Sichere Rollout-Reihenfolge

1. **Vollstaendiges Backup** der Inventar-DB und Asset-Datenbank anfertigen; DB-Datenbanktyp und exakt verwendeten Inventardienst festhalten.
2. Simulatoren fuer das Wartungsfenster kontrolliert anhalten; neue Simulator-Version **nicht vor** dem Inventar-DB-Schema ausrollen, da zusaetzliche `XInventoryItem.thumbnailID`- und `XInventoryFolder.thumbnailID`-Felder DB-Spalten erwarten.
3. Zentrale **Robust-/Inventar-Service-Binaries** aktualisieren, die additive SQL-Migration durch den projektspezifischen DB-Migrationsmechanismus ausfuehren lassen. Danach die neuen `thumbnailID`-Spalten auf `inventoryitems` und `inventoryfolders` nachpruefen.
4. Alle Simulatoren auf denselben Source-Stand aktualisieren. `[ClientStack.LindenCaps] Cap_InventoryThumbnailUpload="localhost"` muss wirksam sein. Ein leeres Setting deaktiviert die Funktion. `InventoryThumbnailUploadMaxBytes=1048576` ist der Standard.
5. In Firestorm **neu einloggen** (CAPS werden pro Session ausgegeben). Einen vorhandenen Inventargegenstand oder Outfit-Ordner mit einem 256×256-Snapshot als Thumbnail versehen.
6. Im OpenGenesisLINK-Log Pruefen, dass die Zweistufen-Anfrage erfolgreich war; **Asset-Service** hat die Texture-UUID gespeichert. Danach pruefen, dass `inventoryitems.thumbnailID` bzw. `inventoryfolders.thumbnailID` nicht mehr die Null-UUID ist.
7. **Vollstaendiger Viewer-Neustart:** `FetchInventory2` bzw. `FetchInventoryDescendents2` muss das gleiche Vorschaubild unter `thumbnail.asset_id` zurueckliefern. Das ist das obligatorische End-to-End-Akzeptanzkriterium.

Beispiel fuer rein lesende MariaDB-Diagnose, **UUID ersetzen**:
```sql
SELECT folderName, type, folderID, parentFolderID, version, thumbnailID
FROM inventoryfolders
WHERE agentID = 'AVATAR-UUID'
ORDER BY folderName;

SELECT inventoryName, invType, assetType, inventoryID,
       parentFolderID, thumbnailID
FROM inventoryitems
WHERE avatarID = 'AVATAR-UUID'
ORDER BY inventoryName;
```

## Neu: sichere Outfit-Galerie-Diagnose im Buergerportal-API

Ab 0.9.3.10 Dev ist die bisher fehlende Inventar-Systemkategorie
`My Outfits` bei der Neuanlage des Residenten-Inventars vorhanden. Bei
bestehenden Avataren wird **nichts heimlich geloescht oder verschoben**.

**1. Fehlende Ordner und defekte Links zuerst anzeigen:**

```http
GET /api/v1/inventory/outfits/health
Authorization: Bearer <token-mit-inventory:read>
```

Die authentifizierte API prueft die beiden Systemordner, die Anzahl gespeicherter
Outfit-Unterordner und die ersten 256 Eintraege von `Current Outfit`. Bei
direkten Inventar-/Ordnerlinks wird das Ziel auf Existenz und Eigentuemer
geprueft. Die Rueckgabe enthaelt `findings`, beispielsweise
`my_outfits_folder_missing`, `current_outfit_broken_links` oder
`no_saved_outfits`. `changed=false` garantiert einen rein lesenden Vorgang.
Ein `no_saved_outfits` ist kein Fehler des Avatar-Aussehens, sondern kann bei
einem neuen Konto normal sein. Der 256-Link-Check hat eine
`truncated`-Markierung und meldet bei groesseren COFs **keine** Vollstaendigkeit.

**2. Nur den fehlenden Systemordner fuer einen Bestandsavatar erzeugen:**

```http
POST /api/v1/inventory/outfits/ensure-folders
Authorization: Bearer <token-mit-inventory:write>
```

Dieser explizite Aufruf laesst `Current Outfit` und alle Kleidungs-Links
unangetastet. Antwort: `created=true` (HTTP 201), wenn `My Outfits` angelegt
wurde, oder `created=false` (HTTP 200), wenn er schon existiert. Beim
zweiten Aufruf duerfen keine zusaetzlichen Ordner entstehen.

Bei explizit gewaehltem `?owner_id=<avatar-uuid>` gilt wie bei der
bestehenden Inventar-API: Fremde Konten sind ausschliesslich mit
`admin:*`-Scope zulaessig. Die beiden Firestorm-Systemordner
`Current Outfit` und `My Outfits` lassen sich ueber diese API nicht mehr
umbenennen, verschieben oder in den Papierkorb legen. Die regulare
Inventar-API liefert jetzt auch `thumbnail_id` fuer Ordner/Items.

**3. Firestorm testen:**

Nach dem Aufruf **neu anmelden**, unter `Aussehen → Outfit-Galerie` einen
neuen benannten Outfit-Eintrag speichern, den Viewer vollstaendig beenden
und erneut anmelden. Nur wenn gespeicherte Ordner, Thumbnail und angezogene
Kleidung wieder korrekt erscheinen, gilt der E2E-Test als bestanden. Es
gibt keinen automatischen Kopier- oder Relink-Vorgang fuer unvollstaendige
COFs. Wenn `Current Outfit`-Links fehlen, muss der Betreiber den
konkreten Fall einzeln pruefen.

## "Kein Outfit" – gesondert pruefen

- Im obigen Ordnerergebnis nach **Current Outfit**, **My Outfits** und den vom Benutzer gespeicherten Outfit-Ordnern suchen. Die Anzeige `Kein Outfit` bedeutet nicht zwingend, dass kein getragenes Objekt existiert.
- Die **Current Outfit Folder (COF)**-Links muessen auf tatsaechlich vorhandene Wearables/Attachments verweisen. Nicht nur die Ordnernamen pruefen, sondern die entsprechenden `parentFolderID`-Verknuepfungen und korrekte `AssetID`.
- Bei unvollstaendigen Links die Avatar-Appearance-/Wearables-Daten und ggf. vorherige Inventory-Backups untersuchen. **Keine** pauschale Loeschung, Neuerstellung oder automatische Outfit-Ersetzung fuer vorhandene Nutzer vornehmen.
- My Outfits ist bei frisch angelegten Konten moeglicherweise leer; ein Outfit muss im Firestorm tatsaechlich unter einem Namen gespeichert worden sein, damit die Galerie einen Eintrag zeigt.

## Produktionsstatus

Die Funktionen und DB-Migrationen werden im Repository bereitgestellt. Ohne echten Test auf NexVerse/Robust und Firestorm (inklusive Re-Login) kann kein produktiver Erfolg behauptet werden. Der Remote-Rechner war bei der Sitzung offline. 
