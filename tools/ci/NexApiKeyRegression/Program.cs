using System;
using System.IO;
using System.Linq;
using NexVerse.Core.Security;

internal static class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static int Main()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "nexverse-apikey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "keys.json");

        try
        {
            PersistentNexApiKeyStore store =
                new PersistentNexApiKeyStore(path);

            NexApiKeyRegistration registration =
                store.Create(
                    "Region read integration",
                    new[]
                    {
                        NexScopes.RegionsRead,
                        NexScopes.SimulatorsRead,
                        NexScopes.UsersRead
                    });

            Require(
                registration.ApiKey.StartsWith(
                    registration.Record.KeyId + ".",
                    StringComparison.Ordinal),
                "API key format mismatch");

            Require(
                store.TryValidate(
                    registration.ApiKey,
                    out NexApiKeyRecord validated),
                "new API key did not validate");

            Require(
                validated.Scopes.Length == 3 &&
                Array.Exists(
                    validated.Scopes,
                    scope => string.Equals(
                        scope,
                        NexScopes.SimulatorsRead,
                        StringComparison.OrdinalIgnoreCase)),
                "API key scopes were not retained");

            Require(
                !store.TryValidate(
                    registration.ApiKey + "x",
                    out _),
                "modified API key validated");

            PersistentNexApiKeyStore reloaded =
                new PersistentNexApiKeyStore(path);

            Require(
                reloaded.TryValidate(
                    registration.ApiKey,
                    out _),
                "API key did not persist across restart");

            Require(
                reloaded.SetEnabled(
                    registration.Record.KeyId,
                    false),
                "API key disable failed");

            Require(
                !reloaded.TryValidate(
                    registration.ApiKey,
                    out _),
                "disabled API key remained valid");

            bool rejectedAdmin = false;
            try
            {
                reloaded.Create(
                    "unsafe",
                    new[] { NexScopes.AdminAll });
            }
            catch (ArgumentException)
            {
                rejectedAdmin = true;
            }

            Require(
                rejectedAdmin,
                "API key store accepted admin:* scope");

            NexApiKeyRegistration experienceRegistration =
                reloaded.Create(
                    "Simulator Experiences adapter",
                    new[] { NexScopes.ExperiencesScript });

            Require(
                reloaded.TryValidate(
                    experienceRegistration.ApiKey,
                    out NexApiKeyRecord experienceKey) &&
                experienceKey.Scopes.Length == 1 &&
                string.Equals(
                    experienceKey.Scopes[0],
                    NexScopes.ExperiencesScript,
                    StringComparison.OrdinalIgnoreCase),
                "Experiences script machine key could not be issued or validated");

            PersistentNexApiKeyStore experiencesReloaded =
                new PersistentNexApiKeyStore(path);

            Require(
                experiencesReloaded.TryValidate(
                    experienceRegistration.ApiKey,
                    out _),
                "Experiences script machine key did not survive a restart");

            bool rejectedExperienceManagement = false;
            try
            {
                experiencesReloaded.Create(
                    "unsafe Experiences management",
                    new[] { NexScopes.ExperiencesManage });
            }
            catch (ArgumentException)
            {
                rejectedExperienceManagement = true;
            }

            Require(
                rejectedExperienceManagement,
                "Machine API keys must not receive experiences:manage");

            // Firestorm viewer-permission CAPS use a *separate* machine
            // credential; it must never carry administrator or script rights.
            NexApiKeyRegistration viewerPermissions =
                experiencesReloaded.Create(
                    "NexVerse Simulator Experience Permissions",
                    new[] { NexScopes.ExperiencesViewerPermissions });
            Require(experiencesReloaded.TryValidate(
                    viewerPermissions.ApiKey, out NexApiKeyRecord scopedViewer) &&
                scopedViewer.Scopes.Length == 1 &&
                scopedViewer.Scopes[0] == NexScopes.ExperiencesViewerPermissions,
                "Dedicated Firestorm permissions machine key failed");

            bool rejectedMixedPermissions = false;
            try
            {
                experiencesReloaded.Create("unsafe mixed permissions",
                    new[] { NexScopes.ExperiencesViewerPermissions,
                            NexScopes.ExperiencesScript });
            }
            catch (ArgumentException) { rejectedMixedPermissions = true; }
            Require(rejectedMixedPermissions,
                "Viewer permission credentials must not be mixed with script rights");

            // Duplicate detection is scoped to the exact service identity,
            // case-insensitive name and normalized set of scopes. It must
            // never reveal or silently replace the existing plaintext secret.
            bool rejectedDuplicate = false;
            try
            {
                experiencesReloaded.Create(
                    " nexverse simulator experience permissions ",
                    new[] { NexScopes.ExperiencesViewerPermissions });
            }
            catch (InvalidOperationException) { rejectedDuplicate = true; }
            Require(rejectedDuplicate, "Duplicate enabled service key accepted");
            Require(experiencesReloaded.List().Count == 3,
                "Duplicate rejection modified the persistent key inventory");

            // Deletion is an irreversible second step after explicit disable.
            Require(!experiencesReloaded.Delete(viewerPermissions.Record.KeyId),
                "Deleting an active machine key must be forbidden");
            Require(experiencesReloaded.SetEnabled(
                    viewerPermissions.Record.KeyId, false),
                "Disabling permission key failed");
            Require(experiencesReloaded.Delete(viewerPermissions.Record.KeyId),
                "Deleting disabled key failed");
            Require(!experiencesReloaded.TryValidate(
                    viewerPermissions.ApiKey, out _),
                "Deleted viewer permission credential still validated");
            NexApiKeyRegistration replacement = experiencesReloaded.Create(
                "NexVerse Simulator Experience Permissions",
                new[] { NexScopes.ExperiencesViewerPermissions });
            Require(replacement.Record.KeyId != viewerPermissions.Record.KeyId &&
                experiencesReloaded.TryValidate(replacement.ApiKey, out _),
                "Explicit credential rotation failed");

            NexApiKeyRecord metadata = experiencesReloaded.List()
                .First(x => x.KeyId == replacement.Record.KeyId);
            Require(metadata.LastUsedAt > 0,
                "Last-used metadata lost when cloning records");
            PersistentNexApiKeyStore afterCleanup =
                new PersistentNexApiKeyStore(path);
            Require(afterCleanup.List().Count == 3 &&
                    afterCleanup.TryValidate(replacement.ApiKey, out _),
                "Key cleanup or replacement did not survive restart");

            Console.WriteLine(
                "NexVerse scoped API key regression: OK");
            return 0;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
