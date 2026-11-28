using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private OsmTerrainHeightfield? ReadOsmSurface(OsmDocument document, float4 bounds)
        {
            WaterSystem water = World.GetOrCreateSystemManaged<WaterSystem>();
            WaterSurfaceData<SurfaceWater> active = water.GetSurfaceData(out var waterWriter);
            waterWriter.Complete();
            if (active.isCreated && active.hasDepths)
            {
                var surfaces = new WaterSurfacesData(active, default, false);
                if (water.simulateBackdrop)
                {
                    surfaces = water.GetSurfacesData(out var backdropWriter);
                    backdropWriter.Complete();
                    surfaces.hasBackdrop = surfaces.downscaledDepths.isCreated && surfaces.downscaledDepths.hasDepths;
                }
                WaterSurfaceData<SurfaceWater> known = surfaces.hasBackdrop ? surfaces.downscaledDepths : active;
                float2 worldScale = surfaces.hasBackdrop ? TerrainUtils.BackDropWorldSizeScale.xz : new float2(1f);
                float2 waterSpacing = worldScale / known.scale.xz;
                float4 waterBounds = SurfaceOverlap(bounds, -known.offset.xz * worldScale, known.resolution.xz, waterSpacing);
                if (math.all(waterBounds.xy < waterBounds.zw))
                    CityOsmSurface.AddWater(document, waterBounds, WaterUtils.GetSampleInterval(ref active),
                        position => WaterUtils.SampleDepth(ref surfaces, new float3(position.x, 0f, position.y)));
            }

            TerrainHeightData terrain = World.GetOrCreateSystemManaged<TerrainSystem>().GetHeightData(waitForPending: true);
            if (!terrain.isCreated) return null;
            float2 activeSpacing = 1f / terrain.scale.xz;
            bool hasBackdrop = terrain.hasBackdrop && terrain.downscaledHeights.IsCreated && terrain.downscaledHeights.Length > 0;
            float2 backdropScale = hasBackdrop ? TerrainUtils.BackDropWorldSizeScale.xz : new float2(1f);
            float2 terrainSpacing = activeSpacing * backdropScale * (hasBackdrop ? TerrainSystem.kDownScaledHeightmapScale : 1f);
            int2 resolution = hasBackdrop ? terrain.downScaledResolution.xz : terrain.resolution.xz;
            float2 terrainMinimum = -terrain.offset.xz * backdropScale - terrainSpacing * 0.5f;
            float4 terrainBounds = SurfaceOverlap(bounds, terrainMinimum, resolution, terrainSpacing);
            return math.all(terrainBounds.xy < terrainBounds.zw)
                ? CityOsmSurface.SampleTerrain(terrainBounds, math.cmin(activeSpacing),
                    position => TerrainUtils.SampleHeight(ref terrain, new float3(position.x, 0f, position.y)))
                : (OsmTerrainHeightfield?)null;
        }

        private static float4 SurfaceOverlap(float4 bounds, float2 minimum, int2 resolution, float2 spacing)
        {
            float2 maximum = minimum + (float2)resolution * spacing;
            return new float4(math.max(bounds.xy, minimum), math.min(bounds.zw, maximum));
        }
    }
}
