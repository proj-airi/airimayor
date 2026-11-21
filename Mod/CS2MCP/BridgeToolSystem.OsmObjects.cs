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
            CreateObjectDefinition(item.Prefab, item.Position, item.Rotation, Entity.Null, item.Original, item.AttachParent);
        }

        private void CreateObjectDefinition(Entity prefab, float3 position, quaternion rotation, Entity owner, Entity original, Entity attachParent)
        {
            var controlPoints = new NativeList<ControlPoint>(1, Allocator.TempJob);
            try
            {
                controlPoints.Add(new ControlPoint
                {
                    m_Position = position,
                    m_HitPosition = position,
                    m_Rotation = rotation,
                    m_OriginalEntity = attachParent,
                });
                NativeReference<AttachmentData> attachmentPrefab = default;
                JobHandle handle = base.CreateDefinitions(
                    prefab,
                    Entity.Null,
                    Entity.Null,
                    owner,
                    original,
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
