# Exchange road and building geometry as a local OSM map

Use an OSM 0.6 document with shared node references for a district's roads and building footprints. Keep general document editing separate from the game's road and building conversion so standard tags and relations can survive local edits and existing native curves and poses can survive an unchanged export and import.

The native extract uses a fictional equatorial origin and records the entity index and generation together. Node elevations and the original cubic carry the road's height profile. Bridge and tunnel tags distinguish their grade from ground. A building way follows the oriented lot boundary. Other OSM features can pass through the document without acquiring a corresponding game object.

This supersedes the individual placement, network construction and road replacement tools in [0003](0003-one-step-building-placement.md), [0004](0004-linear-networks.md), [0041](0041-network-connection-shapes.md) and [0044](0044-roadside-objects-share-place-building.md). Conversion produces city geometry. Applying it to a loaded city requires native construction, validation and readback on the simulation thread.
