// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Concurrent;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace NexVerse.RegionModules.Archives
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "OglOarManagementModule")]
    public sealed class OglOarManagementModule : ISharedRegionModule
    {
        private readonly ConcurrentDictionary<UUID, OglOarOperationManager> m_Managers = new();
        private bool m_Enabled;
        private string m_StorageRoot = "OGLArchives";
        private long m_MaximumArchiveBytes = 20L * 1024 * 1024 * 1024;

        public string Name => "OGL OAR Management";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["OpenGenesisLINKOAR"];
            m_Enabled = config?.GetBoolean("Enabled", true) ?? true;
            if (!m_Enabled)
                return;

            m_StorageRoot = config?.GetString("StorageRoot", "OGLArchives") ?? "OGLArchives";
            long maxMiB = Math.Max(1, config?.GetLong("MaximumArchiveMiB", 20480) ?? 20480);
            m_MaximumArchiveBytes = checked(maxMiB * 1024L * 1024L);
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled || scene == null)
                return;

            string regionRoot = System.IO.Path.Combine(m_StorageRoot, scene.RegionInfo.RegionID.ToString());
            OglOarOperationManager manager = new(scene, new OglOarStoragePolicy(regionRoot, m_MaximumArchiveBytes));
            if (!m_Managers.TryAdd(scene.RegionInfo.RegionID, manager))
            {
                manager.Dispose();
                throw new InvalidOperationException("OAR-Manager fuer Region ist bereits registriert.");
            }
            scene.RegisterModuleInterface<IOglOarOperations>(manager);
        }

        public void RemoveRegion(Scene scene)
        {
            if (scene == null)
                return;

            scene.UnregisterModuleInterface<IOglOarOperations>(null);
            if (m_Managers.TryRemove(scene.RegionInfo.RegionID, out OglOarOperationManager manager))
                manager.Dispose();
        }

        public void RegionLoaded(Scene scene) { }
        public void PostInitialise() { }

        public void Close()
        {
            foreach (OglOarOperationManager manager in m_Managers.Values)
                manager.Dispose();
            m_Managers.Clear();
        }
    }
}
