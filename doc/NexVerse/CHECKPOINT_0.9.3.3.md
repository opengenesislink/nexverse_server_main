# NexVerse 0.9.3.3 Development Checkpoint

Completed: 3 October 2026

NexVerse 0.9.3.3 completes the Identity, Display Names, Profiles, Social Graph and Security development checkpoint in the release train toward 0.9.3.4.

## Completed scope

- Resident-compatible login names: short, dotted and legacy two-part forms.
- Persistent non-unique Display Names with seven-day self-service cooldown and administrator override.
- Display Name integration in login, viewer CAPS, World API, LSL and same-region cache updates.
- WebProfileV3 backed by the authoritative profile datastore: image, username, display name, about, web URL, interests, partner, picks and classifieds.
- Persistent profile privacy controls for profile, online, groups and search visibility.
- Friends/relationships API: request, accept, decline, remove, rights, block/mute and relationship audit.
- Live presence projection respects the friendship online-visibility right.
- Partner changes are audited.
- Optional per-account TOTP MFA. Accounts without MFA remain on the normal password path.
- Optional per-account passkey/WebAuthn foundation with expiring one-use challenges, RP-ID/origin validation, user-presence checks, ES256 assertion verification and signature-counter protection.
- Security sessions and security-event history foundation.
- API-key lifecycle, scope restrictions and last-use tracking.
- Native World API login integrates optional TOTP and tracked security sessions.

## Compatibility and release policy

0.9.3.3 is a development checkpoint, not a separate stable release. NexVerse 0.9.3.1 remains the current stable release. Development now proceeds only to the already-bounded 0.9.3.4 Simulator, Region and Estate Control Plane scope. No 0.9.3.5 implementation work starts before the 0.9.3.4 release.

The historical OpenSimulator source baseline remains 0.9.3.0 Nessie.

## Validation

All changes were merged through pull requests after NexVerse CI validation. The final profile privacy/presence integration passed feature CI and main CI before this checkpoint was closed.
