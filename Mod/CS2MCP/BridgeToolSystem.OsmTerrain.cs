using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Colossal.Mathematics;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;

namespace CS2MCP
{
    internal readonly struct OsmTerrainResult
    {
        public OsmTerrainResult(CityOsmTerrainMask mask, float minimumHeight, float maximumHeight,
            float maximumError, float tolerance, int sampleCount, int appliedStrokes)
        {
            BrushBounds = mask.BrushBounds;
            TransitionWidth = mask.Transition;
            MinimumHeight = minimumHeight;
            MaximumHeight = maximumHeight;
            MaximumError = maximumError;
            Tolerance = tolerance;
            SampleCount = sampleCount;
            AppliedStrokes = appliedStrokes;
        }

        public float4 BrushBounds { get; }
        public float TransitionWidth { get; }
        public float MinimumHeight { get; }
        public float MaximumHeight { get; }
        public float MaximumError { get; }
        public float Tolerance { get; }
        public int SampleCount { get; }
        public int AppliedStrokes { get; }
        public bool ReachedTarget => MaximumError <= Tolerance;
    }

    public sealed partial class BridgeToolSystem
    {
        private const int MaximumTerrainStrokes = 24;
        private const int NativeReadbackUpdates = 4;

        private sealed class OsmTerrainOperation
        {
            public Guid CityLoad;
            public OsmTerrainLevel Level;
            public CityOsmTerrainMask Mask;
            public List<int> Samples;
            public Entity NativeTool;
            public Func<BridgeResponse> CheckCurrent;
            public Func<OsmTerrainResult, BridgeResponse> Complete;
            public BridgeResponse Result;
            public bool Preview;
            public int Strokes;
            public int ReadbackUpdates;
            public float PriorError;
        }

        private OsmTerrainOperation m_OsmTerrain;
        private BrushPrefab m_OsmTerrainBrush;
        private Entity m_OsmTerrainBrushEntity;
        private EntityQuery m_OsmTerrainTools, m_OsmTerrainDefinitions, m_OsmTerrainTempBrushes;

        private void CreateOsmTerrainQueries()
        {
            m_OsmTerrainTools = GetEntityQuery(ComponentType.ReadOnly<TerraformingData>(), ComponentType.ReadOnly<PrefabData>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Locked>());
            m_OsmTerrainDefinitions = GetEntityQuery(ComponentType.ReadOnly<CreationDefinition>(),
                ComponentType.ReadOnly<BrushDefinition>());
            m_OsmTerrainTempBrushes = GetEntityQuery(ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Brush>(),
                ComponentType.ReadOnly<PrefabRef>());
        }

        internal string CaptureOsmTerrainStamp(float4 bounds)
        {
            TerrainHeightData terrain = ReadOsmTerrainHeights();
            CityOsmTerrainMask.BrushSize(bounds, math.cmax(1f / terrain.scale.xz), TerrainToolSystem.kMaxBrushSizeInGame);
            int4 rectangle = OsmTerrainPixels(ref terrain, bounds);
            using var stream = new MemoryStream();
            using var output = new BinaryWriter(stream);
            output.Write(rectangle.x);
            output.Write(rectangle.y);
            output.Write(rectangle.z);
            output.Write(rectangle.w);
            output.Write(terrain.resolution.x);
            output.Write(terrain.resolution.z);
            output.Write(terrain.offset.x);
            output.Write(terrain.offset.y);
            output.Write(terrain.offset.z);
            output.Write(terrain.scale.x);
            output.Write(terrain.scale.y);
            output.Write(terrain.scale.z);
            for (int row = rectangle.y; row <= rectangle.w; row++)
                for (int column = rectangle.x; column <= rectangle.z; column++)
                    output.Write(terrain.heights[row * terrain.resolution.x + column]);
            output.Flush();
            stream.Position = 0;
            using var hash = SHA256.Create();
            return Convert.ToBase64String(hash.ComputeHash(stream));
        }

        internal bool TryQueueOsmTerrain(BridgeRequest request, OsmTerrainLevel level,
            Func<BridgeResponse> checkCurrent, Func<OsmTerrainResult, BridgeResponse> complete, bool preview)
        {
            if (IsBusy) return false;
            TerrainHeightData heights = ReadOsmTerrainHeights();
            float minimum = TerrainUtils.ToWorldSpace(ref heights, 0);
            float maximum = TerrainUtils.ToWorldSpace(ref heights, ushort.MaxValue);
            if (level.TargetHeight < minimum || level.TargetHeight > maximum)
                throw new FormatException("The target height is outside the terrain height range for this city.");

            var mask = CityOsmTerrainMask.Create(level, math.cmax(1f / heights.scale.xz), TerrainToolSystem.kMaxBrushSizeInGame);
            int4 rectangle = OsmTerrainPixels(ref heights, level.Bounds);
            var samples = new List<int>();
            for (int row = rectangle.y; row <= rectangle.w; row++)
                for (int column = rectangle.x; column <= rectangle.z; column++)
                {
                    float2 point = TerrainUtils.ToWorldSpace(ref heights, new float3(column, 0, row)).xz;
                    if (mask.IsLevelCore(point)) samples.Add(row * heights.resolution.x + column);
                }

            if (samples.Count == 0)
                throw new FormatException("Draw a terrain area with an interior wider than its edge transition.");

            Entity nativeTool = FindOsmLevelTerrainTool();
            if (nativeTool == Entity.Null)
                throw new FormatException("The city's level terrain tool is unavailable.");

            m_OsmTerrain = new OsmTerrainOperation
            {
                CityLoad = BridgeSystem.Instance.CityLoadId,
                Level = level,
                Mask = mask,
                Samples = samples,
                NativeTool = nativeTool,
                CheckCurrent = checkCurrent,
                Complete = complete,
                Preview = preview,
            };
            m_PendingRequest = request;
            Activate();
            return true;
        }

        private TerrainHeightData ReadOsmTerrainHeights()
        {
            var system = World.GetOrCreateSystemManaged<TerrainSystem>();
            TerrainHeightData terrain = system.GetHeightData(waitForPending: true);
            if (!terrain.isCreated || terrain.resolution.x <= 2 || terrain.resolution.z <= 2
                || terrain.heights.Length != terrain.resolution.x * terrain.resolution.z)
                throw new FormatException("The terrain height grid is not yet available.");
            return terrain;
        }

        private static int4 OsmTerrainPixels(ref TerrainHeightData terrain, float4 bounds)
        {
            float2 lower = TerrainUtils.ToHeightmapSpace(ref terrain, new float3(bounds.x, 0, bounds.y)).xz;
            float2 upper = TerrainUtils.ToHeightmapSpace(ref terrain, new float3(bounds.z, 0, bounds.w)).xz;
            int2 last = terrain.resolution.xz - 1;
            if (math.any(lower < 0f) || math.any(upper > last))
                throw new FormatException("The terrain area extends beyond the editable terrain grid.");
            return new int4(math.max(0, (int2)math.floor(lower) - 1), math.min(last, (int2)math.ceil(upper) + 1));
        }

        private Entity FindOsmLevelTerrainTool()
        {
            using NativeArray<Entity> tools = m_OsmTerrainTools.ToEntityArray(Allocator.Temp);
            foreach (Entity tool in tools)
            {
                TerraformingData data = EntityManager.GetComponentData<TerraformingData>(tool);
                if (data.m_Type == TerraformingType.Level && data.m_Target == TerraformingTarget.Height) return tool;
            }
            return Entity.Null;
        }

        private void UpdateOsmTerrain()
        {
            applyMode = ApplyMode.None;
            try
            {
                if (m_Stage == Stage.Finish)
                {
                    CompletePending(m_OsmTerrain.Result);
                    Deactivate();
                    return;
                }
                if (m_OsmTerrain.CityLoad != BridgeSystem.Instance?.CityLoadId)
                    throw new InvalidOperationException("Another city was loaded before the terrain could be checked.");

                if (m_PendingRequest.CancellationToken.IsCancellationRequested)
                {
                    EndOsmTerrain(TerrainFailure(BridgeErrorKind.Conflict, "The terrain edit was cancelled."));
                    return;
                }
                if (!airimayor.Setting.StaticAllowConstruction)
                {
                    EndOsmTerrain(TerrainFailure(BridgeErrorKind.Conflict, "This terrain edit requires construction permission."));
                    return;
                }

                if (m_Stage == Stage.Readback)
                {
                    UpdateTerrainReadback();
                    return;
                }
                if (m_Stage == Stage.CreateDefinitions)
                {
                    if (!OsmTerrainBrushReady()) return;
                    applyMode = ApplyMode.Clear;
                    CreateOsmTerrainDefinition();
                    m_Stage = Stage.Apply;
                    return;
                }

                if (!HasOsmTerrainPreview()) return;
                if (!m_ErrorQuery.IsEmptyIgnoreFilter || !GetAllowApply())
                    throw new InvalidOperationException(DescribeValidationBlock());

                if (m_OsmTerrain.Strokes == 0)
                {
                    BridgeResponse conflict = m_OsmTerrain.CheckCurrent();
                    if (conflict != null)
                    {
                        EndOsmTerrain(conflict);
                        return;
                    }
                }
                OsmTerrainResult result = MeasureOsmTerrain();
                if (m_OsmTerrain.Preview || result.ReachedTarget)
                {
                    EndOsmTerrain(m_OsmTerrain.Complete(result));
                    return;
                }

                m_OsmTerrain.PriorError = result.MaximumError;
                m_OsmTerrain.ReadbackUpdates = 0;
                m_OsmTerrain.Strokes++;
                m_Stage = Stage.Readback;
                applyMode = ApplyMode.Apply;
            }
            catch (Exception exception)
            {
                EndOsmTerrain(TerrainFailure(BridgeErrorKind.Conflict, exception.Message));
            }
        }

        private void UpdateTerrainReadback()
        {
            OsmTerrainResult result = MeasureOsmTerrain();
            bool progressing = result.MaximumError < m_OsmTerrain.PriorError;
            bool waiting = !result.ReachedTarget && !progressing && ++m_OsmTerrain.ReadbackUpdates < NativeReadbackUpdates;
            if (!waiting && (result.ReachedTarget || !progressing || m_OsmTerrain.Strokes >= MaximumTerrainStrokes))
            {
                EndOsmTerrain(m_OsmTerrain.Complete(result));
                return;
            }
            ClearOsmTerrainDefinitions();
            if (!waiting) m_Stage = Stage.CreateDefinitions;
        }

        private OsmTerrainResult MeasureOsmTerrain()
        {
            TerrainHeightData terrain = ReadOsmTerrainHeights();
            float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
            foreach (int index in m_OsmTerrain.Samples)
            {
                float height = TerrainUtils.ToWorldSpace(ref terrain, terrain.heights[index]);
                minimum = math.min(minimum, height);
                maximum = math.max(maximum, height);
            }
            float target = m_OsmTerrain.Level.TargetHeight;
            float error = math.max(math.abs(target - minimum), math.abs(target - maximum));
            float tolerance = math.max(0.15f, 2f / terrain.scale.y);
            return new OsmTerrainResult(m_OsmTerrain.Mask, minimum, maximum, error, tolerance,
                m_OsmTerrain.Samples.Count, m_OsmTerrain.Strokes);
        }

        private bool OsmTerrainBrushReady()
        {
            if (m_OsmTerrainBrush == null)
            {
                var prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
                var brush = ScriptableObject.CreateInstance<BrushPrefab>();
                brush.name = "AIRI Mayor Terrain Mask";
                brush.m_Priority = int.MaxValue;
                brush.m_Texture = BuildOsmTerrainTexture();
                if (!prefabSystem.AddPrefab(brush))
                {
                    UnityEngine.Object.Destroy(brush.m_Texture);
                    UnityEngine.Object.Destroy(brush);
                    throw new InvalidOperationException("The city's terrain brush could not be registered.");
                }
                m_OsmTerrainBrush = brush;
                m_OsmTerrainBrushEntity = prefabSystem.GetEntity(brush);
            }

            return EntityManager.HasComponent<BrushData>(m_OsmTerrainBrushEntity)
                && EntityManager.GetComponentData<BrushData>(m_OsmTerrainBrushEntity).m_Archetype.Valid;
        }

        private Texture2D BuildOsmTerrainTexture()
        {
            CityOsmTerrainMask mask = m_OsmTerrain.Mask;
            var pixels = new Color32[mask.Pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                byte opacity = mask.Pixels[i];
                pixels[i] = new Color32(opacity, opacity, opacity, opacity);
            }
            var texture = new Texture2D(mask.Resolution, mask.Resolution, TextureFormat.RGBA32, false, true)
            {
                name = "AIRI Mayor Terrain Mask",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false);
            return texture;
        }

        private bool HasOsmTerrainPreview()
        {
            using NativeArray<Entity> previews = m_OsmTerrainTempBrushes.ToEntityArray(Allocator.Temp);
            foreach (Entity entity in previews)
            {
                PrefabRef prefab = EntityManager.GetComponentData<PrefabRef>(entity);
                if (prefab.m_Prefab != m_OsmTerrainBrushEntity || EntityManager.HasComponent<Deleted>(entity)) continue;
                Temp temp = EntityManager.GetComponentData<Temp>(entity);
                if ((temp.m_Flags & TempFlags.Cancel) != 0)
                    throw new InvalidOperationException("The city cancelled the terrain brush.");
                return true;
            }
            return false;
        }

        private void CreateOsmTerrainDefinition()
        {
            CityOsmTerrainMask mask = m_OsmTerrain.Mask;
            TerrainHeightData terrain = ReadOsmTerrainHeights();
            var center = new float3(mask.Center.x, 0, mask.Center.y);
            center.y = TerrainUtils.SampleHeight(ref terrain, center);
            EntityCommandBuffer commands = m_ToolOutputBarrier.CreateCommandBuffer();
            Entity definition = commands.CreateEntity();
            commands.AddComponent(definition, new CreationDefinition { m_Prefab = m_OsmTerrainBrushEntity });
            commands.AddComponent(definition, new BrushDefinition
            {
                m_Tool = m_OsmTerrain.NativeTool,
                m_Line = new Line3.Segment(center, center),
                m_Size = mask.Size,
                m_Strength = 1,
                m_Time = 1,
                m_Target = new float3(center.x, m_OsmTerrain.Level.TargetHeight, center.z),
                m_Start = center,
            });
            commands.AddComponent(definition, default(Updated));
        }

        private void ClearOsmTerrainDefinitions()
        {
            if (m_OsmTerrainBrushEntity == Entity.Null) return;
            using NativeArray<Entity> definitions = m_OsmTerrainDefinitions.ToEntityArray(Allocator.Temp);
            EntityCommandBuffer commands = m_ToolOutputBarrier.CreateCommandBuffer();
            foreach (Entity entity in definitions)
                if (EntityManager.GetComponentData<CreationDefinition>(entity).m_Prefab == m_OsmTerrainBrushEntity
                    && !EntityManager.HasComponent<Deleted>(entity))
                    commands.AddComponent(entity, default(Deleted));
        }

        private void EndOsmTerrain(BridgeResponse response)
        {
            ClearOsmTerrainDefinitions();
            applyMode = ApplyMode.Clear;
            m_OsmTerrain.Result = response;
            m_Stage = Stage.Finish;
        }

        private BridgeResponse TerrainFailure(BridgeErrorKind kind, string message)
        {
            if (m_OsmTerrain.Strokes > 0)
                message += " The city may have applied part of the terrain edit. Read the current ground before retrying.";
            return BridgeResponse.Error(kind, message);
        }

        private void StopOsmTerrain(BridgeErrorKind kind, string message)
        {
            CompletePending(TerrainFailure(kind, message));
            applyMode = ApplyMode.Clear;
            Deactivate();
        }

        private void ResetOsmTerrain()
        {
            m_OsmTerrain = null;
            if (m_OsmTerrainBrush == null) return;

            using NativeArray<Entity> definitions = m_OsmTerrainDefinitions.ToEntityArray(Allocator.Temp);
            foreach (Entity entity in definitions)
                if (EntityManager.GetComponentData<CreationDefinition>(entity).m_Prefab == m_OsmTerrainBrushEntity)
                    EntityManager.DestroyEntity(entity);
            using NativeArray<Entity> previews = m_OsmTerrainTempBrushes.ToEntityArray(Allocator.Temp);
            foreach (Entity entity in previews)
                if (EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab == m_OsmTerrainBrushEntity)
                    EntityManager.DestroyEntity(entity);

            World.GetOrCreateSystemManaged<PrefabSystem>().RemovePrefab(m_OsmTerrainBrush);
            DestroyOsmTerrainBrush();
        }

        private void DestroyOsmTerrainBrush()
        {
            if (m_OsmTerrainBrush != null)
            {
                UnityEngine.Object.Destroy(m_OsmTerrainBrush.m_Texture);
                UnityEngine.Object.Destroy(m_OsmTerrainBrush);
            }
            m_OsmTerrainBrush = null;
            m_OsmTerrainBrushEntity = Entity.Null;
        }

        [Preserve]
        protected override void OnDestroy()
        {
            DestroyOsmTerrainBrush();
            base.OnDestroy();
        }
    }
}
