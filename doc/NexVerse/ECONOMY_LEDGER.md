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

`INexLedgerStore` ist die Persistenzgrenze. Der derzeitige `InMemoryNexLedgerStore` ist ausschließlich Referenzimplementierung und Regressionstest-Backend.

Er darf **nicht** als produktive Wirtschaftsdatenbank aktiviert werden. Bevor Transfers oder Viewer-Zahlungen produktiv freigeschaltet werden, benötigt 0.9.3.6 einen transaktionalen, dauerhaften SQL-Store samt Migrationen und Crash-/Rollback-Tests für die unterstützten Produktionsdatenbanken.

## Nächste Schichten

Auf dieser Grundlage folgen:

1. dauerhafte SQL-Persistenz;
2. Kontolebenszyklus und Sperren;
3. Saldo-/Historien-/Transfer-Service;
4. Audit und Reconciliation;
5. virtuelle NexVerse-Kontonummern;
6. World API;
7. Viewer-`IMoneyModule`-Adapter;
8. Objekt-, Land- und Commerce-Zahlungsflüsse.
