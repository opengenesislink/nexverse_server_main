# NexVerse Bürgerportal – Freunde, Profil, Nachrichten, Gruppen und Display Name

Diese Schnittstellen nutzen die vorhandenen OpenSimulator-Datenspeicher
und OpenGenesisLINK-Authentifizierung. **Keine direkte SQL-Verbindung**
aus dem Stadtportal zulassen; ausschließlich OAuth2/OIDC mit PKCE S256
und die World API verwenden.

## Freundschaften

- `GET /api/v1/relationships/{principalId}` — Freundesliste,
  ausgehende/eingehende Anfragen, Rechte und sichtbarer Online-Status.
- `POST /api/v1/relationships/{principalId}/{friendId}` — Anfrage versenden
  (Status 202); wiederholte Anfragen werden mit 409 zurückgewiesen.
- `PUT /api/v1/relationships/{principalId}/{friendId}` — **nur eingehende**
  Anfrage akzeptieren; kein eigenmächtiges Akzeptieren durch Absender.
- `DELETE /api/v1/relationships/{principalId}/{friendId}` —
  Anfrage ablehnen/zurückziehen oder Freundschaft beenden.
- `PATCH /api/v1/relationships/{principalId}/{friendId}` mit
  `{"rights":1}` — Rechte einer bestätigten Freundschaft anpassen.
- `PUT /api/v1/relationships/{principalId}/blocks/{friendId}` —
  blockiert Avatar und beendet Freundschaft.

Der API-Scope ist `relationships:read` bzw. `relationships:write`.
**Ownerprüfung gilt zusätzlich**. Der Online-Status eines Freundes
wird nur gezeigt, soweit dieser die notwendigen Viewer-Freundesrechte
gewährt hat. Die Viewer-Benachrichtigung für neue Portal-Einladungen
soll im Live-Test verifiziert werden; ausstehende Angebote werden beim
Login vom bestehenden OpenSim FriendsModule erkannt.

## Direktnachrichten

`POST /api/v1/messages` mit Body:

```json
{"to_agent_id":"<UUID eines bestätigten lokalen Freundes>","message":"Hallo!"}
```

Der authentifizierte Avatar ist der **einzige** zulässige Absender.
Die Route akzeptiert ausschließlich bestätigte lokale Freundschaften,
maximal 1024 UTF-8-Bytes und benutzt die reguläre OpenSim
`HGInstantMessageService`-Zustellung. Wenn der Dienst fehlt oder die
Zustellung fehlschlägt, meldet die API einen Fehler anstatt Erfolg
zu behaupten. Offline-Zustellung hängt von der Installation und
Konfiguration des vorhandenen `OfflineIMService` ab.

**Neu:** Eine optionale, eigentümergeschützte Portal-Chat-Historie und
cursorbasierter Empfang sind implementiert. Vor dem ersten Empfang
muss der Einwohner ausdrücklich zustimmen. Die Server- und
Simulator-Konfiguration, API-Endpunkte, Datenschutzregeln und
Abnahmetests stehen in [PORTAL_IM_CHAT.md](PORTAL_IM_CHAT.md).
Es gibt weiterhin keinen öffentlichen/globalen IM-Verlauf,
keinen Hypergrid-Fremdadressen-Webchat und keine Lesebestätigung.
Audits speichern nur Empfänger-ID und Nachrichtengröße, **nie**
Nachrichteninhalt.

## Avatarprofil

- `GET /api/v1/profiles/{principalId}` — Profil einschließlich
  Anzeigename, Beschreibung, Web-URL, Reifegrad, Interessen, Picks,
  Classifieds, Sichtbarkeitsregeln und `profile_image`-Asset-UUID.
- `PATCH /api/v1/profiles/{principalId}` — eigene erlaubte Felder
  bearbeiten, z. B.:

```json
{
  "about":"Willkommen in NexVerse!",
  "web_url":"https://stadt-nexverse.de",
  "profile_image":"<bereits hochgeladene Textur-UUID>",
  "visibility":"public",
  "online_visibility":"friends",
  "groups_visibility":"public",
  "search_visibility":"public"
}
```

Private Profile benötigen ab jetzt den Eigentümer oder `admin:*`.
Die eigentlichen Profilinformationen liegen im autoritativen OpenSim-
UserProfiles-Datenspeicher. Die Profile-API ist nur aktiv, wenn
`[UserProfilesService] Enabled = true` und der Datenbankprovider
konfiguriert ist.

**Profilbild:** Die API referenziert eine **bereits vorhandene,
gültige Asset-UUID**. Ein neuer Web-Upload mit Bildvalidierung,
Kostenprüfung und eigenem Asset-Endpunkt gehört in eine separate
Umsetzungsphase; dieses Update beansprucht keine solche Funktion.

## Display Name

`PATCH /api/v1/users/{principalId}` mit
`{"display_name":"Gewünschter Anzeigename"}`.
Die bestehende Normalisierung und Self-Service-Sperre von sieben Tagen
bleiben unverändert. Das unveränderliche Login-`username` bleibt
davon getrennt.

## Gruppenmitteilungen – auch Adminbereich

- `GET /api/v1/groups` — Gruppen suchen.
- `GET /api/v1/groups/{groupId}` — Profil und Einstellungen lesen.
- `GET /api/v1/groups/{groupId}/members` — Mitgliederliste.
- `GET /api/v1/groups/{groupId}/roles` — Rollen und Powers.
- `GET /api/v1/groups/{groupId}/notices` — Mitteilungen lesen.
- `POST /api/v1/groups/{groupId}/notices` mit
  `{"from_name":"...","subject":"...","message":"..."}` —
  Mitteilung mit **bestehender Gruppenberechtigung** versenden.
- Einladungen, Bans, Rollenverwaltung und Gruppenaccounting sind
  bereits über weitere `/groups/`-Routen vorhanden.

Gruppen-Scopes `groups:read` und `groups:manage` öffnen
**nicht automatisch fremde Gruppen zur Verwaltung**; die
OpenSim-Gruppenpowers bleiben maßgeblich.
Im Stadtportal-Adminbereich sollen Änderungen fremder Gruppen nur
erfolgen, wenn dafür eine **ausdrücklich implementierte und
geprüfte** Administrationsrichtlinie besteht.

## Technische Grenzen

- Das Stadtportal-Frontend ist nicht Teil dieses GitHub-Repositories.
  Navigation, Formulare und Darstellungsansichten müssen im separaten
  Website-Projekt implementiert werden.
- Kein automatisches Live-Deployment, keine DB-Migration.
- Nach einem Update Firestorm-Live-Tests für Anfragen, Online-Status,
  blockierte Nutzer, Gruppenrechte, Profile und IM durchführen.
