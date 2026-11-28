using System;
using System.Collections.Generic;
using System.Text;
using Colossal.Mathematics;
using Game;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Notifications;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Scripting;
using AgeMask = Game.Tools.AgeMask;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    public sealed partial class BridgeToolSystem : ObjectToolBaseSystem
    {
        private enum Stage
        {
            Idle,
            CreateDefinitions,
            Apply,
            Readback,
            Finish,
        }

        private Stage m_Stage = Stage.Idle;
        private BridgeRequest m_PendingRequest;
        private ToolBaseSystem m_PreviousTool;
        private EntityQuery m_TempRouteQuery;

        private CityConfigurationSystem m_CityConfigurationSystem;

        public override string toolID => "CS2MCP.Bridge";

        public bool IsBusy => m_Stage != Stage.Idle;

        internal bool Handles(BridgeRequest request) => ReferenceEquals(m_PendingRequest, request);

        /// <summary>
        /// Resets a bridge operation that has not finished within the watchdog
        /// window. Must be called on the simulation thread.
        /// </summary>
        public void AbortStuckOperation()
        {
            if (m_Stage == Stage.Idle)
            {
                return;
            }
            CompletePending(OsmFailure(BridgeErrorKind.Timeout, m_Stage == Stage.Readback
                ? "The city received the edit but its result could not be confirmed. Read the district again before editing."
                : "Construction did not finish in time. Read the site before retrying."));
            applyMode = ApplyMode.None;
            Deactivate();
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            CreateOsmQueries();
            m_CityConfigurationSystem = base.World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_TempRouteQuery = GetEntityQuery(
                ComponentType.ReadOnly<Route>(),
                ComponentType.ReadOnly<Temp>(),
                ComponentType.Exclude<Deleted>());
        }

        public override PrefabBase GetPrefab() => null;

        public override bool TrySetPrefab(PrefabBase prefab)
        {
            // Never let the game UI select this tool via asset selection.
            return false;
        }

        private void Activate()
        {
            m_Stage = Stage.CreateDefinitions;
            m_PreviousTool = m_ToolSystem.activeTool;
            if (m_PreviousTool != null && m_PreviousTool.toolID == "airimayor.PointedPlace")
            {
                m_PreviousTool = m_DefaultToolSystem;
            }
            m_ToolSystem.activeTool = this;
        }

        [Preserve]
        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            if (m_OsmEdit == null) applyMode = ApplyMode.None;
            else UpdateOsmEdit();
            return inputDeps;
        }

        private void CompletePending(BridgeResponse response)
        {
            m_PendingRequest?.Complete(response);
            m_PendingRequest = null;
        }

        /// <summary>
        /// Pull ErrorType values off temp entities via their notification icons
        /// (IconElement → PrefabRef → ToolErrorData), so the agent sees why
        /// GetAllowApply failed instead of a generic overlap/water list.
        /// </summary>
        private string DescribeValidationBlock()
        {
            var reasons = new List<string>();
            using (NativeArray<Entity> entities = m_ErrorQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    CollectErrorReasons(entities[i], reasons);
                }
            }

            var message = new StringBuilder("operation blocked by game validation");
            if (reasons.Count > 0)
            {
                var unique = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string reason in reasons)
                {
                    if (seen.Add(reason))
                    {
                        unique.Add(reason);
                    }
                }
                message.Append(": ").Append(string.Join("; ", unique));
            }
            else
            {
                message.Append(" (no ErrorType icons on temp entities)");
            }

            message.Append("; try a different position or target");
            return message.ToString();
        }

        private void CollectErrorReasons(Entity entity, List<string> reasons)
        {
            if (EntityManager.HasComponent<PrefabRef>(entity))
            {
                TryAddToolError(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab, reasons);
            }

            if (!EntityManager.HasBuffer<IconElement>(entity))
            {
                return;
            }

            DynamicBuffer<IconElement> icons = EntityManager.GetBuffer<IconElement>(entity, true);
            for (int i = 0; i < icons.Length; i++)
            {
                Entity icon = icons[i].m_Icon;
                if (icon == Entity.Null || !EntityManager.Exists(icon))
                {
                    continue;
                }
                if (EntityManager.HasComponent<PrefabRef>(icon))
                {
                    TryAddToolError(EntityManager.GetComponentData<PrefabRef>(icon).m_Prefab, reasons);
                }
            }
        }

        private void TryAddToolError(Entity prefab, List<string> reasons)
        {
            if (prefab == Entity.Null || !EntityManager.HasComponent<ToolErrorData>(prefab))
            {
                return;
            }

            ErrorType error = EntityManager.GetComponentData<ToolErrorData>(prefab).m_Error;
            if (error == ErrorType.None)
            {
                return;
            }

            reasons.Add(DescribeErrorType(error));
        }

        private static string DescribeErrorType(ErrorType error)
        {
            switch (error)
            {
                case ErrorType.OverlapExisting:
                    return "OverlapExisting (overlaps building/prop/network)";
                case ErrorType.InvalidShape:
                    return "InvalidShape (geometry not valid here)";
                case ErrorType.NotEnoughMoney:
                    return "NotEnoughMoney";
                case ErrorType.LongDistance:
                    return "LongDistance (too far / split into shorter segments)";
                case ErrorType.TightCurve:
                    return "TightCurve (curve too sharp)";
                case ErrorType.InWater:
                    return "InWater (placement is invalid at this water depth)";
                case ErrorType.NotOnShoreline:
                    return "NotOnShoreline (move the placement seed closer to a wet/dry shoreline transition)";
                case ErrorType.ExceedsCityLimits:
                    return "ExceedsCityLimits (outside owned map tiles)";
                case ErrorType.AlreadyExists:
                    return "AlreadyExists";
                case ErrorType.ShortDistance:
                    return "ShortDistance (endpoints too close)";
                case ErrorType.LowElevation:
                    return "LowElevation";
                case ErrorType.SteepSlope:
                    return "SteepSlope";
                case ErrorType.SmallArea:
                    return "SmallArea";
                case ErrorType.ExceedsLotLimits:
                    return "ExceedsLotLimits";
                case ErrorType.OnFire:
                    return "OnFire";
                default:
                    return error.ToString();
            }
        }

        private void Deactivate()
        {
            m_Stage = Stage.Idle;
            m_OsmEdit = null;
            m_PendingRequest = null;
            if (m_ToolSystem.activeTool == this)
            {
                m_ToolSystem.activeTool = m_PreviousTool != null ? m_PreviousTool : m_DefaultToolSystem;
            }
            m_PreviousTool = null;
        }

        private void CreateDemolitionDefinition(Entity target)
        {
            EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            Entity e = commandBuffer.CreateEntity();
            var definition = new CreationDefinition
            {
                m_Original = target,
                m_Flags = CreationFlags.Delete,
            };
            commandBuffer.AddComponent(e, default(Updated));

            if (EntityManager.HasComponent<Game.Net.Edge>(target))
            {
                Game.Net.Edge edge = EntityManager.GetComponentData<Game.Net.Edge>(target);
                NetCourse course = default;
                course.m_Curve = EntityManager.GetComponentData<Game.Net.Curve>(target).m_Bezier;
                course.m_Length = MathUtils.Length(course.m_Curve);
                course.m_FixedIndex = EntityManager.HasComponent<Game.Net.Fixed>(target)
                    ? EntityManager.GetComponentData<Game.Net.Fixed>(target).m_Index
                    : -1;
                course.m_StartPosition.m_Entity = edge.m_Start;
                course.m_StartPosition.m_Position = course.m_Curve.a;
                course.m_StartPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(course.m_Curve));
                course.m_StartPosition.m_CourseDelta = 0f;
                course.m_EndPosition.m_Entity = edge.m_End;
                course.m_EndPosition.m_Position = course.m_Curve.d;
                course.m_EndPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(course.m_Curve));
                course.m_EndPosition.m_CourseDelta = 1f;
                commandBuffer.AddComponent(e, course);
            }
            else if (EntityManager.HasComponent<Transform>(target))
            {
                Transform transform = EntityManager.GetComponentData<Transform>(target);
                var objectDefinition = new ObjectDefinition
                {
                    m_Position = transform.m_Position,
                    m_Rotation = transform.m_Rotation,
                    m_Probability = 100,
                    m_PrefabSubIndex = -1,
                    m_LocalPosition = transform.m_Position,
                    m_LocalRotation = transform.m_Rotation,
                };
                if (EntityManager.HasComponent<Game.Objects.Elevation>(target))
                {
                    Game.Objects.Elevation elevation = EntityManager.GetComponentData<Game.Objects.Elevation>(target);
                    objectDefinition.m_Elevation = elevation.m_Elevation;
                    objectDefinition.m_ParentMesh = Game.Objects.ObjectUtils.GetSubParentMesh(elevation.m_Flags);
                }
                else
                {
                    objectDefinition.m_ParentMesh = -1;
                }
                commandBuffer.AddComponent(e, objectDefinition);
            }
            else if (EntityManager.HasBuffer<Game.Areas.Node>(target))
            {
                DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(target, isReadOnly: true);
                commandBuffer.AddBuffer<Game.Areas.Node>(e).CopyFrom(nodes.AsNativeArray());
            }

            commandBuffer.AddComponent(e, definition);
        }

        /// <summary>
        /// Mirrors AreaToolSystem's existing-area edit definition: the original
        /// area, its real owner, existing prefab, Relocate flag, and full Node
        /// buffer travel through Generate/Validation/ApplyAreasSystem together.
        /// </summary>
        private void CreateOperationalAreaDefinition(OsmPerimeter perimeter)
        {
            EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            Entity definitionEntity = commandBuffer.CreateEntity();
            commandBuffer.AddComponent(definitionEntity, new CreationDefinition
            {
                m_Original = perimeter.Area,
                m_Owner = perimeter.Owner,
                m_Prefab = perimeter.Prefab,
                m_Flags = CreationFlags.Relocate,
            });
            commandBuffer.AddComponent(definitionEntity, default(Updated));
            DynamicBuffer<Game.Areas.Node> nodes =
                commandBuffer.AddBuffer<Game.Areas.Node>(definitionEntity);
            foreach (Game.Areas.Node node in perimeter.Nodes)
            {
                nodes.Add(node);
            }
        }

        /// <summary>
    }
}
