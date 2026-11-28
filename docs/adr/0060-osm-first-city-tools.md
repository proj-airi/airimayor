# Use the local OSM map as the editing interface for city places

An agent can design a whole block when it can read, change and compare the same objects. Extend the coordinate-addressed [local OSM map](./0059-coordinate-addressed-osm-editing.md) with the editable state of those objects. Keep the game authoritative for the result. The construction asset catalog remains an independent read.

## What the agent reads

The complete district file carries geometry, shared nodes, editable properties and relationships. An existing road way identifies one native segment. Its local OSM ID supports references between elements. Its read-only `airimayor:entity` carries the native index and generation for this city load. Splitting a road can produce several segments. The edit result should associate the submitted reference with all realized references in the city's readback so the agent can continue editing the right pieces.

A coordinate query can also return matching places and bounds, dated observations and topology findings. Only a complete district read supplies a revision for full-file replacement. Filtered or zoomed-out results cannot imply deletion by omission. Traffic and electricity measurements carry the observation time and the measured feature identity. Changes to those readings do not invalidate a street edit.

The construction catalog reads the game's asset menu before the player selects a site. It supports category, role, name and availability filters. Its default view omits locked designs and unique assets already placed. The full catalog retains those entries and reports their status. An exact asset name from the catalog can be used in the `airimayor:prefab` tag on a map feature.

The catalog and road editor use the same road class and default car-lane count. An unrecognized native road menu group yields an unknown class. The game's default lane composition identifies lanes that carry cars. Where it is absent, the count comes from unconditional lane sections in the road design. A placed road's OSM geometry uses the lanes of its actual composition.

## What the agent edits

| Legacy capability | Representation and native effect |
| --- | --- |
| `demolish` | Delete the mapped feature. Check the player's demolition permission for a physical removal, including when construction is disabled. Preserve other users of a shared junction. |
| `set_road_features` | Edit properties on the existing directed road way. Resolve left and right in node order. Preserve the opposite side, center treatments and native flags outside the edit. |
| `zone` | Draw or edit a closed area carrying the assigned game zone. Resolve the theme and unlocked zone, apply it to eligible frontage cells and read back the cells actually assigned. Clearing a zone and demolishing a building are separate effects. |
| `add_transit_line`, `remove_transit_line` | Edit a route relation with ordered boarding points. Expose station boarding points as well as roadside platforms. The game validates the passenger mode, resolves the path and closes the loop. Deleting the relation withdraws the service while retaining its stops. |
| `expand_operational_area` | Link the facility to its area polygon. Expand the closed way and keep the building-side edge fixed. Check land ownership, obstacles and productive resources. Return the perimeter that the game built. |
| Install a facility upgrade | Add the selected upgrade as a tag on the existing facility way. The parent lot determines the attachment and native placement. Check the preview and read the installed upgrade from the facility. The service-state tool controls installed upgrades. |
| `list_buildings`, `list_networks` | Search placed features by geography, kind, service role and capability. Rank traffic or electrical load on request and return OSM references and bounds for the next edit. |
| `inspect_network_topology` | Return findings at the implicated ways and nodes. Compute reachability against the city graph before filtering by the requested bounds. A road that reaches an outside connection through another district is connected. |

Standalone footways use `highway=footway`. Standalone tracks use `railway=rail`, `railway=tram`, or `railway=subway`. Resolve their assets from the native path or track type. Keep road traffic and road-side upgrades on streets.

Standalone trees use [`natural=tree`](https://wiki.openstreetmap.org/wiki/Tag:natural=tree). Standalone street lamps use [`highway=street_lamp`](https://wiki.openstreetmap.org/wiki/Tag:highway=street_lamp). Give each point its own node. Lights and trees that belong to a street remain part of that street.

An administrative district uses a [boundary relation](https://wiki.openstreetmap.org/wiki/Relation:boundary) with `boundary=administrative`. Its `outer` ways form one closed ring. The game stores the name and polygon. Use the district relation to create, rename, redraw or remove the area.

Use established OSM semantics when they describe the feature precisely. Use `airimayor:*` for game-specific facts such as the legally assigned game zone and an operational area's owner. A route uses the [ordered members of an OSM route relation](https://wiki.openstreetmap.org/wiki/Relation:route). [Road sides](https://wiki.openstreetmap.org/wiki/Forward_%26_backward,_left_%26_right) follow the way's direction.

Check the effects of the complete diff against player permissions before work starts. Withdrawing a transit service uses the existing construction permission. A mixed physical build and demolition requires both permissions. Keep partial cross-boundary relations read-only and return the bounds needed to read the complete editable object. When a combined edit requires several native stages, check dependencies before the first stage and report the realized objects and any outstanding work from a fresh read if application stops partway.
