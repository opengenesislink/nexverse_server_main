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
