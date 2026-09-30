# NexVerse HG deployment configuration

This directory contains the first production-oriented NexVerse configuration baseline.

## Robust / Hypergrid

- Public host: `hg.stadt-nexverse.de`
- Public listener: TCP `80`
- Public URL: `http://hg.stadt-nexverse.de/`
- Private services port: TCP `8003`
- Database backend: MariaDB through `OpenSim.Data.MySQL.dll`
- Database host: `localhost`
- Database/user: `nexverse-server`
- Password placeholder: `<change-me>`
- Asset backend: FSAssets
- FSAssets data: `bin/fsassets/data`
- FSAssets spool: `bin/fsassets/tmp`

The MariaDB password must be replaced locally before starting Robust. Do not commit the real password.

## Simulator

- Public hostname: `mainland.stadt-nexverse.de`
- Simulator HTTP listener: TCP `9000`
- Example first region UDP port: `9000`
- Grid architecture: Hypergrid
- Central asset service: Robust FSAssets
- Simulator cache: FlotsamAssetCache
- Region-state storage remains the baseline SQLite configuration until a separate simulator database policy is selected.

The simulator must be able to reach `hg.stadt-nexverse.de:8003`. Restrict TCP 8003 at the firewall to trusted simulator hosts/IPs; it is not intended as a public Hypergrid endpoint.

## GridInfo

Viewer/GridInfo endpoints identify NexVerse and use `http://stadt-nexverse.de/` for the public portal links. Login and Gatekeeper are served by `http://hg.stadt-nexverse.de/`.

## Linux port 80

TCP port 80 is a privileged port on Linux. If Robust runs as a non-root systemd user, grant only the bind capability (for example with systemd `AmbientCapabilities=CAP_NET_BIND_SERVICE`) instead of running the whole service as root.
