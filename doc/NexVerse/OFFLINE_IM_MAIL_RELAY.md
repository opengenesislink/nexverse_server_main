# NexVerse Offline IM Mail Relay

NexVerse can forward stored avatar-to-avatar offline IMs by email and accept a direct email reply back as an Inworld-IM.

## KeyHelp mailbox

Create or assign the mail domain `im.stadt-nexverse.de` in KeyHelp.

Create the mailbox:

```text
relay@im.stadt-nexverse.de
```

Enable **Catch-All** for this mailbox. Individual avatar UUID addresses do not need separate mailboxes.

The outgoing message uses the visible sender identity:

```text
Avatar Name <avatar-uuid@im.stadt-nexverse.de>
```

The Reply-To address is a short-lived signed relay address. It is valid for five days by default and is bound to the receiving local account email address.

## DNS

Point the MX of `im.stadt-nexverse.de` to the KeyHelp mail server. Publish SPF and the KeyHelp-generated DKIM record for the subdomain. DMARC is recommended.

## Runtime secrets

Set both values in the protected NexVerse runtime environment, normally `/etc/nexverse/nexverse.env`:

```text
NEXVERSE_IM_RELAY_SIGNING_KEY=<at-least-32-random-bytes>
NEXVERSE_IM_RELAY_IMAP_PASSWORD=<relay-mailbox-password>
```

Generate a signing key, for example:

```bash
openssl rand -hex 32
```

The same signing key must be visible to Robust and every simulator that sends offline IM email.

## Robust configuration

`[OfflineIMMailRelay]` runs only on Robust. It polls the Catch-All mailbox through IMAP and validates the signed Reply-To token before creating an Inworld-IM.

The default configuration expects:

```ini
IMAPHost = "mail.stadt-nexverse.de"
IMAPPort = 993
UseSslOnConnect = true
IMAPUsername = "relay@im.stadt-nexverse.de"
```

Change `IMAPHost` if the KeyHelp server uses another mail hostname.

## Security properties

- Only active, local NexVerse accounts may send an email reply into the grid.
- The inbound From address must match the email stored on that local NexVerse account.
- Reply addresses are HMAC signed and expire after five days.
- The signed token is bound to the replying account email address.
- Automated/bounce messages are ignored.
- Only the new reply portion of the text/plain message is forwarded.
- Inworld reply length is capped.
- Messages are marked Seen only after terminal rejection or successful delivery/storage.
- A transient Inworld delivery exception leaves the email unread for retry.
- Robust is the sole IMAP consumer, preventing duplicate processing by multiple simulators.

## End-to-end test

1. Log avatar B out.
2. Avatar A sends B an IM.
3. B receives the styled NexVerse offline-IM email.
4. B presses Reply in the email client and sends a short response.
5. Robust polls `relay@im.stadt-nexverse.de`.
6. If A is online, A receives the response immediately as an Inworld-IM.
7. If A is offline, the Gatekeeper stores the reply as an offline IM for the next login.
