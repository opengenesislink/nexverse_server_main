# OpenGenesisLINK 0.9.3.10 – Firestorm Thumbnail/Outfit Fehlerbehebung

## Screenshot-Befund (Firestorm Release x64 7.2.4.80712)

**Fehler:** `Regionsfähigkeit "InventoryThumbnailUpload" konnte nicht abgerufen werden.`

**Getrennt sichtbarer Zustand:** `Aussehen > Outfit-Galerie > Kein Outfit` und Inventarordner `Avatar von Sleimer`. Die fehlende Thumbnail-CAP ist **kein Beweis**, dass der Avatar kein Outfit hat oder das Inventar korrupt ist.

## Umsetzung

Der Server registriert `InventoryThumbnailUpload` beim CAPS Seed. Firestorm sendet zuerst eine LLSD-POST-Anfrage mit `item_id` oder `category_id` und bekommt `state=upload,uploader=<Einmal-URL>`. Anschliessend sendet der Viewer die JPEG2000-Datei an den Einmal-Uploader. Nach Besitzer-/Sitzungspruefung speichert OpenGenesisLINK eine **permanente** Texture-Asset-ID und aktualisiert das `ThumbnailID`-Feld des betroffenen Items/Outfit-Ordners. Firestorm erhaelt `state=complete,new_asset=<UUID>`.

Die neuen `thumbnailID`-Spalten und die bisher fehlende Inventar-Konvertierung werden fuer drei DB-Engines bereitgestellt:
- MariaDB/MySQL `InventoryStore` Migration v8
- PostgreSQL `InventoryStore` Migration v11
- SQLite `XInventoryStore` Migration v3

Die Zuordnung wird auch ueber die **Robust-HTTP-Inventar-Connectoren** transportiert. `FetchInventory2` und `FetchInventoryDescendents2` liefern die von Firestorm erwartete LLSD-Struktur `thumbnail: {asset_id:<UUID>}`.

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

## "Kein Outfit" – gesondert pruefen

- Im obigen Ordnerergebnis nach **Current Outfit**, **My Outfits** und den vom Benutzer gespeicherten Outfit-Ordnern suchen. Die Anzeige `Kein Outfit` bedeutet nicht zwingend, dass kein getragenes Objekt existiert.
- Die **Current Outfit Folder (COF)**-Links muessen auf tatsaechlich vorhandene Wearables/Attachments verweisen. Nicht nur die Ordnernamen pruefen, sondern die entsprechenden `parentFolderID`-Verknuepfungen und korrekte `AssetID`.
- Bei unvollstaendigen Links die Avatar-Appearance-/Wearables-Daten und ggf. vorherige Inventory-Backups untersuchen. **Keine** pauschale Loeschung, Neuerstellung oder automatische Outfit-Ersetzung fuer vorhandene Nutzer vornehmen.
- My Outfits ist bei frisch angelegten Konten moeglicherweise leer; ein Outfit muss im Firestorm tatsaechlich unter einem Namen gespeichert worden sein, damit die Galerie einen Eintrag zeigt.

## Produktionsstatus

Die Funktionen und DB-Migrationen werden im Repository bereitgestellt. Ohne echten Test auf NexVerse/Robust und Firestorm (inklusive Re-Login) kann kein produktiver Erfolg behauptet werden. Der Remote-Rechner war bei der Sitzung offline. 
