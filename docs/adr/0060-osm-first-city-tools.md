# Use the local OSM map as the editing interface for city places

An agent can design a whole block when it can read, change and compare the same objects. Extend the coordinate-addressed [local OSM map](./0059-coordinate-addressed-osm-editing.md) with the editable state of those objects. Keep the game authoritative for the result. The ten legacy tools listed below are in scope for retirement or migration.

## What the agent reads

The complete district file carries geometry, shared nodes, editable properties and relationships. An existing road way identifies one native segment. Its local OSM ID supports references between elements. Its read-only `airimayor:entity` carries the native index and generation for this city load. Splitting a road can produce several segments. The edit result should associate the submitted reference with all realized references in the city's readback so the agent can continue editing the right pieces.

A coordinate query can also return matching places and bounds, dated observations, topology findings, and build options on request. Only a complete district read supplies a revision for full-file replacement. Filtered or zoomed-out results cannot imply deletion by omission. Traffic and electricity measurements carry the observation time and the measured feature identity. Changes to those readings do not invalidate a street edit. Build options come from the loaded city's prefab catalog, including assets without a placed instance and their lock state. An empty rectangle can still offer building choices.

## What the agent edits

| Legacy capability | Representation and native effect |
| --- | --- |
| `demolish` | Delete the mapped feature. Check the player's demolition permission for a physical removal, including when construction is disabled. Preserve other users of a shared junction. |
| `set_road_features` | Edit properties on the existing directed road way. Resolve left and right in node order. Preserve the opposite side, center treatments and native flags outside the edit. |
| `zone` | Draw or edit a closed area carrying the assigned game zone. Resolve the theme and unlocked zone, apply it to eligible frontage cells and read back the cells actually assigned. Clearing a zone and demolishing a building are separate effects. |
| `add_transit_line`, `remove_transit_line` | Edit a route relation with ordered boarding points. Expose station boarding points as well as roadside platforms. The game validates the passenger mode, resolves the path and closes the loop. Deleting the relation withdraws the service while retaining its stops. |
| `expand_operational_area` | Link the facility to its area polygon. An edit can supply an expanded boundary or a requested total area for the existing planner. Keep the building-side edge fixed. Account for ownership, obstacles and productive resources, then return the realized boundary. |
| `list_buildings`, `list_networks` | Search placed features by geography, kind, service role and capability. Rank traffic or electrical load on request and return OSM references and bounds for the next edit. |
| `inspect_network_topology` | Return findings at the implicated ways and nodes. Compute reachability against the city graph before filtering by the requested bounds. A road that reaches an outside connection through another district is connected. |
| `list_prefabs` | Offer searchable build options by role, name and native properties. Allow an exact prefab choice and report availability even where the asset has never been built. |

Use established OSM semantics when they describe the feature precisely. Use `airimayor:*` for game-specific facts such as the legally assigned game zone and an operational area's owner. A route uses the [ordered members of an OSM route relation](https://wiki.openstreetmap.org/wiki/Relation:route). [Road sides](https://wiki.openstreetmap.org/wiki/Forward_%26_backward,_left_%26_right) follow the way's direction.

Check the effects of the complete diff against player permissions before work starts. Withdrawing a transit service uses the existing construction permission. A mixed physical build and demolition requires both permissions. Keep partial cross-boundary relations read-only and return the bounds needed to read the complete editable object. When a combined edit requires several native stages, check dependencies before the first stage and report the realized objects and any outstanding work from a fresh read if application stops partway.
