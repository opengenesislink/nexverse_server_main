# OpenGenesisLINK `llEstateSay`

`llEstateSay` ist eine OpenGenesisLINK-Erweiterung der LSL-Kommunikation.

## Syntax

```lsl
llEstateSay(integer channel, string message);
```

Die Funktion arbeitet aehnlich wie `llRegionSay`, erweitert die Reichweite jedoch auf alle laufenden Regionen desselben Estates.

## Berechtigung

Der Server prueft den Besitzer des Objekts, in dem das Script laeuft. Der Aufruf wird nur ausgefuehrt, wenn dieser Besitzer im aktuellen Estate:

- Estate Owner oder
- Estate Manager

ist.

Normale Einwohner, Parcel Owner, Gruppenmitglieder oder fremde Objektbesitzer erhalten dadurch keine Estate-weite Sendeberechtigung. Ein nicht autorisierter Aufruf wird verworfen und serverseitig protokolliert.

## Kanaele und Nachrichtengroesse

- Kanal `0` ist nicht erlaubt.
- `DEBUG_CHANNEL` ist nicht erlaubt.
- andere positive oder negative Integer-Kanaele sind erlaubt;
- die Nachricht wird wie bei `llRegionSay` auf maximal 1023 Zeichen begrenzt.

Fuer administrative Steuerkommunikation sollte ein eigener negativer Kanal verwendet werden.

Ein Chatkanal ist keine Verschluesselung und kein Secret. Scripts, die den Kanal kennen und in einer belieferten Region lauschen, koennen die Nachricht empfangen. Passwoerter, API-Schluessel und andere Secrets duerfen deshalb nicht ueber `llEstateSay` versendet werden.

## Reichweite

Innerhalb eines Simulatorprozesses stellt OpenGenesisLINK die Nachricht direkt an alle geladenen Regionen mit derselben Estate-ID zu.

Ist der NexVerse NodeAgent mit authentifiziertem NexBus aktiv und `EstateScriptMessaging = true` gesetzt, wird die Nachricht zusaetzlich als `estate.script.message.requested` an andere Simulatornodes verteilt. Empfangende Nodes pruefen erneut:

- Estate-ID;
- Estate-Owner/Manager-Berechtigung des Quellbesitzers;
- Nachrichtenalter;
- Ratenlimit.

Die eigentliche Zustellung erfolgt weiterhin ueber `IWorldComm`. Empfangende Scripts sehen daher ein normales `listen`-Event.

## Sender

```lsl
integer ESTATE_CHANNEL = -9342801;

default
{
    touch_start(integer count)
    {
        llEstateSay(
            ESTATE_CHANNEL,
            "Estate-Systemtest"
        );
    }
}
```

Das Objekt muss dem Estate Owner oder einem eingetragenen Estate Manager gehoeren.

## Empfaenger

```lsl
integer ESTATE_CHANNEL = -9342801;

default
{
    state_entry()
    {
        llListen(
            ESTATE_CHANNEL,
            "",
            NULL_KEY,
            ""
        );
    }

    listen(
        integer channel,
        string name,
        key id,
        string message)
    {
        llOwnerSay(
            "Estate-Nachricht von " +
            name +
            ": " +
            message
        );
    }
}
```

Der Empfaenger selbst benoetigt keine Estate-Manager-Rechte. Die Berechtigung wird beim Senden durchgesetzt.

## Konfiguration

Der lokale Estate-Versand innerhalb desselben Simulatorprozesses benoetigt keinen NexBus.

Fuer mehrere Simulatornodes:

```ini
[NexVerseNodeAgent]
Enabled = true
EstateScriptMessaging = true
```

Zusaetzlich muss der bestehende NodeAgent/NexBus-Pfad korrekt authentifiziert und gegen ungeschuetzten externen Zugriff abgesichert sein.
