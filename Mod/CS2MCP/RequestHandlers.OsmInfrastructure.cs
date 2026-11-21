using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private Entity ResolveOsmUtility(OsmWay original, OsmWay target, Entity existing)
        {
            CityOsmEdit.CheckEditedTags(original, target, "power", "man_made", "substance", "location", "airimayor:prefab");
            TypedNetworkKinds wanted = CityOsmInfrastructure.UtilityKinds(target);
            string name = CityOsm.Tag(target, "airimayor:prefab");
            return FindOsmInfrastructureAsset(NetPrefabQuery, name, existing,
                entity => IsPlaceableNetworkPrefab(entity) && ClassifyNetPrefab(entity) == wanted);
        }

        private float OsmUtilityHeight(Entity prefab, float3 position, ref TerrainHeightData terrain)
        {
            Bounds1 range = EntityManager.GetComponentData<PlaceableNetData>(prefab).m_ElevationRange;
            float step = World.GetOrCreateSystemManaged<Game.Tools.NetToolSystem>().elevationStep;
            float depth = math.clamp(-step, range.min, range.max);
            if (depth >= 0f) throw new FormatException("The utility asset has no underground placement depth. Choose a buried asset.");
            return TerrainUtils.SampleHeight(ref terrain, position) + depth;
        }

        private Entity FindOsmInfrastructureAsset(EntityQuery assets, string name, Entity existing, Func<Entity, bool> matches)
        {
            PrefabSystem prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            if (existing != Entity.Null)
            {
                Entity current = EntityManager.GetComponentData<PrefabRef>(existing).m_Prefab;
                if ((name == null || PrefabDisplayName(prefabs, current) == name) && matches(current)) return current;
            }
            using (NativeArray<Entity> entities = assets.ToEntityArray(Allocator.Temp))
            {
                string bestName = null;
                Entity best = Entity.Null;
                foreach (Entity prefab in entities)
                {
                    if (IsLocked(prefab) || !matches(prefab) || !EntityManager.HasComponent<UIObjectData>(prefab)) continue;
                    string candidate = PrefabDisplayName(prefabs, prefab);
                    if (name != null && name != candidate) continue;
                    if (best != Entity.Null && string.CompareOrdinal(candidate, bestName) >= 0) continue;
                    bestName = candidate;
                    best = prefab;
                }
                if (best != Entity.Null) return best;
            }
            throw new FormatException($"No unlocked city asset matches the mapped feature{(name == null ? "." : $" '{name}'.")}");
        }

        private OsmNode ReadOsmStop(Entity entity, Bounds2 district, PrefabSystem prefabs, Game.UI.NameSystem names)
        {
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (!IsMenuCurbObject(prefab) || !EntityManager.HasComponent<TransportStopData>(prefab)) return null;
            float3 position = EntityManager.GetComponentData<Transform>(entity).m_Position;
            if (!MathUtils.Intersect(district, position.xz)) return null;
            string mode = StopMode(prefab);
            if (mode == null) return null;
            OsmNode node = CityOsm.ToNode(CityOsm.NativeId(entity.Index), position);
            node.Tags["public_transport"] = "platform";
            node.Tags[mode] = "yes";
            node.Tags["airimayor:entity"] = OsmEntityIdentity(entity);
            node.Tags["airimayor:prefab"] = PrefabDisplayName(prefabs, prefab);
            if (names.TryGetCustomName(entity, out string name)) node.Tags["name"] = name;
            return node;
        }

        private string StopMode(Entity prefab)
        {
            switch (EntityManager.GetComponentData<TransportStopData>(prefab).m_TransportType)
            {
                case TransportType.Bus: return "bus";
                case TransportType.Tram: return "tram";
                case TransportType.Taxi: return "taxi";
                default: return null;
            }
        }

        private bool TryPlanOsmStop(OsmElement original, OsmElement edited,
            CityOsmGeometry before, CityOsmGeometry after,
            List<OsmBuildingPlacement> buildings, List<Entity> deletions)
        {
            if (!(original is OsmNode || edited is OsmNode)) return false;
            var previous = original as OsmNode;
            var node = edited as OsmNode;
            if (CityOsmTransit.StationOwned(previous) || CityOsmTransit.StationOwned(node))
                throw new FormatException($"Boarding point {(previous ?? node).Id} belongs to its station.");
            if (!CityOsm.IsPlatform(previous) && !CityOsm.IsPlatform(node)) return false;
            Entity existing = OsmOriginal(previous);
            if (previous != null && (!CityOsm.IsPlatform(previous) || !TransitStopQuery.MatchesIgnoreFilter(existing)))
                throw new FormatException($"Node {previous.Id} is not an existing roadside stop.");
            if (node == null)
            {
                deletions.Add(existing);
                return true;
            }
            CityOsmEdit.CheckEditedTags(previous, node, "name", "public_transport", "highway", "bus", "tram", "taxi", "ele", "airimayor:prefab");
            string mode = CityOsmInfrastructure.PlatformMode(node);
            Entity prefab = FindOsmInfrastructureAsset(CurbObjectPrefabQuery, CityOsm.Tag(node, "airimayor:prefab"), existing,
                asset => IsMenuCurbObject(asset) && EntityManager.HasComponent<TransportStopData>(asset) && StopMode(asset) == mode);
            float3 point = after.Positions[node.Id];
            if (node.Tags.ContainsKey("ele") && !math.isfinite(point.y)) throw new FormatException($"Stop {node.Id} needs a numeric elevation.");
            var placement = new OsmBuildingPlacement { WayId = node.Id, Original = existing, Prefab = prefab };
            placement.Name = OsmRequestedName(previous, node);
            if (previous != null && CityOsmEdit.SameNodePosition(previous, node) && EntityManager.HasComponent<Game.Objects.Attached>(existing))
            {
                Transform pose = EntityManager.GetComponentData<Transform>(existing);
                placement.AttachParent = EntityManager.GetComponentData<Game.Objects.Attached>(existing).m_Parent;
                placement.Position = pose.m_Position;
                placement.Rotation = pose.m_Rotation;
            }
            else
            {
                FindOsmStopCurb(point, before.Document, after.Document, ref placement);
            }
            buildings.Add(placement);
            return true;
        }

        private void FindOsmStopCurb(float3 point, OsmDocument original, OsmDocument edited, ref OsmBuildingPlacement placement)
        {
            const float curbSnapMeters = 8f;
            float closest = curbSnapMeters;
            float3 outward = default;
            foreach (OsmWay way in original.Ways.Values)
            {
                if (!way.Tags.ContainsKey("highway") || !edited.Ways.ContainsKey(way.Id)) continue;
                Entity edge = OsmOriginal(way);
                if (!EntityManager.HasComponent<EdgeGeometry>(edge) || !AcceptsOsmStop(edge, placement.Prefab)) continue;
                EdgeGeometry geometry = EntityManager.GetComponentData<EdgeGeometry>(edge);
                foreach (Segment segment in new[] { geometry.m_Start, geometry.m_End })
                {
                    foreach (bool left in new[] { true, false })
                    {
                        Bezier4x3 side = left ? segment.m_Left : segment.m_Right;
                        MathUtils.Distance(side.xz, point.xz, out float t);
                        float3 curb = MathUtils.Position(side, t);
                        float distance = math.isfinite(point.y) ? math.distance(point, curb) : math.distance(point.xz, curb.xz);
                        if (distance > closest || (distance == closest && placement.AttachParent != Entity.Null)) continue;
                        closest = distance;
                        placement.AttachParent = edge;
                        placement.Position = curb;
                        outward = curb - MathUtils.Position(left ? segment.m_Right : segment.m_Left, t);
                    }
                }
            }
            if (placement.AttachParent == Entity.Null)
                throw new FormatException($"Stop {placement.WayId} needs a mapped road curb within {curbSnapMeters} meters.");
            placement.Rotation = quaternion.RotateY(math.atan2(outward.x, outward.z));
        }

        private bool AcceptsOsmStop(Entity edge, Entity prefab)
        {
            if (!EntityManager.HasBuffer<Game.Net.SubLane>(edge)) return false;
            var requirements = EntityManager.GetComponentData<NetObjectData>(prefab);
            var flags = EntityManager.GetComponentData<PlaceableObjectData>(prefab).m_Flags;
            bool needsFootpath = (flags & Game.Objects.PlacementFlags.RequirePedestrian) != 0;
            RoadTypes roads = RoadTypes.None;
            TrackTypes tracks = TrackTypes.None;
            foreach (Game.Net.SubLane part in EntityManager.GetBuffer<Game.Net.SubLane>(edge, true))
            {
                Entity lane = part.m_SubLane;
                if (!EntityManager.HasComponent<PrefabRef>(lane)) continue;
                Entity type = EntityManager.GetComponentData<PrefabRef>(lane).m_Prefab;
                if (EntityManager.HasComponent<Game.Net.CarLane>(lane) && EntityManager.HasComponent<CarLaneData>(type))
                    roads |= EntityManager.GetComponentData<CarLaneData>(type).m_RoadTypes;
                if (EntityManager.HasComponent<Game.Net.TrackLane>(lane) && EntityManager.HasComponent<TrackLaneData>(type))
                    tracks |= EntityManager.GetComponentData<TrackLaneData>(type).m_TrackTypes;
                if (EntityManager.HasComponent<Game.Net.PedestrianLane>(lane)) needsFootpath = false;
            }
            return !needsFootpath && (roads & requirements.m_RequireRoad) == requirements.m_RequireRoad
                && (requirements.m_TrackPassThrough == TrackTypes.None || (tracks & requirements.m_TrackPassThrough) != 0);
        }
    }
}
