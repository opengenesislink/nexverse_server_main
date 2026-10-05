# NV$ Ledger-Grundlage

OpenGenesisLINK baut die NexVerse-Wirtschaft ab 0.9.3.6 auf einem unveränderlichen Double-Entry-Ledger auf. Diese Grundlage ist bewusst von Viewer-Protokollen, Web-APIs und dem historischen `IMoneyModule` getrennt.

## Währung

- Anzeigename/Symbol: `NV$`
- interner Code: `NVD`
- initiale Untereinheiten: `0`

NV$ ist ausschließlich virtuelle NexVerse-Währung. Der interne Code `NVD` ist kein ISO-4217-Versprechen und darf nicht als reale Fiat-Währung oder echtes Bankguthaben dargestellt werden.

## Double-Entry-Invariante

Jede Buchung besteht aus mindestens zwei Postings auf mindestens zwei Ledger-Konten. Alle Beträge sind positive Ganzzahlen. Die Summe der Debit-Postings muss exakt der Summe der Credit-Postings entsprechen.

Transaktionen und Postings sind nach dem Anhängen unveränderlich. Korrekturen werden später ausschließlich durch neue Gegen-/Korrekturbuchungen vorgenommen; vorhandene Buchungen werden nicht überschrieben.

## Kontoklassen

Der Kern kennt folgende Klassen:

- Resident;
- Group;
- Business;
- Estate;
- ObjectMerchantEndpoint;
- System;
- Escrow.

Jedes Konto besitzt zusätzlich eine explizite Normal-Seite (`Debit` oder `Credit`). Dadurch muss der Ledger keine fehlerhafte Annahme treffen, dass jede System- oder Escrow-Art dieselbe Bilanzlogik hat.

Ein positiver von `GetBalance()` gelieferter Saldo bedeutet: Saldo auf der für dieses konkrete Konto definierten Normal-Seite.

## Idempotenz und Konflikte

Die `TransactionId` ist die unveränderliche Buchungsidentität.

- dieselbe ID mit exakt demselben Inhalt: `Duplicate`, keine zweite Buchung;
- dieselbe ID mit abweichendem Inhalt: Konflikt;
- eine bereits global verwendete `PostingId`: Konflikt.

Diese Regeln sind erforderlich, damit API-, Viewer- oder NexBus-Retries später keine Doppelbuchungen erzeugen.

## Speichergrenze

`INexLedgerStore` ist die Persistenzgrenze. `InMemoryNexLedgerStore` bleibt ausschließlich Referenzimplementierung und Regressionstest-Backend.

Mit `NexLedgerSqlStore` existiert jetzt zusätzlich ein dauerhafter, providerneutraler ADO.NET-Store. Er verwendet ein versioniertes Schema mit getrennten Tabellen für Konten, Transaktionsköpfe und Postings. Das Journal bleibt append-only; eine mutable Balance-Spalte wird absichtlich nicht gespeichert. `GetBalance()` berechnet den Saldo aus den unveränderlichen Postings und der Normal-Seite des Kontos.

Die SQL-Buchung läuft in einer seriellen Datenbanktransaktion. Transaktions-ID, Posting-IDs und Kontoreferenzen werden durch Unique-/Primary-Key-Regeln zusätzlich auf Datenbankebene abgesichert. SQLite aktiviert pro Verbindung Foreign Keys. Der CI-Test legt eine reale SQLite-Datei an, migriert Schema v1, bucht NV$, öffnet den Store erneut und prüft Salden, Historie, Idempotenz und abgewiesene Buchungen.

Die Runtime-Treiber MySQL/MariaDB, PostgreSQL und SQLite werden nicht in den Ledger-Kern eingebaut. Der Store erhält eine `Func<DbConnection>`; dadurch bleibt der Kern providerneutral. `NexLedgerSqlRuntime` löst die vorhandenen OpenSim-`StorageProvider`-Bezeichnungen auf die bereits mitgelieferten ADO.NET-Treiber auf und erzeugt die Verbindung ausschließlich per Reflection. Der SQLite-CI-Test benutzt genau diesen Resolver.

`NexEconomyConnector` bindet diesen Store jetzt als eigenen Robust-Service ein. `[NexEconomy]` ist standardmäßig deaktiviert. Bei Aktivierung erben leere `StorageProvider`-/`ConnectionString`-Werte die autoritative `[DatabaseService]`-Konfiguration; ein expliziter Override ist nur für eine bewusst getrennte Ledger-Datenbank vorgesehen. Null-Storage wird für NV$ abgelehnt. Der Connector registriert keine HTTP-Routen und keine Viewer-Geldschnittstelle. Ein eigener Robust-CI-Smoke aktiviert den Connector mit temporärem SQLite, prüft Schema v2 einschließlich Kontostatus-/Eventtabellen und bestätigt ein leeres Journal.

## Kontolebenszyklus und Policy

Kontostatus wird bewusst außerhalb des unveränderlichen Buchungsjournals geführt. Schema v2 ergänzt `ogl_ledger_account_state` und die append-only Historie `ogl_ledger_account_events`.

Status:

- `Active`: normale Transfers erlaubt;
- `Locked`: normale Transfers gesperrt, administrative Korrektur/Reversal bleibt möglich;
- `Closed`: terminal; Wiedereröffnung ist nicht erlaubt und Schließung erfordert Saldo 0.

`NexEconomyService` ist die verbindliche Policy-Grenze für produktive Geldbewegungen. Der Service verhindert Überziehungen von Credit-normalen Wallets, erzwingt aktive Transferkonten, verlangt bei administrativen Korrekturen ein System-Gegenkonto und erzeugt Reversals ausschließlich als neue, deterministisch idempotente Gegenbuchungen. Vorhandene Journalzeilen werden niemals geändert oder gelöscht.

Die Live-CI-Matrix startet MariaDB und PostgreSQL und prüft neben normalen Commits sowohl explizites Rollback als auch den Verlust einer offenen Verbindung ohne Commit. In beiden Fällen darf kein unvollständiger Transaktionskopf bestehen bleiben.

## World API und Viewer-Adapter

Die privilegierte World API stellt die kontrollierten NV$-Operationen unter `/api/v1/economy` bereit. Balance und Transaktionslesezugriffe verwenden `economy:read`; normale Transfers verwenden `economy:transfer`. Jeder Transfer verlangt einen `Idempotency-Key`, der innerhalb des authentifizierten Principals deterministisch auf eine Ledger-Transaktions-ID abgebildet wird. Kontostatusänderungen und Reversals bleiben `admin:*`.

Der API-Handler besitzt bewusst keinen Zugriff auf `INexLedgerStore.Append`, SQL-Verbindungen oder administrative Geldschöpfung. Er arbeitet ausschließlich über `NexEconomyService`.

Der Simulator verwendet `NexVerseMoneyModule` als `IMoneyModule`-Adapter. Das Modul öffnet keine Ledger-Datenbank. Balance- und Transferoperationen werden über die zentrale World API mit einem eingeschränkten Maschinen-API-Schlüssel ausgeführt. Die Konfiguration `[NexEconomyViewer]` ist standardmäßig deaktiviert; der Schlüssel wird über `NEXVERSE_ECONOMY_API_KEY` aus der Laufzeitumgebung bezogen.

Damit ist Roadmap 10.1 als Ledger-/Policy-/Adapter-Grundlage abgeschlossen. Die folgenden Roadmap-Blöcke ergänzen darauf aufbauend virtuelle Kontonummern, Bankfunktionen, Commerce, Land- und Objektzahlungen.

## Nächste Schichten

1. 10.2 virtuelle NexVerse-Kontonummern/IBAN-ähnliche Kennungen;
2. 10.3 erweiterte Bankfunktionen, Statements, Requests, Limits, Gebühren und Reconciliation;
3. 10.4 vollständige Viewer-Commerce-Flows;
4. 10.5+ Land-, Objekt-, Marketplace- und weitere Wirtschaftsabläufe.
