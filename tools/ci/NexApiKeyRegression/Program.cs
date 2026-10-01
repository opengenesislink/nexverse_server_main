using System;
using System.IO;
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
                validated.Scopes.Length == 2,
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
