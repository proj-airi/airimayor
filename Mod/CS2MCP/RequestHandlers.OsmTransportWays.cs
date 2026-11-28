using Game.Net;
using Game.Prefabs;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private TypedNetworkKinds OsmTransportPrefabKind(Entity prefab)
        {
            if (EntityManager.HasComponent<RoadData>(prefab)) return TypedNetworkKinds.None;
            if (EntityManager.HasComponent<TrackData>(prefab))
            {
                return EntityManager.GetComponentData<TrackData>(prefab).m_TrackType switch
                {
                    TrackTypes.Train => TypedNetworkKinds.Rail,
                    TrackTypes.Tram => TypedNetworkKinds.Tram,
                    TrackTypes.Subway => TypedNetworkKinds.Subway,
                    _ => TypedNetworkKinds.None,
                };
            }
            return EntityManager.HasComponent<PathwayData>(prefab) ? TypedNetworkKinds.Footway : TypedNetworkKinds.None;
        }

        private Entity ResolveOsmTransportWay(OsmWay original, OsmWay edited, Entity existing, TypedNetworkKinds kind)
        {
            CityOsmEdit.CheckEditedTags(original, edited, kind == TypedNetworkKinds.Footway ? "highway" : "railway",
                "bridge", "tunnel", "layer", "airimayor:prefab");
            return FindOsmInfrastructureAsset(NetPrefabQuery, CityOsm.Tag(edited, "airimayor:prefab"), existing,
                asset => IsPlaceableNetworkPrefab(asset) && OsmTransportPrefabKind(asset) == kind);
        }
    }
}
