using System;
using System.IO;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Services.AuthenticationService;
using OpenSim.Services.InventoryService;
using OpenSim.Services.UserAccountService;

internal static class Program
{
    private static readonly UUID UserId =
        new UUID("6f4d3a90-3b71-4f15-a804-2d94c8d7a201");

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static int Main(string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("Usage: NexLoginPlacementSeed <sqlite-db-path>");

        string databasePath = Path.GetFullPath(args[0]);
        string connectionString =
            "URI=file:" + databasePath + ",version=3,UseUTF16Encoding=True";

        MainConsole.Instance = new MockConsole();

        IniConfigSource config = new IniConfigSource();

        IConfig database = config.AddConfig("DatabaseService");
        database.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
        database.Set("ConnectionString", connectionString);
        database.Set("EstateConnectionString", connectionString);

        IConfig users = config.AddConfig("UserAccountService");
        users.Set(
            "LocalServiceModule",
            "OpenSim.Services.UserAccountService.dll:UserAccountService");

        IConfig authentication = config.AddConfig("AuthenticationService");
        authentication.Set(
            "LocalServiceModule",
            "OpenSim.Services.AuthenticationService.dll:PasswordAuthenticationService");

        IConfig inventory = config.AddConfig("InventoryService");
        inventory.Set(
            "LocalServiceModule",
            "OpenSim.Services.InventoryService.dll:XInventoryService");

        UserAccountService userService =
            new UserAccountService(config);

        UserAccount account =
            new UserAccount(
                UUID.Zero,
                UserId,
                "NexVerseCI",
                "Resident",
                "ci-login@nexverse.invalid")
            {
                UserLevel = 0,
                UserFlags = 0,
                Active = true,
                NexVerseState = "active",
                NexVerseStateReason = string.Empty,
                NexVerseStateChanged = 1
            };

        Require(
            userService.StoreUserAccount(account),
            "failed to seed CI user account");

        PasswordAuthenticationService authService =
            new PasswordAuthenticationService(
                config,
                userService);

        Require(
            authService.SetPassword(
                UserId,
                "NexVerse-CI-Login-2026"),
            "failed to seed CI password");

        XInventoryService inventoryService =
            new XInventoryService(config);

        inventoryService.CreateUserInventory(UserId);

        Require(
            inventoryService.GetRootFolder(UserId) != null,
            "failed to seed CI inventory root");

        Console.WriteLine(
            "NexVerse login placement seed: OK " +
            UserId);
        return 0;
    }
}
