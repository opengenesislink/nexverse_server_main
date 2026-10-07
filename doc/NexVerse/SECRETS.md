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

The active HG configuration enables `[NexBus] Enabled = true`,
`[NexVerseNodeAgent] Enabled = true` and `[NexExperiencesViewer] Enabled = true`.
All three must have secrets and peer endpoints **before** their respective processes start.

1. On the Robust/simulator host, in the repository root, run
   `python3 tools/admin/activate_experiences_nexbus.py prepare-nexbus --peer-url http://127.0.0.1:80/internal/nexbus/v1/events --peers http://127.0.0.1:9000/internal/nexbus/v1/events`
   **only if both processes are on this host** and those are their real HTTP listener ports.
   Replace these examples with verified HTTPS or trusted private endpoints. For multiple
   simulator nodes, specify each simulator's inbound URL in `--peers`, separated by commas.
2. Fetch/build the latest server version containing the `experiences:script` machine-scope fix,
   then restart Robust after the common NexBus key has been prepared.
3. Run `python3 tools/admin/activate_experiences_nexbus.py register-experiences`
   and authenticate using the local NexVerse administrator's `Vorname.Nachname` and password.
   The resulting dedicated machine API key is written to the protected env file, never stdout.
4. Run `python3 tools/admin/activate_experiences_nexbus.py check` and restart the simulator.
   For remote simulator nodes, provision the **same** HMAC key and the dedicated Experience
   key on their protected hosts before enabling or restarting the modules.

The CLI uses `/etc/nexverse/nexverse.env` unless `NEXVERSE_ENV_FILE` is set.
Secrets in live process environments take precedence. It never changes existing economy keys.
An outgoing NexBus heartbeat can only prove the simulator-to-Robust direction; test
Robust-to-simulator delivery separately, including the inbound URLs in `NEXVERSE_NEXBUS_PEERS`.
Do not expose `/internal/nexbus/v1/events` publicly without authenticated HMAC **and** TLS/firewall controls.
