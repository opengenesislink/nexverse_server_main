# NexVerse OSSL Profile

NexVerse ships an active OSSL override profile in:

`bin/config-include/osslEnable.ini`

The profile is loaded automatically by `osslDefaultEnable.ini`.

## Policy

The complete current OSSL function surface is enabled, but privileged functions
are not exposed indiscriminately.

The profile uses:

`OSFunctionThreatLevel = NoAccess`

This is deliberate.  Existing functions are explicitly enabled through the
inherited `Allow_*` rules and NexVerse overrides.  A newly added OSSL function
therefore remains unavailable until it has been reviewed and assigned a policy.

This avoids the security problem created by setting the global threat level to
`Severe`, which would automatically permit future unclassified functions.

## Access model

- Low-risk functions that are explicitly marked `true` remain available to scripts.
- Existing restricted functions retain the inherited estate/parcel policy.
- NPC functions are limited to estate managers and estate owners.
- Forced avatar actions that OpenSimulator disables by default are enabled for
  estate managers and estate owners.
- `osConsoleCommand` is enabled only for `GRID_GOD` users.
- `osGetAgentIP` remains subject to its internal administrator/god check.
- Parcel ownership by itself does not automatically grant privileged OSSL calls.

## NexVerse overrides

The profile enables the functions that the inherited default file explicitly
disables:

- `osAvatarPlayAnimation`
- `osAvatarStopAnimation`
- `osForceAttachToOtherAvatarFromInventory`
- `osForceDetachFromAvatar`
- `osForceOtherSit`
- `osSetRot`
- `osConsoleCommand`
- `osSetContentType`

It also explicitly covers functions present in the current implementation that
do not have active rules in the inherited default configuration:

- `osTerrainSetHeight`
- `osSunGetParam`
- `osSunSetParam`
- `osWindActiveModelPluginName`
- `osGetGender`
- `osGetHealth`
- `osGetHealRate`
- `osGetRezzingObject`

## Runtime

A simulator restart is required after changing OSSL configuration.

The effective configuration can be inspected from the simulator console with the
normal configuration inspection commands.  OSSL permission failures are sent to
the object owner rather than broadcast to nearby users.

## Security rule

Do not replace the NexVerse policy with a blanket global `Severe` threat level.
When new OSSL functions are added, assign an explicit `Allow_<FunctionName>`
rule before making them available in production.


## Scripted-content compatibility

NexVerse deliberately exposes the three read-only synchronous notecard helpers to ordinary scripts:

- `osGetNotecard`
- `osGetNotecardLine`
- `osGetNumberOfNotecardLines`

Upstream defaults classify these as VeryHigh and restrict them to estate roles because synchronous asset reads can be expensive. That restriction breaks established OpenSim products such as PMAC 2.x when their owner is not an estate manager. NexVerse therefore overrides all three to `true`.

Administrative, force-avatar and console functions remain role-restricted. CI explicitly verifies that the notecard compatibility functions stay globally enabled.
