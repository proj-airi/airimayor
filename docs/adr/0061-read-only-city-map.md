# Export a read-only city map snapshot

Use the map view of `read_osm` to draw the requested city rectangle. It returns an OSM file for drawing. Use the district view to obtain the complete editable OSM file and its revision. A full replacement of a district needs its native segment identities and exact geometry. The map can divide one native segment at changes in crossing order and include observed shapes with no construction operation.

Keep bridge and tunnel classification from the game. Use the height at each crossing to order elevated or underground segments. Where one order per way cannot describe all crossings, divide the drawn ways between crossings without creating a junction between crossing roads. Represent each elevated span with a [bridge outline](https://wiki.openstreetmap.org/wiki/Tag:man_made%3Dbridge) derived from the game's left and right edge curves. Its relative layer matches the way it carries.

The map view records `map.osm`. It has no district revision. The editor rejects a map snapshot or its generated features. Drawings and local layer changes leave the game's roads and the editable district's revision intact.
