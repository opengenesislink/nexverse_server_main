// SPDX-License-Identifier: MPL-2.0
// OpenGenesisLINK 0.9.3.10 Dev – Firestorm Experience live smoke.
// Assign a real Experience to this script in a supported viewer before upload.
// Install in a controllable prim on OGL Developer Gen1; use a second avatar.
// No automatic grant or privileged permissions are assumed.
default
{
    state_entry()
    {
        llOwnerSay("Experience smoke ready. Experience id: " +
            (string)llGetExperienceDetails(NULL_KEY));
    }

    touch_start(integer count)
    {
        key resident = llDetectedKey(0);
        llOwnerSay("Asking " + (string)resident +
            " to allow the linked Experience.");
        llRequestExperiencePermissions(resident, "");
    }

    experience_permissions(key resident)
    {
        llOwnerSay("EXPERIENCE ALLOWED: " + (string)resident);
    }

    experience_permissions_denied(key resident, integer code)
    {
        llOwnerSay("EXPERIENCE DENIED: " + (string)resident +
            " error=" + (string)code + " " +
            llGetExperienceErrorMessage(code));
    }
}
