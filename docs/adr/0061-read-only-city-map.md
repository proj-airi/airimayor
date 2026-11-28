# Draw a city map from the loaded city

Use `render_city_map` to draw a north-up PNG for a rectangle in city meters. SkiaSharp draws the city using the [OpenStreetMap Standard stylesheet](https://github.com/openstreetmap-carto/openstreetmap-carto/tree/v6.1.0) as the reference for its colors, line hierarchy, symbols and zoom thresholds. Derive the scale from the rectangle. Show the image in chat and keep it in the recording. Rendering uses data from the loaded city.

Use `read_osm` to obtain the complete editable OSM district and its revision. The game reads authoritative shapes for the drawing separately. A full replacement of a district needs its native segment identities and exact geometry. The drawing can divide one native segment at changes in crossing order and include observed shapes with no construction operation.

Keep bridge and tunnel classification from the game. Use the height at each crossing to order elevated or underground segments. Where one order per way cannot describe all crossings, divide the drawn ways between crossings without connecting crossing roads at the cut. Represent each elevated span with a [bridge outline](https://wiki.openstreetmap.org/wiki/Tag:man_made%3Dbridge) derived from the game's left and right edge curves. Its relative layer matches the way it carries.

Sample the game's water surface above the shoreline depth threshold. The drawn water follows the sampled shore and closes at the requested rectangle or the edge of known data. Use [`natural=water`](https://wiki.openstreetmap.org/wiki/Tag:natural%3Dwater) and multipolygon holes for islands. The game simulation supplies the shape.

Record the drawing's `map.osm` and available `terrain.json` beside its image. The heightfield layout uses city meters, includes its bounds and grid spacing, and contains at most 193 columns and 193 rows. Height values use the game's vertical coordinate. Samples follow increasing X within each row and increasing Z between rows. The editor rejects a map snapshot or its generated features.
