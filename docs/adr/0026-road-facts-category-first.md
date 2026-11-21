# Native road facts; category first, width never promotes

Status: accepted. [0027](./0027-tool-surface-renaming.md) renamed the construction tool. [0060](./0060-osm-first-city-tools.md) moved spatial reads to local OSM and revised how the catalog and map resolve road classes and lane counts.

`list_prefabs`, `list_networks`, `map_text` and `map_image` share one road
identity: UI-group category, speed limit, car-lane count, highway-rules
flag, zoning flag (`RoadFactsMath` + one ECS reader). The native UI group
decides Highways membership; prefab name only covers group-less
nets as a best-effort fallback. Width never decides: without a group
or a highway name the answer stays unknown.

This beat the width heuristic (name contains Highway, else width 24/16/10):
a live run served a 24 m Medium Road that the old rule painted as a
highway, while a narrow 1-lane Highway Ramp painted as a minor street.
`flowPercent` joins the existing `traffic` object on `list_networks`
instead of a new traffic tool, per the traffic-governance constraint.
`build_road` stays permissive: a small street may join a highway where
native validation allows it; the playbook prefers ramps and collectors
and verifies flow at 70 or above.

Speed display is baked x 1.5: the authoring unit is half-km/h
(RoadBuilder shows SpeedLimit/2 as km/h and round-trips it raw into
RoadPrefab.m_SpeedLimit) while baking stores authoring/3. A 66.67
highway reads 100, a 27.78 medium road reads ~42. Car lanes count
unconditional section pieces via NetPieceLanes, the same native shape
RoadBuilder traverses; UI groups resolve through PrefabSystem as
UIGroupPrefab (RoadsHighways, RoadsSmallRoads, ...).
