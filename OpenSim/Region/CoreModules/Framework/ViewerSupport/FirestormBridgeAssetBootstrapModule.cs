/*
 * OpenGenesisLINK Firestorm LSL bridge asset bootstrap.
 */

using System;
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;

namespace OpenSim.Region.CoreModules.Framework.ViewerSupport
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "FirestormBridgeAssetBootstrap")]
    public sealed class FirestormBridgeAssetBootstrapModule : INonSharedRegionModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private bool m_enabled;

        public string Name => "OpenGenesisLINK Firestorm Bridge Asset Bootstrap";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["FirestormBridge"];
            m_enabled = config?.GetBoolean("Enabled", false) ?? false;
        }

        public void AddRegion(Scene scene)
        {
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled || scene?.AssetService is null)
                return;

            try
            {
                string assetId = FirestormBridgeCompatibility.BootstrapAssetId.ToString();
                if (scene.AssetService.Get(assetId) is not null)
                    return;

                SceneObjectGroup bridgeContainer =
                    new(
                        Constants.servicesGodAgentID,
                        Vector3.Zero,
                        PrimitiveBaseShape.CreateBox());

                bridgeContainer.RootPart.UUID =
                    FirestormBridgeCompatibility.BootstrapObjectId;
                bridgeContainer.RootPart.Name =
                    FirestormBridgeCompatibility.GetBridgeItemName(
                        FirestormBridgeCompatibility.DefaultBridgeVersion);
                bridgeContainer.RootPart.Description =
                    "OpenGenesisLINK Firestorm LSL bridge bootstrap container";

                uint all = (uint)PermissionMask.All;
                bridgeContainer.RootPart.BaseMask = all;
                bridgeContainer.RootPart.OwnerMask = all;
                bridgeContainer.RootPart.NextOwnerMask = all;
                bridgeContainer.RootPart.GroupMask = (uint)PermissionMask.None;
                bridgeContainer.RootPart.EveryoneMask = (uint)PermissionMask.None;

                string xml =
                    SceneObjectSerializer.ToOriginalXmlFormat(
                        bridgeContainer,
                        false);

                AssetBase asset =
                    new(
                        FirestormBridgeCompatibility.BootstrapAssetId,
                        "OpenGenesisLINK Firestorm Bridge Bootstrap",
                        (sbyte)AssetType.Object,
                        Constants.servicesGodAgentID.ToString())
                    {
                        Description =
                            "Empty attachable container used to bootstrap Firestorm LSL Bridge on OpenSim-compatible grids.",
                        Data = Utils.StringToBytes(xml)
                    };

                string storedId = scene.AssetService.Store(asset);
                if (string.IsNullOrWhiteSpace(storedId))
                {
                    m_log.Warn(
                        "[FIRESTORM BRIDGE]: Failed to store bootstrap object asset.");
                    return;
                }

                m_log.InfoFormat(
                    "[FIRESTORM BRIDGE]: Stored bootstrap object asset {0}.",
                    storedId);
            }
            catch (Exception e)
            {
                m_log.WarnFormat(
                    "[FIRESTORM BRIDGE]: Failed to ensure bootstrap object asset: {0}",
                    e);
            }
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void Close()
        {
        }
    }
}
