# OpenGenesisLINK – Bidirektionaler IM-Webchat im Bürgerportal

**Implementiert im Serverbranch `feature/citizen-im-inbox-history` (nach Merge in `main`); keine automatische Produktivfreigabe.**  
Ziel: Inworld-IM aus Firestorm empfängt der Avatar im Bürgerportal und umgekehrt. Auch Portal↔Portal-Nachrichten werden über OpenSims bestehende IM-Zustellung versandt.

## Bestehendes IM-System wird nicht ersetzt

- Der reguläre `HGInstantMessageService` und der Viewer-Chat bleiben maßgeblich.
- `HGMessageTransferModule` meldet **erfolgreich angenommene** direkte Avatar-IMs per internem HMAC-Transport an Robust, unabhängig von der Region.
- Portalnachrichten werden erst dem OpenSim-IM-Dienst übergeben und anschließend im opt-in Posteingang archiviert. Kein Rückschreiben oder Manipulieren der OpenSim-OfflineIM-Tabellen.
- Direktnachrichten sind **nur zwischen bestätigten lokalen Freunden** über die Web-API zulässig. Andere IM-Typen (Gruppen, Objekte, Inventory Offers) werden nicht ins Portalarchiv übernommen.
- `202 Accepted` bedeutet vom Server akzeptiert, nicht „gelesen“. Eine Firestorm-Lesebestätigung existiert nicht.
- Neues Verhalten setzt sowohl Robust- als auch Simulator-Konfiguration voraus; ohne Aktivierung bleibt die bisherige Web-IM-Versandfunktion erhalten.

## HTTP-Endpunkte

Basis: konfigurierte World API, bei NexVerse voraussichtlich `https://hg.stadt-nexverse.de/api/v1`. **Auf dem tatsächlich genutzten Host prüfen.**

| Methode | Pfad | Beschreibung |
|---|---|---|
| POST | `/messages` | Bisheriger authentifizierter Nachrichtenversand an bestätigten lokalen Freund |
| GET | `/messages/settings` | Eigene Opt-in-Einstellung lesen |
| PATCH | `/messages/settings` | Opt-in aktivieren/deaktivieren (false löscht gespeicherten Verlauf) |
| GET | `/messages` | Eigene Konversationsübersicht |
| GET | `/messages/events?after=0&limit=50` | Neue **eigene** Nachrichten, chronologisch nach Cursor |
| GET | `/messages/with/{peerId}?after=0&limit=50` | Eigene Konversation mit einem Avatar |
| DELETE | `/messages/history` | Eigene gespeicherte Portalhistorie löschen |

Alle Endpunkte verlangen ein OAuth-Bearer-Token des betroffenen lokalen Einwohners; GET benötigt `relationships:read`, PATCH/POST/DELETE `relationships:write`. Die Avatar-ID wird aus dem Token bestimmt. Selbst `admin:*` erlaubt hier **kein** Ausgeben als fremder Avatar. Browser dürfen **niemals** den geheimen internen HMAC-Endpunkt verwenden.

### Opt-in vor der ersten Unterhaltung

```http
PATCH /api/v1/messages/settings
Authorization: Bearer <authentifiziertes Einwohner-Token>
Content-Type: application/json

{"enabled":true}
```

Standard ist `false`. Erst nach ausdrücklicher Aktivierung werden **neue** Chatnachrichten für den Einwohner erfasst. Es gibt keinen rückwirkenden Zugriff auf zuvor übertragene IMs.

Wenn der Nutzer den Chat ausschaltet, wird seine **gesamte Portalhistorie automatisch gelöscht**. Zusätzlich bietet der Menüpunkt „Chat → Einstellungen“ eine Schaltfläche „Nachrichtenverlauf löschen“ (`DELETE /messages/history`). Das löscht **nicht** bereits in Firestorm/OfflineIM oder bei anderen Teilnehmern gespeicherte Nachrichten.

### Senden

```http
POST /api/v1/messages
Content-Type: application/json
Authorization: Bearer <Token>

{"to_agent_id":"<UUID eines bestätigten Freundes>","message":"Hallo aus dem Portal!"}
```

Maximal 1024 UTF-8-Bytes; der Server übernimmt Avatar-ID und Anzeigenamen aus dem authentifizierten Konto. Das Ergebnis enthält `status: "accepted"`, `event_id`, `archived`, `to_agent_id` und `correlation_id`. `archived=false` bedeutet **nicht**, dass die IM gescheitert ist; mindestens ein zuständiges Portalarchiv wurde nicht bestätigt.

### Empfang und Verlauf

```http
GET /api/v1/messages/events?after=0&limit=50
Authorization: Bearer <Token>
```

Beispielstruktur:

```json
{
  "messages": [{
    "seq": 177,
    "event_id": "11111111-2222-3333-4444-555555555555",
    "from_agent_id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
    "to_agent_id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
    "peer_id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
    "sender_name": "Beispiel Avatar",
    "message": "Hallo zurück!",
    "source": "viewer",
    "accepted_at": "2026-10-09T12:00:00Z"
  }],
  "count": 1,
  "next_cursor": 177,
  "poll_after_seconds": 5
}
```

Das Stadtportal speichert den `next_cursor` **nur für das eigene eingeloggte Konto** und fragt ihn etwa alle **5 Sekunden** erneut ab. Bei Fehlern Exponential Backoff, bei Logout Polling stoppen und den gespeicherten Token/Cursor löschen. Die Nachrichtenansicht niemals über die neue Avatar-UUID eines anderen Kontos weiterverwenden.

Mit `GET /messages/with/{peerId}` erhält man die betreffende Unterhaltung. `GET /messages` listet gespeicherte Gesprächspartner mit letzter Nachrichtenzeit (keine globalen Nachrichtenlisten). `after` ist ein monoton steigender Sequenzcursor, kein Zeitstempel; `limit` 1–100.

## Robust-Konfiguration

Die Beispielsektionen liegen in `bin/Robust.HG.ini.example` und `bin/Robust.ini.example`:

```ini
[NexPortalIM]
    Enabled = true
    StorePath = "data/nexverse-portal-im.sqlite"
    RetentionDays = 30
    SharedSecret = "${Environment|NEX_PORTAL_IM_SECRET}"
```

Generiere **einen neuen zufälligen Wert mit mindestens 32 Bytes** als Umgebungsvariable `NEX_PORTAL_IM_SECRET` und verwende **denselben** Wert in Robust und allen beteiligten Simulatorprozessen. Nicht mit OAuth-, OIDC- oder NexBus-Schlüsseln gleichsetzen und den Wert weder committen noch protokollieren. Der Datenbankpfad muss mit restriktiven Dateirechten für den Robust-Prozess beschreibbar sein.

Der separate SQLite-Store ist allein für den Portalchat. Er führt keine Migrationen an den bestehenden OpenSim-Nutzerdatenbanken durch. Datensicherung und Löschrichtlinie separat festlegen.

## OpenSim-Simulator-Konfiguration

In `bin/OpenSim.ini.example`:

```ini
[NexPortalIM]
    Enabled = true
    IngestUrl = "http://127.0.0.1/internal/nexportal/im/v1"
    SharedSecret = "${Environment|NEX_PORTAL_IM_SECRET}"
```

`http://127.0.0.1` ist nur bei Robust **auf demselben Host** zulässig. Bei getrennten Hosts ausschließlich geschütztes HTTPS verwenden und den Ingest-Pfad durch Firewall/Reverse Proxy auf die Simulatoren beschränken. Intern verlangt der Endpoint `X-NexPortal-IM-Time` und `X-NexPortal-IM-Signature`; Signaturen werden über die **rohen Nutzdaten** geprüft, mit engem Zeitfenster und idempotenter Ereignis-ID. Portal-Browser benötigen und erhalten dieses Secret niemals.

**Wichtig:** Aktivierung erfolgt in Robust **und jedem Simulator**, dessen Viewer-IMs im Portal erscheinen sollen. Ein Update auf GitHub ändert keine laufende INI.

## Sicherheit und Datenschutz

- Nur aktivierte Empfänger/Absender besitzen einen eigenen Nachrichtenbestand. Keine nachträgliche Übernahme alter Viewer-Nachrichten.
- Ein Viewer-Absender ohne Portal-Opt-in kann auf eine IM an einen Portal-Empfänger antworten: Nur dessen bereits aktivierter Posteingang enthält die neue Nachricht.
- Der Store trennt Daten pro Eigentümer, nutzt Parameterbindung und dedupliziert signierte Simulatorereignisse. Standardaufbewahrung 30 Tage (konfigurierbar 7–365), maximale 10.000 Einträge je Eigentümer.
- Nachrichtentext wird **nicht** in Audit-Protokollen erfasst. Lokale Dateisystemberechtigungen/verschlüsselte Datenträger und Zugriffskontrolle des Portalservers bleiben erforderlich.
- `POST /messages` hat zusätzlich eine Begrenzung von 30 Nachrichten pro Minute je Avatar. Für HTTP gilt weiterhin der zentrale World-API-Rate-Limiter.
- Bei Verbindungsproblemen übergibt der Simulator die IM trotzdem an Firestorm. Das optionale Webarchiv ist **best effort** mit drei kurzen Versuchen, kein garantierter Event-Stream. Eine nicht archivierte IM darf nicht als archiviert angezeigt werden.
- Die UI darf **niemals** behaupten, dass eine Nachricht gelesen wurde. `accepted_at` entspricht der serverseitigen Aufnahmezeit.
- Für Nutzerrecht auf Löschung in Portal eigenen `DELETE /messages/history` anbieten; Konto-Löschworkflow zusätzlich überprüfen (nicht als automatisch integriert behaupten).

## Abnahmetests vor Portal-Freigabe

1. Opt-in für Avatar A über das Stadtportal aktivieren; Avatar B bleibt in Firestorm.
2. A sendet aus Portal an B in anderer Region/Simulator: B empfängt echte Inworld-IM.
3. B antwortet in Firestorm: A sieht neue `source: "viewer"` Nachricht beim nächsten Poll (`GET /messages/events`).
4. Beide Teilnehmer melden sich im Portal an und optieren ein: Web→Web-Kommunikation und Gesprächsverlauf prüfen.
5. Zwei Nutzer außerhalb der Freundschaft dürfen keine Portal-IMs senden; auch mit einem fremden Bearer dürfen sie keine Inbox lesen.
6. Opt-out von A löscht dessen Verlauf; B behält nur seinen eigenen Bestand, falls B weiter optiert ist.
7. Falsche HMAC-Signatur/alter Timestamp muss auf dem internen Ingest-Pfad zu 401 führen.
8. Kein Webportal-Schlüssel erscheint in API-Antworten, Browser-JavaScript, Logs oder HTML.
9. Ausfall des Ingest-Dienstes: Firestorm-Nachricht funktioniert weiter; Portal meldet keine falsche Zustellung/Archivierung.
10. Aufbewahrung, Neustart-Persistenz, Lastbegrenzung und Kontolöschung im Grid testen.

## Portalimplementierung

Für die Stadtportal-Oberfläche eine Karte-/Tab-Lösung „Freunde“ und „Nachrichten“ anbieten, mit Opt-in-Einwilligungsdialog und personenbezogenem Cursor-Polling. Die Weltkarte `GET /api/v1/world-map/view` bleibt unabhängig. Der Browser benötigt **keine direkte SQL-Verbindung**, kein Administrations-Token und kein serverseitiges HMAC-Secret.

**Status:** Code in GitHub ist kein Nachweis des produktiven Live-Verhaltens. Vor Werbung als vollständigen Live-Chat alle oben genannten Abnahmetests durchführen.
