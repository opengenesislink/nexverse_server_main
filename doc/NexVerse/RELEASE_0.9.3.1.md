# NexVerse 0.9.3.1 — NEXJAST

Veröffentlicht: 2. Oktober 2026

## Status

NexVerse 0.9.3.1 schließt den NEXJAST-Meilenstein „Legacy-Bereinigung und Plattform-Grundlage“ ab. Die Definition of Done ist mit **45/45 Kriterien** erfüllt.

## Wesentliche Änderungen

- RemoteAdmin, Vivox, FreeSwitch und die obsolete IRC-Bridge wurden aus Code, Build und Konfiguration entfernt.
- Die NexVerse-native Grundlage mit `NexVerse.Core`, `NexVerse.Server.Api` und `NexVerse.RegionModules` ist etabliert.
- World API v1, OpenAPI 3.1, OAuth2/OIDC-Grundlagen, native Zugriffstokens, API-Schlüssel, Audit-Historie und Idempotenz sind vorhanden.
- NexBus, NodeAgent, NexMetrics sowie OpenTelemetry-/OTLP-Grundlagen sind integriert.
- Die API-Dokumentation unter `/api/v1/docs` ist für die sichtbare Bedienoberfläche auf Deutsch umgestellt.
- Einwohner-/Aktivitäts-/Hypergrid-Statistiken sind über den geschützten Endpunkt `/api/v1/statistics/summary` verfügbar.
- Firestorm-kompatibler LLLogin und reale Simulator-Platzierung werden durch Runtime-CI geprüft.
- Hypergrid HomeAgent → Gatekeeper Login ist end-to-end durch Runtime-CI verifiziert.
- LSL XML-RPC RemoteData wird mit laufender Region und persistiertem Script end-to-end geprüft.
- SQLite-Reader- und HG-Travel-Lock-/Cleanup-Probleme wurden korrigiert.

## Laufzeitversion

Die Laufzeit meldet:

```text
NexVerse 0.9.3.1
```

Der Build-Flavour ist `Release`.

## Kompatibilität

Der historische Quellstand bleibt OpenSimulator 0.9.3.0 („Nessie“). Diese Referenz ist ausschließlich Provenienz und bestimmt nicht die NexVerse-Produktversion.

## Nächster Meilenstein

Der nächste geplante Produkt-Meilenstein ist **NexVerse 0.9.3.2**. Er wird nicht durch diesen Release-Commit automatisch als aktive Entwicklungsline gestartet.
