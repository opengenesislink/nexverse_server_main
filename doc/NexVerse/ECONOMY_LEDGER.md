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

Die Runtime-Treiber MySQL/MariaDB, PostgreSQL und SQLite werden nicht in den Ledger-Kern eingebaut. Der Store erhält eine `Func<DbConnection>`; dadurch bleibt der Kern providerneutral. `NexLedgerSqlRuntime` löst die vorhandenen OpenSim-`StorageProvider`-Bezeichnungen auf die bereits mitgelieferten ADO.NET-Treiber auf und erzeugt die Verbindung ausschließlich per Reflection. Der SQLite-CI-Test benutzt genau diesen Resolver. Vor produktiven Transfers fehlen noch die Einbindung in die Robust-Konfiguration und echte Matrix-Regressionen gegen MySQL/MariaDB und PostgreSQL.

## Nächste Schichten

Auf dieser Grundlage folgen:

1. Robust-Konfigurationsanbindung über `[DatabaseService]` mit optionalem `[NexEconomy]`-Override;
2. Live-Matrix-/Crash-/Rollback-Tests für MySQL/MariaDB und PostgreSQL;
3. Kontolebenszyklus und Sperren;
4. Saldo-/Historien-/Transfer-Service;
5. Audit und Reconciliation;
6. virtuelle NexVerse-Kontonummern;
7. World API;
8. Viewer-`IMoneyModule`-Adapter;
9. Objekt-, Land- und Commerce-Zahlungsflüsse.
