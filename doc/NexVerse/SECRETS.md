# NexVerse Runtime Secrets

NexVerse runtime secrets must not be committed to the repository.

The active grid configuration uses OpenSim's existing environment-to-config merge. Values declared in the `[Environment]` section are loaded from process environment variables before configuration key expansion.

## Required production variables

For the current grid configuration:

- `NEXVERSE_DB_PASSWORD` — MariaDB password used by simulator and Robust services.
- `NEXVERSE_ROBUST_CERT_PASSWORD` — password for the Robust TLS certificate container when that certificate is enabled.

Example systemd override:

```ini
[Service]
EnvironmentFile=/etc/nexverse/nexverse.env
```

Example local environment file structure:

```text
NEXVERSE_DB_PASSWORD=<local-secret>
NEXVERSE_ROBUST_CERT_PASSWORD=<local-secret>
```

The environment file should be owned by the service administrator, readable only by the service account/root as appropriate, and must not live inside the Git working tree.

## Repository guard

`tools/ci/verify_no_plaintext_runtime_secrets.py` checks tracked active `.ini` runtime configuration and fails CI when a database password or selected runtime secret is committed as plaintext.

If a secret has ever been committed, removing it from the current tree is not sufficient: rotate the credential because older Git objects may still contain the previous value.
