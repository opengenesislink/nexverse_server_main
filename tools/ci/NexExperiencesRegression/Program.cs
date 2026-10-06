// SPDX-License-Identifier: MPL-2.0

using System;
using System.IO;
using System.Linq;
using NexVerse.Core.Experiences;

internal static class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Main()
    {
        string root =
            Path.Combine(
                Path.GetTempPath(),
                "ogl-experiences-" +
                Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        string path =
            Path.Combine(
                root,
                "experiences.json");

        Guid owner = Guid.NewGuid();
        Guid admin = Guid.NewGuid();
        Guid contributor = Guid.NewGuid();
        Guid resident = Guid.NewGuid();
        Guid blocked = Guid.NewGuid();
        Guid estate = Guid.NewGuid();
        Guid parcel = Guid.NewGuid();
        Guid script = Guid.NewGuid();

        NexExperienceStore store =
            new NexExperienceStore(path);

        NexExperience created =
            store.Create(
                owner,
                Guid.NewGuid(),
                "CI Experience",
                "Chapter 11 regression",
                NexExperienceMaturity.Moderate,
                "ci");

        Require(
            created.OwnerId == owner &&
            created.Admins.Contains(owner),
            "owner/admin foundation failed");

        store.SetRole(
            created.ExperienceId,
            owner,
            admin,
            "admin",
            true,
            "ci");
        store.SetRole(
            created.ExperienceId,
            admin,
            contributor,
            "contributor",
            true,
            "ci");

        Require(
            store.Get(created.ExperienceId).Contributors.Contains(contributor),
            "role persistence failed");

        store.SetResidentPermission(
            created.ExperienceId,
            admin,
            resident,
            NexExperiencePermissionStatus.Allowed,
            "ci");
        store.SetResidentPermission(
            created.ExperienceId,
            owner,
            blocked,
            NexExperiencePermissionStatus.Blocked,
            "ci");

        Require(
            store.GetResidentPermission(
                created.ExperienceId,
                resident) ==
                NexExperiencePermissionStatus.Allowed,
            "allowed resident failed");
        Require(
            store.GetResidentPermission(
                created.ExperienceId,
                blocked) ==
                NexExperiencePermissionStatus.Blocked,
            "blocked resident failed");

        store.SetLocationPolicy(
            created.ExperienceId,
            owner,
            "estate",
            estate,
            true,
            true,
            "ci");
        store.SetLocationPolicy(
            created.ExperienceId,
            admin,
            "parcel",
            parcel,
            true,
            true,
            "ci");

        Require(
            store.IsLocationAllowed(
                created.ExperienceId,
                estate,
                parcel),
            "allowed estate/parcel failed");
        Require(
            !store.IsLocationAllowed(
                created.ExperienceId,
                Guid.NewGuid(),
                parcel),
            "estate allow-list was not enforced");

        store.BindScript(
            created.ExperienceId,
            owner,
            script,
            "ci");

        Require(
            store.ResolveScript(script) ==
                created.ExperienceId,
            "script binding failed");

        Require(
            store.CreateKeyValue(
                created.ExperienceId,
                script,
                "alpha",
                "one"),
            "K/V create failed");

        Require(
            store.TryReadKeyValue(
                created.ExperienceId,
                script,
                "alpha",
                out string value) &&
            value == "one",
            "K/V read failed");

        Require(
            !store.UpdateKeyValue(
                created.ExperienceId,
                script,
                "alpha",
                "two",
                true,
                "stale",
                out bool retryMismatch) &&
            retryMismatch,
            "checked K/V retry semantics failed");

        Require(
            store.UpdateKeyValue(
                created.ExperienceId,
                script,
                "alpha",
                "two",
                true,
                "one",
                out retryMismatch) &&
            !retryMismatch,
            "checked K/V update failed");

        Require(
            store.UpdateKeyValue(
                created.ExperienceId,
                script,
                "created-by-update",
                "value",
                false,
                string.Empty,
                out retryMismatch),
            "missing-key update did not create");

        Require(
            store.GetKeyCount(
                created.ExperienceId,
                script) == 2,
            "K/V count mismatch");

        Require(
            store.ListKeys(
                created.ExperienceId,
                script,
                0,
                10).SequenceEqual(
                    new[]
                    {
                        "alpha",
                        "created-by-update"
                    }),
            "K/V key listing mismatch");

        Require(
            store.GetDataSize(
                created.ExperienceId,
                script) > 0,
            "K/V data size missing");

        Require(
            store.DeleteKeyValue(
                created.ExperienceId,
                script,
                "alpha"),
            "K/V delete failed");

        Require(
            store.GetLogs(
                created.ExperienceId,
                0,
                100).Count >= 6,
            "Experience audit log history missing");

        NexExperienceStore reopened =
            new NexExperienceStore(path);

        Require(
            reopened.Get(created.ExperienceId) != null &&
            reopened.ResolveScript(script) ==
                created.ExperienceId &&
            reopened.GetKeyCount(
                created.ExperienceId,
                script) == 1,
            "Experience persistence/reopen failed");

        reopened.Delete(
            created.ExperienceId,
            owner,
            "ci");

        Require(
            reopened.Get(created.ExperienceId) == null &&
            reopened.ResolveScript(script) == Guid.Empty,
            "Experience deletion cleanup failed");

        Directory.Delete(root, true);

        Console.WriteLine(
            "OpenGenesisLINK NexExperiences persistence, permissions and K/V regression: OK");
    }
}
