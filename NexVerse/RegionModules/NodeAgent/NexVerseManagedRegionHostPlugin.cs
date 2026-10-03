// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;

namespace NexVerse.RegionModules.NodeAgent
{
    /// <summary>
    /// Simulator-process host used by the NodeAgent for explicitly enabled,
    /// NexVerse-managed region create/move operations.
    /// </summary>
    [Extension(
        Path = "/OpenSim/Startup",
        Id = "NexVerseManagedRegionHost",
        NodeName = "Plugin")]
    public sealed class NexVerseManagedRegionHostPlugin :
        IApplicationPlugin
    {
        private static readonly ILog m_Log =
            LogManager.GetLogger(
                typeof(NexVerseManagedRegionHostPlugin));

        private static readonly object s_CurrentSync =
            new object();

        private readonly object m_CommandSync =
            new object();

        private OpenSimBase m_OpenSim;
        private bool m_Enabled;
        private string m_ConfigDirectory =
            string.Empty;
        private string m_InternalAddress =
            "0.0.0.0";
        private string m_ExternalHostName =
            string.Empty;
        private string m_RegionType =
            "NexVerse Managed";
        private int m_PortMin = 9300;
        private int m_PortMax = 9399;

        public static NexVerseManagedRegionHostPlugin Current
        {
            get;
            private set;
        }

        public string Version => "0.9.3.2";
        public string Name =>
            "NexVerse Managed Region Host";

        public bool Enabled => m_Enabled;

        public void Initialise()
        {
            throw new PluginNotInitialisedException(
                Name +
                " requires OpenSim application initialization.");
        }

        public void Initialise(
            OpenSimBase openSim)
        {
            m_OpenSim =
                openSim ??
                throw new ArgumentNullException(
                    nameof(openSim));

            IConfig config =
                openSim.ConfigSource?.Source
                    ?.Configs["NexVerseNodeAgent"];

            m_Enabled =
                config != null &&
                config.GetBoolean(
                    "ManagedRegionCommands",
                    false);

            string configuredDirectory =
                config?.GetString(
                    "ManagedRegionConfigDirectory",
                    "Regions/NexVerseManaged") ??
                "Regions/NexVerseManaged";

            m_ConfigDirectory =
                Path.GetFullPath(
                    Path.IsPathRooted(
                        configuredDirectory)
                        ? configuredDirectory
                        : Path.Combine(
                            AppContext.BaseDirectory,
                            configuredDirectory));

            m_InternalAddress =
                (config?.GetString(
                    "ManagedRegionInternalAddress",
                    "0.0.0.0") ??
                 "0.0.0.0").Trim();

            m_ExternalHostName =
                (config?.GetString(
                    "ManagedRegionExternalHostName",
                    string.Empty) ??
                 string.Empty).Trim();

            m_RegionType =
                (config?.GetString(
                    "ManagedRegionType",
                    "NexVerse Managed") ??
                 "NexVerse Managed").Trim();

            m_PortMin =
                Math.Max(
                    1,
                    config?.GetInt(
                        "ManagedRegionPortMin",
                        9300) ??
                    9300);

            m_PortMax =
                Math.Min(
                    65535,
                    config?.GetInt(
                        "ManagedRegionPortMax",
                        9399) ??
                    9399);

            if (m_PortMax < m_PortMin)
            {
                m_Log.Error(
                    "[NEX-REGION-HOST]: Managed region port range is invalid; managed commands disabled.");
                m_Enabled = false;
            }

            if (m_Enabled)
            {
                Directory.CreateDirectory(
                    m_ConfigDirectory);

                m_Log.WarnFormat(
                    "[NEX-REGION-HOST]: Managed region mutations enabled. Config directory={0}, UDP ports={1}-{2}.",
                    m_ConfigDirectory,
                    m_PortMin,
                    m_PortMax);
            }

            lock (s_CurrentSync)
                Current = this;
        }

        public void PostInitialise()
        {
        }

        public bool TryCreateRegion(
            UUID regionId,
            string regionName,
            int gridX,
            int gridY,
            int sizeX,
            int sizeY,
            int estateId,
            out string error,
            out int internalPort,
            out string configPath)
        {
            error = string.Empty;
            internalPort = 0;
            configPath = string.Empty;

            if (!m_Enabled)
            {
                error =
                    "managed_region_commands_disabled";
                return false;
            }

            if (regionId.IsZero())
            {
                error =
                    "invalid_region_id";
                return false;
            }

            if (!IsSafeRegionName(regionName))
            {
                error =
                    "invalid_region_name";
                return false;
            }

            if (!IsValidGridCoordinate(gridX) ||
                !IsValidGridCoordinate(gridY) ||
                !IsValidRegionSize(sizeX) ||
                !IsValidRegionSize(sizeY))
            {
                error =
                    "invalid_region_geometry";
                return false;
            }

            lock (m_CommandSync)
            {
                SceneManager manager =
                    SceneManager.Instance;

                if (manager == null)
                {
                    error =
                        "scene_manager_unavailable";
                    return false;
                }

                if (manager.TryGetScene(
                        regionId,
                        out Scene _))
                {
                    error =
                        "region_already_running";
                    return false;
                }

                if (manager.TryGetScene(
                        regionName,
                        out Scene _))
                {
                    error =
                        "region_name_already_running";
                    return false;
                }

                if (!EstateExists(estateId))
                {
                    error =
                        "estate_not_found";
                    return false;
                }

                string externalHost =
                    ResolveExternalHostName();

                if (string.IsNullOrWhiteSpace(
                        externalHost))
                {
                    error =
                        "external_host_unavailable";
                    return false;
                }

                internalPort =
                    FindAvailablePort();

                if (internalPort == 0)
                {
                    error =
                        "managed_region_port_exhausted";
                    return false;
                }

                configPath =
                    ConfigPath(regionId);

                if (File.Exists(configPath))
                {
                    error =
                        "managed_region_config_exists";
                    return false;
                }

                try
                {
                    WriteManagedConfig(
                        configPath,
                        regionName,
                        regionId,
                        gridX,
                        gridY,
                        sizeX,
                        sizeY,
                        internalPort,
                        externalHost,
                        estateId);

                    if (!m_OpenSim.EstateDataService
                        .LinkRegion(
                            regionId,
                            estateId))
                    {
                        TryDelete(configPath);
                        error =
                            "estate_link_failed";
                        return false;
                    }

                    RegionInfo regionInfo =
                        LoadManagedRegion(
                            configPath,
                            regionName);

                    // The explicit estate link above ensures this is
                    // non-interactive and cannot fall back to console prompts.
                    m_OpenSim.PopulateRegionEstateInfo(
                        regionInfo);

                    m_OpenSim.CreateRegion(
                        regionInfo,
                        true,
                        out IScene scene);

                    if (scene == null)
                    {
                        error =
                            "region_create_failed";
                        return false;
                    }

                    scene.Start();

                    m_Log.InfoFormat(
                        "[NEX-REGION-HOST]: Created managed region {0} ({1}) at {2},{3}, size {4}x{5}, UDP {6}.",
                        regionName,
                        regionId,
                        gridX,
                        gridY,
                        sizeX,
                        sizeY,
                        internalPort);

                    return true;
                }
                catch (Exception e)
                {
                    m_Log.Error(
                        "[NEX-REGION-HOST]: Managed region create failed.",
                        e);
                    error =
                        "region_create_failed:" +
                        e.GetType().Name;
                    return false;
                }
            }
        }

        public bool TryMoveRegion(
            UUID regionId,
            int gridX,
            int gridY,
            out string error,
            out int oldGridX,
            out int oldGridY)
        {
            error = string.Empty;
            oldGridX = 0;
            oldGridY = 0;

            if (!m_Enabled)
            {
                error =
                    "managed_region_commands_disabled";
                return false;
            }

            if (regionId.IsZero() ||
                !IsValidGridCoordinate(gridX) ||
                !IsValidGridCoordinate(gridY))
            {
                error =
                    "invalid_region_placement";
                return false;
            }

            lock (m_CommandSync)
            {
                SceneManager manager =
                    SceneManager.Instance;

                if (manager == null ||
                    !manager.TryGetScene(
                        regionId,
                        out Scene scene))
                {
                    error =
                        "region_not_running_on_node";
                    return false;
                }

                if (scene.GetRootAgentCount() > 0)
                {
                    error =
                        "region_has_agents";
                    return false;
                }

                RegionInfo current =
                    scene.RegionInfo;

                oldGridX =
                    checked((int)current.RegionLocX);
                oldGridY =
                    checked((int)current.RegionLocY);

                if (oldGridX == gridX &&
                    oldGridY == gridY)
                {
                    return true;
                }

                string configPath =
                    ConfigPath(regionId);

                if (!File.Exists(configPath) ||
                    !SamePath(
                        current.RegionFile,
                        configPath))
                {
                    error =
                        "region_not_nexverse_managed";
                    return false;
                }

                string regionName =
                    current.RegionName;

                try
                {
                    IniConfigSource source =
                        new IniConfigSource(
                            configPath);

                    IConfig config =
                        source.Configs[regionName];

                    if (config == null ||
                        !UUID.TryParse(
                            config.GetString(
                                "RegionUUID",
                                string.Empty),
                            out UUID configuredId) ||
                        configuredId != regionId)
                    {
                        error =
                            "managed_region_config_invalid";
                        return false;
                    }

                    string oldLocation =
                        config.GetString(
                            "Location",
                            oldGridX +
                            "," +
                            oldGridY);

                    // CloseScene persists the region and deregisters it
                    // without deleting scene objects.
                    m_OpenSim.CloseRegion(
                        scene);

                    config.Set(
                        "Location",
                        gridX +
                        "," +
                        gridY);
                    source.Save(
                        configPath);

                    try
                    {
                        StartFromManagedConfig(
                            configPath,
                            regionName);

                        m_Log.InfoFormat(
                            "[NEX-REGION-HOST]: Moved managed region {0} ({1}) from {2},{3} to {4},{5}.",
                            regionName,
                            regionId,
                            oldGridX,
                            oldGridY,
                            gridX,
                            gridY);

                        return true;
                    }
                    catch (Exception moveError)
                    {
                        m_Log.Error(
                            "[NEX-REGION-HOST]: Region move failed; attempting rollback.",
                            moveError);

                        try
                        {
                            IniConfigSource rollback =
                                new IniConfigSource(
                                    configPath);
                            rollback.Configs[regionName]
                                ?.Set(
                                    "Location",
                                    oldLocation);
                            rollback.Save(
                                configPath);

                            StartFromManagedConfig(
                                configPath,
                                regionName);
                        }
                        catch (Exception rollbackError)
                        {
                            m_Log.Error(
                                "[NEX-REGION-HOST]: Region move rollback failed.",
                                rollbackError);
                        }

                        error =
                            "region_move_failed:" +
                            moveError.GetType().Name;
                        return false;
                    }
                }
                catch (Exception e)
                {
                    m_Log.Error(
                        "[NEX-REGION-HOST]: Managed region move failed.",
                        e);
                    error =
                        "region_move_failed:" +
                        e.GetType().Name;
                    return false;
                }
            }
        }

        private void StartFromManagedConfig(
            string configPath,
            string regionName)
        {
            RegionInfo regionInfo =
                LoadManagedRegion(
                    configPath,
                    regionName);

            m_OpenSim.PopulateRegionEstateInfo(
                regionInfo);

            m_OpenSim.CreateRegion(
                regionInfo,
                true,
                out IScene replacement);

            if (replacement == null)
                throw new InvalidOperationException(
                    "CreateRegion returned no scene.");

            replacement.Start();
        }

        private RegionInfo LoadManagedRegion(
            string configPath,
            string regionName)
        {
            RegionInfo regionInfo =
                new RegionInfo(
                    regionName,
                    configPath,
                    true,
                    m_OpenSim.ConfigSource.Source,
                    regionName);

            regionInfo.SetExtraSetting(
                "NexVerseNonFatalGridRegistration",
                "true");

            return regionInfo;
        }

        private bool EstateExists(
            int estateId)
        {
            if (estateId <= 0 ||
                m_OpenSim?.EstateDataService == null)
            {
                return false;
            }

            try
            {
                return m_OpenSim.EstateDataService
                    .GetEstatesAll()
                    .Contains(estateId);
            }
            catch
            {
                return false;
            }
        }

        private int FindAvailablePort()
        {
            HashSet<int> used =
                new HashSet<int>();

            SceneManager manager =
                SceneManager.Instance;

            if (manager != null)
            {
                foreach (Scene scene in
                         manager.Scenes)
                {
                    int port =
                        scene.RegionInfo
                            .InternalEndPoint
                            ?.Port ??
                        0;

                    if (port > 0)
                        used.Add(port);
                }
            }

            if (Directory.Exists(
                    m_ConfigDirectory))
            {
                foreach (string file in
                         Directory.EnumerateFiles(
                             m_ConfigDirectory,
                             "*.ini",
                             SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        IniConfigSource source =
                            new IniConfigSource(file);

                        foreach (IConfig config in
                                 source.Configs)
                        {
                            int port =
                                config.GetInt(
                                    "InternalPort",
                                    0);

                            if (port > 0)
                                used.Add(port);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            for (int port = m_PortMin;
                 port <= m_PortMax;
                 port++)
            {
                if (!used.Contains(port))
                    return port;
            }

            return 0;
        }

        private string ResolveExternalHostName()
        {
            if (!string.IsNullOrWhiteSpace(
                    m_ExternalHostName))
            {
                return m_ExternalHostName;
            }

            Scene template =
                SceneManager.Instance?.Scenes
                    .FirstOrDefault();

            return template?.RegionInfo
                       ?.ExternalHostName ??
                   string.Empty;
        }

        private void WriteManagedConfig(
            string path,
            string regionName,
            UUID regionId,
            int gridX,
            int gridY,
            int sizeX,
            int sizeY,
            int internalPort,
            string externalHost,
            int estateId)
        {
            Directory.CreateDirectory(
                m_ConfigDirectory);

            IniConfigSource source =
                new IniConfigSource();

            source.AddConfig(
                regionName);

            IConfig config =
                source.Configs[regionName];

            config.Set(
                "RegionUUID",
                regionId.ToString());
            config.Set(
                "Location",
                gridX +
                "," +
                gridY);
            config.Set(
                "SizeX",
                sizeX);
            config.Set(
                "SizeY",
                sizeY);
            config.Set(
                "InternalAddress",
                m_InternalAddress);
            config.Set(
                "InternalPort",
                internalPort);
            config.Set(
                "AllowAlternatePorts",
                false);
            config.Set(
                "ResolveAddress",
                false);
            config.Set(
                "ExternalHostName",
                externalHost);
            config.Set(
                "RegionType",
                m_RegionType);
            config.Set(
                "TargetEstate",
                estateId);

            source.Save(path);
        }

        private string ConfigPath(
            UUID regionId)
        {
            return Path.Combine(
                m_ConfigDirectory,
                regionId.ToString() +
                ".ini");
        }

        private static bool SamePath(
            string first,
            string second)
        {
            if (string.IsNullOrWhiteSpace(first) ||
                string.IsNullOrWhiteSpace(second))
            {
                return false;
            }

            return string.Equals(
                Path.GetFullPath(first),
                Path.GetFullPath(second),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }

        private static bool IsSafeRegionName(
            string name)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                name.Trim().Length > 128)
            {
                return false;
            }

            return name.IndexOfAny(
                       new[]
                       {
                           '\r',
                           '\n',
                           '[',
                           ']'
                       }) < 0;
        }

        private static bool IsValidGridCoordinate(
            int coordinate)
        {
            return
                coordinate >= 0 &&
                (long)coordinate *
                (int)Constants.RegionSize <=
                (long)int.MaxValue -
                (int)Constants.MaximumRegionSize;
        }

        private static bool IsValidRegionSize(
            int size)
        {
            return
                size >= (int)Constants.RegionSize &&
                size <= (int)Constants.MaximumRegionSize &&
                size % (int)Constants.RegionSize == 0;
        }

        private static void TryDelete(
            string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            lock (s_CurrentSync)
            {
                if (ReferenceEquals(
                        Current,
                        this))
                {
                    Current = null;
                }
            }

            m_OpenSim = null;
            m_Enabled = false;
        }
    }
}
