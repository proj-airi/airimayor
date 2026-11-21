using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    internal struct OsmRoadPlacement
    {
        public long WayId, StartId, EndId;
        public Entity Original, Prefab, Start, End;
        public float3 StartPosition, EndPosition;
        public RoadPath Curve;
        public Bezier4x3 Bezier => new Bezier4x3(Curve.A, Curve.B, Curve.C, Curve.D);
        public bool Ground;
        public MapGrade? Grade;
        public string Name;
    }

    internal struct OsmBuildingPlacement
    {
        public long WayId;
        public Entity Original, Prefab, AttachParent;
        public float3 Position;
        public quaternion Rotation;
        public string Name;
    }

    public sealed partial class BridgeToolSystem
    {
        private sealed class OsmEdit
        {
            public Guid CityLoad;
            public IReadOnlyList<OsmRoadPlacement> Roads;
            public IReadOnlyList<OsmBuildingPlacement> Buildings;
            public IReadOnlyList<Entity> Deletions;
            public Func<BridgeResponse> CheckCurrent;
            public Func<IEnumerable<Entity>, BridgeResponse> Readback;
            public bool NamesApplied;
            public readonly List<OsmRoadWitness> Edges = new List<OsmRoadWitness>();
            public readonly Dictionary<Entity, float3> Junctions = new Dictionary<Entity, float3>();
            public readonly List<(OsmBuildingPlacement Placement, Entity Entity)> Objects =
                new List<(OsmBuildingPlacement, Entity)>();
        }

        private OsmEdit m_OsmEdit;
        private EntityQuery m_OsmTempEdges, m_OsmTempObjects;

        private void CreateOsmQueries()
        {
            m_OsmTempEdges = GetEntityQuery(ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Edge>(),
                ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Owner>());
            m_OsmTempObjects = GetEntityQuery(ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Transform>(),
                ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Deleted>());
        }

        internal bool TryQueueOsmEdit(BridgeRequest request, IReadOnlyList<OsmRoadPlacement> roads,
            IReadOnlyList<OsmBuildingPlacement> buildings, IReadOnlyList<Entity> deletions,
            Func<BridgeResponse> checkCurrent, Func<IEnumerable<Entity>, BridgeResponse> readback)
        {
            if (IsBusy) return false;
            m_OsmEdit = new OsmEdit
            {
                CityLoad = BridgeSystem.Instance.CityLoadId,
                Roads = roads, Buildings = buildings, Deletions = deletions,
                CheckCurrent = checkCurrent, Readback = readback
            };
            m_PendingRequest = request;
            Activate();
            return true;
        }

        [Preserve]
        protected override void OnStopRunning()
        {
            if (m_OsmEdit != null)
            {
                string reason = m_Stage == Stage.Readback
                    ? "The city received the edit before the active tool changed. Read the district again."
                    : "The active tool changed before the city applied the edit.";
                CompletePending(BridgeResponse.Error(BridgeErrorKind.Conflict, reason));
                Deactivate();
            }
            base.OnStopRunning();
        }

        private void UpdateOsmEdit()
        {
            try
            {
                if (m_Stage == Stage.Finish)
                {
                    applyMode = ApplyMode.None;
                    Deactivate();
                    return;
                }
                if (m_Stage == Stage.Readback)
                {
                    applyMode = ApplyMode.None;
                    if (m_OsmEdit.CityLoad != BridgeSystem.Instance.CityLoadId)
                        throw new InvalidOperationException("Another city was loaded before the district could be checked.");
                    VerifyAppliedOsmEdit();
                    if (!CheckOsmNames(preview: false)) return;
                    BridgeResponse result = m_OsmEdit.Readback(AppliedOsmEntities());
                    if (result == null) return;
                    CompletePending(result);
                    Deactivate();
                    return;
                }
                if (m_PendingRequest.CancellationToken.IsCancellationRequested)
                {
                    CompletePending(BridgeResponse.Error(BridgeErrorKind.Conflict, "The district edit was cancelled before construction."));
                    applyMode = ApplyMode.Clear;
                    m_Stage = Stage.Finish;
                    return;
                }
                BridgeResponse conflict = m_OsmEdit.CheckCurrent();
                if (conflict != null)
                {
                    CompletePending(conflict);
                    applyMode = ApplyMode.Clear;
                    m_Stage = Stage.Finish;
                    return;
                }
                if (m_Stage == Stage.CreateDefinitions)
                {
                    applyMode = ApplyMode.Clear;
                    var terrain = World.GetOrCreateSystemManaged<TerrainSystem>().GetHeightData();
                    foreach (OsmRoadPlacement road in m_OsmEdit.Roads)
                    {
                        CreateOsmCourse(road, ref terrain);
                    }
                    foreach (OsmBuildingPlacement building in m_OsmEdit.Buildings) CreateOsmObject(building);
                    foreach (Entity target in m_OsmEdit.Deletions) CreateModifyDefinitions(target, CreationFlags.Delete, default);
                    m_Stage = Stage.Apply;
                    return;
                }
                if (!m_ErrorQuery.IsEmptyIgnoreFilter || !GetAllowApply())
                {
                    throw new InvalidOperationException(DescribeValidationBlock());
                }
                CaptureOsmPreview();
                CheckOsmNames(preview: true);
                applyMode = ApplyMode.Apply;
                m_Stage = Stage.Readback;
            }
            catch (Exception exception)
            {
                bool submitted = m_Stage == Stage.Readback;
                string message = submitted
                    ? "The city received the edit. Read the district again before editing. "
                    : "The city declined the edit before applying it. ";
                CompletePending(BridgeResponse.Error(BridgeErrorKind.Conflict, message + exception.Message));
                applyMode = submitted ? ApplyMode.None : ApplyMode.Clear;
                m_Stage = Stage.Finish;
            }
        }

        private void CreateOsmCourse(OsmRoadPlacement road, ref TerrainHeightData terrain)
        {
            float3 start = road.StartPosition;
            float3 end = road.EndPosition;
            float startElevation = road.Ground ? 0f : start.y - TerrainUtils.SampleHeight(ref terrain, start);
            float endElevation = road.Ground ? 0f : end.y - TerrainUtils.SampleHeight(ref terrain, end);
            Bezier4x3 curve = road.Bezier;
            var course = new NetCourse
            {
                m_Curve = curve,
                m_Length = MathUtils.Length(curve),
                m_FixedIndex = road.Original != Entity.Null && EntityManager.HasComponent<Game.Net.Fixed>(road.Original)
                    ? EntityManager.GetComponentData<Game.Net.Fixed>(road.Original).m_Index : -1,
                m_StartPosition = new CoursePos
                {
                    m_Entity = road.Start, m_Position = start, m_Elevation = startElevation,
                    m_ParentMesh = -1, m_CourseDelta = 0,
                    m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(curve)),
                    m_Flags = CoursePosFlags.IsFirst | (road.Ground && road.Start == Entity.Null ? CoursePosFlags.FreeHeight : 0)
                },
                m_EndPosition = new CoursePos
                {
                    m_Entity = road.End, m_Position = end, m_Elevation = endElevation,
                    m_ParentMesh = -1, m_CourseDelta = 1,
                    m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(curve)),
                    m_Flags = CoursePosFlags.IsLast | (road.Ground && road.End == Entity.Null ? CoursePosFlags.FreeHeight : 0)
                }
            };
            EntityCommandBuffer buffer = m_ToolOutputBarrier.CreateCommandBuffer();
            Entity definition = buffer.CreateEntity();
            CreationFlags flags = 0;
            if (road.Original != Entity.Null)
            {
                flags = CreationFlags.Align | CreationFlags.SubElevation;
                Edge original = EntityManager.GetComponentData<Edge>(road.Original);
                if (road.Start == original.m_End || road.End == original.m_Start) flags |= CreationFlags.Invert;
            }
            buffer.AddComponent(definition, new CreationDefinition
            {
                m_Original = road.Original, m_Prefab = road.Prefab, m_RandomSeed = RandomSeed.Next().GetRandom(0).NextInt(),
                m_Flags = flags
            });
            buffer.AddComponent(definition, default(Updated));
            buffer.AddComponent(definition, course);
        }
    }
}
