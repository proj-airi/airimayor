# Export a read-only city map snapshot

Use the map view of `read_osm` to draw the requested city rectangle. It returns an OSM file for drawing. Use the district view to obtain the complete editable OSM file and its revision. A full replacement of a district needs its native segment identities and exact geometry. The map can divide one native segment at changes in crossing order and include observed shapes with no construction operation.

Keep bridge and tunnel classification from the game. Use the height at each crossing to order elevated or underground segments. Where one order per way cannot describe all crossings, divide the drawn ways between crossings without creating a junction between crossing roads. Represent each elevated span with a [bridge outline](https://wiki.openstreetmap.org/wiki/Tag:man_made%3Dbridge) derived from the game's left and right edge curves. Its relative layer matches the way it carries.

Sample the game's water surface above the shoreline depth threshold. The drawn water follows the sampled shore and closes at the requested rectangle or the edge of known data. Use [`natural=water`](https://wiki.openstreetmap.org/wiki/Tag:natural%3Dwater) and multipolygon holes for islands. The game simulation supplies the shape. The map gives the observed water surface without assigning a named river, lake or coastline.

Record terrain next to `map.osm` as `terrain.json`. The heightfield layout uses city meters, includes its bounds and grid spacing, and contains at most 193 columns and 193 rows. Height values use the game's vertical coordinate. Samples follow increasing X within each row and increasing Z between rows. The map result describes the available terrain file and both files can be downloaded from the recording.

The map view has no district revision. The editor rejects a map snapshot or its generated features. Drawings and local layer changes leave the game's roads and the editable district's revision intact.
