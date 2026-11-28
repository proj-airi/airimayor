using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using AgeMask = Game.Tools.AgeMask;

namespace CS2MCP
{
    public sealed partial class BridgeToolSystem
    {
        private void CreateOsmObject(OsmBuildingPlacement item)
        {
            var controlPoints = new NativeList<ControlPoint>(1, Allocator.TempJob);
            try
            {
                controlPoints.Add(new ControlPoint
                {
                    m_Position = item.Position,
                    m_HitPosition = item.Position,
                    m_Rotation = item.Rotation,
                    m_OriginalEntity = item.AttachParent,
                });
                NativeReference<AttachmentData> attachmentPrefab = default;
                JobHandle handle = base.CreateDefinitions(
                    item.Prefab,
                    Entity.Null,
                    Entity.Null,
                    item.UpgradeOwner,
                    item.Original,
                    Entity.Null,
                    m_CityConfigurationSystem.defaultTheme,
                    controlPoints,
                    attachmentPrefab,
                    false,
                    m_CityConfigurationSystem.leftHandTraffic,
                    false,
                    false,
                    0f,
                    0f,
                    0f,
                    0f,
                    0f,
                    RandomSeed.Next(),
                    Snap.None,
                    AgeMask.Mature,
                    false,
                    default(PlacementOverrides),
                    default(JobHandle));
                handle.Complete();
            }
            finally
            {
                if (controlPoints.IsCreated)
                {
                    controlPoints.Dispose();
                }
            }
        }
    }
}
