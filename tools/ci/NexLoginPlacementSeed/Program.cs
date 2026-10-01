using System;
using System.IO;
using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.AssetService;
using OpenSim.Services.AuthenticationService;
using OpenSim.Services.EstateService;
using OpenSim.Services.InventoryService;
using OpenSim.Services.Interfaces;
using OpenSim.Services.SimulationService;
using OpenSim.Services.UserAccountService;

internal static class Program
{
    private static readonly UUID UserId =
        new UUID("6f4d3a90-3b71-4f15-a804-2d94c8d7a201");

    private static readonly UUID RegionId =
        new UUID("1c6aa9e1-6cef-4c01-a4e4-6b4d19449ef1");

    private static readonly UUID RemoteDataObjectId =
        new UUID("0c19865c-25b8-45f8-8fc3-0fe849b7f001");

    private static readonly UUID RemoteDataScriptItemId =
        new UUID("0c19865c-25b8-45f8-8fc3-0fe849b7f002");

    private static readonly UUID RemoteDataScriptAssetId =
        new UUID("0c19865c-25b8-45f8-8fc3-0fe849b7f003");

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

        IConfig assets = config.AddConfig("AssetService");
        assets.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
        assets.Set("ConnectionString", connectionString);
        assets.Set("DefaultAssetLoader", string.Empty);

        IConfig simulation = config.AddConfig("SimulationDataStore");
        simulation.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
        simulation.Set("ConnectionString", connectionString);

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

        SeedRemoteDataScript(config);

        Console.WriteLine(
            "NexVerse login placement + RemoteData seed: OK " +
            UserId);
        return 0;
    }

    private static void SeedRemoteDataScript(IConfigSource config)
    {
        const string scriptSource =
@"default
{
    state_entry()
    {
        llOpenRemoteDataChannel();
    }

    remote_data(integer event_type, key channel, key message_id, string sender, integer idata, string sdata)
    {
        if (event_type == 2)
        {
            llRemoteDataReply(
                channel,
                message_id,
                ""nexverse-ci-reply:"" + sdata,
                idata + 1);
        }
    }
}";

        AssetService assetService =
            new AssetService(config);

        AssetBase scriptAsset =
            new AssetBase(
                RemoteDataScriptAssetId,
                "NexVerse CI RemoteData Script",
                (sbyte)AssetType.LSLText,
                UserId.ToString())
            {
                Data = Encoding.UTF8.GetBytes(scriptSource)
            };

        Require(
            assetService.Store(scriptAsset) == RemoteDataScriptAssetId.ToString(),
            "failed to seed RemoteData LSL asset");

        SceneObjectGroup probe =
            new SceneObjectGroup(
                UserId,
                new Vector3(128f, 128f, 24f),
                PrimitiveBaseShape.CreateBox());

        probe.RootPart.UUID = RemoteDataObjectId;
        probe.RootPart.Name = "NexVerse CI RemoteData Probe";
        probe.RootPart.Description = "NEXJAST RemoteData end-to-end runtime probe";

        TaskInventoryItem scriptItem =
            new TaskInventoryItem
            {
                ItemID = RemoteDataScriptItemId,
                AssetID = RemoteDataScriptAssetId,
                OwnerID = UserId,
                LastOwnerID = UserId,
                CreatorID = UserId,
                GroupID = UUID.Zero,
                Name = "NexVerse CI RemoteData Script",
                Description = "CI-only runtime RemoteData probe",
                Type = (int)AssetType.LSLText,
                InvType = (int)InventoryType.LSL,
                ScriptRunning = true
            };

        probe.RootPart.Inventory.AddInventoryItem(scriptItem, false);

        SimulationDataService simulationData =
            new SimulationDataService(config);

        simulationData.StoreObject(probe, RegionId);

        Require(
            simulationData.LoadObjects(RegionId).Count > 0,
            "failed to persist RemoteData probe object");
    }
}
