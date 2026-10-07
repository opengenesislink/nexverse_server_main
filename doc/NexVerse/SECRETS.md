# NexVerse Runtime Secrets

NexVerse runtime secrets must not be committed to the repository.

The active grid configuration declares required values in the `[Environment]` section. Before normal configuration expansion, NexVerse resolves these values in this order:

1. an already-set process environment variable;
2. an explicitly selected file from `NEXVERSE_ENV_FILE`;
3. `/etc/nexverse/nexverse.env`;
4. `bin/nexverse.local.env` next to the runtime assemblies.

The first available value wins. Values are read directly by .NET; shell expansion is not used. Passwords containing characters such as `!`, `$`, `#` or spaces therefore do not need shell escaping when stored as the value in one of these files. Single or double outer quotes are accepted and removed.

## Required production variables

For the current grid configuration:

- `NEXVERSE_DB_PASSWORD` — MariaDB password used by simulator and Robust services.
- `NEXVERSE_ROBUST_CERT_PASSWORD` — password for the Robust TLS certificate container when that certificate is enabled.
- `NEXVERSE_NATIVE_TOKEN_SIGNING_KEY` — HMAC signing key for NexVerse native access tokens; generate at least 32 random bytes (for example `openssl rand -hex 64`).
- `NEXVERSE_NEXBUS_PEERS` — comma-separated signed NexBus endpoints of simulator nodes, used by Robust for bidirectional delivery. Restrict access with private networking/TLS.

Simulator service variables are also declared in `bin/config-include/GridCommon.ini` so Nini can resolve every `${Environment|...}` expression during startup:

- `NEXVERSE_ECONOMY_API_KEY` — required only when `[NexEconomyViewer] Enabled = true`; use a restricted machine key with `economy:read` and `economy:transfer`.
- `NEXVERSE_EXPERIENCES_API_KEY` — required when `[NexExperiencesViewer] Enabled = true`; use a restricted machine key with only `experiences:script`.
- `NEXVERSE_NEXBUS_SHARED_KEY` — identical HMAC key on Robust and every simulator, at least 32 UTF-8 bytes (generate once with `secrets.token_urlsafe(48)`; do not rotate per node).
- `NEXVERSE_NEXBUS_PEER_URL` — Simulator-to-Robust signed NexBus endpoint. Use HTTPS or loopback; public plaintext HTTP is prohibited.
- `NEXVERSE_OFFLINE_IM_SMTP_PASSWORD` — SMTP password used by `[OfflineIMEmail]` when authenticated SMTP is configured. It may remain empty when a trusted local relay is used.
- `NEXVERSE_IM_RELAY_SIGNING_KEY` — shared HMAC secret used by simulators and Robust to create/validate 5-day offline-IM email reply addresses. Use at least 32 random bytes.
- `NEXVERSE_IM_RELAY_IMAP_PASSWORD` — IMAP password for the KeyHelp catch-all mailbox `relay@im.stadt-nexverse.de`.

The declarations themselves stay empty in the tracked INI. When the corresponding module is disabled, the empty declaration is sufficient for configuration expansion. When the module is enabled, provide the real value through a protected runtime secret source.

## Recommended production setup

Create the directory and file once:

```bash
sudo install -d -m 700 /etc/nexverse
sudo nano /etc/nexverse/nexverse.env
```

File format:

```text
NEXVERSE_DB_PASSWORD=<local-secret>
NEXVERSE_ROBUST_CERT_PASSWORD=<local-secret>
NEXVERSE_NATIVE_TOKEN_SIGNING_KEY=<local-secret>
NEXVERSE_OFFLINE_IM_SMTP_PASSWORD=<smtp-secret>
```

Then restrict access:

```bash
sudo chmod 600 /etc/nexverse/nexverse.env
```

No systemd `EnvironmentFile=` directive is required for NexVerse to read this file, although an existing systemd environment configuration continues to work and has higher priority.

## Runtime-local alternative

For a single installation you may instead create:

```text
bin/nexverse.local.env
```

This file is ignored by Git and survives normal `git pull` operations because it is not tracked.

## Custom location

Set:

```text
NEXVERSE_ENV_FILE=/path/to/private/nexverse.env
```

The explicitly selected file is checked before the standard `/etc/nexverse/nexverse.env` location.

## Repository guard

`tools/ci/verify_no_plaintext_runtime_secrets.py` checks tracked active `.ini` runtime configuration and fails CI when a database password or selected runtime secret is committed as plaintext.

If a secret has ever been committed, removing it from the current tree is not sufficient: rotate the credential because older Git objects may still contain the previous value.

## Production activation: Experiences and NexBus

Die aktiven HG-Profile setzen `[NexBus] Enabled = true`, `[NexVerseNodeAgent] Enabled = true` und `[NexExperiencesViewer] Enabled = true`. **Vor dem Neustart müssen daher alle Secrets und Peers eingerichtet sein.** Ein fehlender Experiences-Key verhindert den Simulatorstart; ein ungültiger NexBus-Schlüssel deaktiviert NodeAgent.

1. Aktualisiere und baue zuerst Robust inklusive des `experiences:script`-Maschinenschlüssels aus PR #104. Du kannst Robust anschließend **ohne NexBus-SharedKey** starten: Der Transport fällt für die Ersteinrichtung vorübergehend auf den lokalen EventBus zurück, damit die World-API-Oberfläche erreichbar ist. Nach der Secret-Hinterlegung Robust nochmals neu starten, um den verteilten Transport tatsächlich zu aktivieren.
2. Öffne `https://world.stadt-nexverse.de/api/v1/docs` und wähle **Admin-Anmeldung**. Die separate Anmeldung `POST /api/v1/auth/admin/session` akzeptiert ausschließlich lokale Benutzer mit `UserLevel >= 200` und `admin:*`. Das normale Einwohner-Login bleibt getrennt.
3. Nach der Anmeldung unter **Schlüssel & Einrichtung** auswählen: **Experiences** und **NexBus**. **Economy** nur markieren, wenn kein gültiger Economy-Key vorhanden ist.
4. Gib verifizierte NexBus-URLs ein: `NEXVERSE_NEXBUS_PEER_URL` für Simulator → Robust, `NEXVERSE_NEXBUS_PEERS` für Robust → Simulator(en), kommagetrennt. Jede URL muss `/internal/nexbus/v1/events` verwenden. Nur HTTPS oder HTTP über localhost/Loopback wird akzeptiert. Firewall, Proxy und Erreichbarkeit separat prüfen.
5. Mit **Ausgewählte Schlüssel erstellen** registriert die World API einmalig die gewünschten Maschinen-Schlüssel. Der NexBus-HMAC-Key wird sicher im Browser erzeugt und niemals an den World-API-Schlüssel-Endpunkt geschickt. Anschließend den ausgegebenen `NAME=WERT`-Block kopieren und **nur die betreffenden Zeilen** in `/etc/nexverse/nexverse.env` ergänzen bzw. ersetzen. Vorhandene Datenbank-, Mail- und Economy-Secrets erhalten.
6. Der NexBus-SharedKey muss auf Robust und allen Simulatoren **identisch** sein. Experiences-/Economy-API-Keys gehören nur auf die Simulator-Hosts, die sie benötigen. `chmod 600 /etc/nexverse/nexverse.env` sowie restriktive Verzeichnisrechte setzen.
7. Robust und danach die Simulatoren neu starten. Heartbeats mit `GET /api/v1/nodes` kontrollieren und beide signierten NexBus-Richtungen sowie die Experiences-Skript-API testen.

Die Admin-Sitzung bleibt nur im Speicher des geöffneten Browsers. Die Browseroberfläche schreibt keine Dateien auf den Server, speichert keine Schlüssel in localStorage/Cookies und zeigt registrierte API-Secrets **nur unmittelbar nach ihrer Ausstellung**. Ein verloren gegangener API-Key muss erneut erstellt und der alte Schlüssel über die autorisierte API-Verwaltung deaktiviert werden. Ein Abmelden löscht angezeigte Secrets aus der Oberfläche.

**Wichtig:** Der standardmäßige Secret-Pfad lautet `/etc/nexverse/nexverse.env`, nicht `/etc/nexverse/nexverse.ini`. Die letztere Datei kann nur über ein entsprechend gesetztes `NEXVERSE_ENV_FILE` verwendet werden, weiterhin im Format `NAME=WERT` (keine INI-Sektionen). Bereits im Prozess gesetzte Umgebungsvariablen haben Vorrang.

NexBus-HMAC schützt die Authentizität, **nicht** die Vertraulichkeit des Netzwerkverkehrs. Den Endpoint über private Netze/TLS schützen; `ManagedRegionCommands=false` beibehalten, bis die Remote-Befehlsausführung explizit und sicher eingerichtet ist.
