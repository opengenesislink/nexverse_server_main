using System;
using System.IO;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Services.AuthenticationService;
using OpenSim.Services.EstateService;
using OpenSim.Services.InventoryService;
using OpenSim.Services.Interfaces;
using OpenSim.Services.UserAccountService;

internal static class Program
{
    private static readonly UUID UserId =
        new UUID("6f4d3a90-3b71-4f15-a804-2d94c8d7a201");

    private static readonly UUID RegionId =
        new UUID("1c6aa9e1-6cef-4c01-a4e4-6b4d19449ef1");

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

        IConfig gridUsers = config.AddConfig("GridUserService");
        gridUsers.Set(
            "LocalServiceModule",
            "OpenSim.Services.UserAccountService.dll:GridUserService");

        IConfig estates = config.AddConfig("EstateDataStore");
        estates.Set(
            "LocalServiceModule",
            "OpenSim.Services.EstateService.dll:EstateDataService");

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
                Environment.GetEnvironmentVariable("NEXVERSE_CI_LOGIN_PASSWORD")
                    ?? throw new InvalidOperationException("NEXVERSE_CI_LOGIN_PASSWORD is required")),
            "failed to seed CI password");

        XInventoryService inventoryService =
            new XInventoryService(config);

        inventoryService.CreateUserInventory(UserId);

        Require(
            inventoryService.GetRootFolder(UserId) != null,
            "failed to seed CI inventory root");

        GridUserService gridUserService =
            new GridUserService(config);

        Require(
            gridUserService.SetHome(
                UserId.ToString(),
                RegionId,
                new Vector3(128f, 128f, 25f),
                new Vector3(0f, 1f, 0f)),
            "failed to seed CI home position");

        Require(
            gridUserService.SetLastPosition(
                UserId.ToString(),
                UUID.Zero,
                RegionId,
                new Vector3(128f, 128f, 25f),
                new Vector3(0f, 1f, 0f)),
            "failed to seed CI last position");

        EstateDataService estateService =
            new EstateDataService(config);

        EstateSettings estate =
            estateService.CreateNewEstate(100);
        estate.EstateName = "NexVerse CI Estate";
        estate.EstateOwner = UserId;
        estateService.StoreEstateSettings(estate);

        Console.WriteLine(
            "NexVerse login placement seed: OK " +
            UserId);
        return 0;
    }
}
