# Read and edit a city district by coordinates

The mayor reads the complete local OSM file for a rectangle in city meters. The city's network and static-object spatial indexes find candidate features. A geometry check selects the roads, standalone utility lines, building lots and roadside platforms that intersect the rectangle. This gives overlapping reads the same city object IDs and lets the mayor create the first street on empty land.

The mayor edits that rectangle with its revision and either a complete OSM document or an OsmChange patch. A revision identifies the loaded city, rectangle and exported content. A complete replacement can delete features by omission. A patch can describe a small change without resending the district. Both feed one compilation and validation path. A directory of named files would need an additional rule for which file owns a road on a boundary.

Resolve edits against the loaded city. Preserve shared junctions, reject changes outside the selection, check the player's permissions, and preview all native definitions together. Recheck the district before applying and obtain the returned file from the city after application. An unsupported change or failed preview reports the affected feature. When native readback fails after application, a fresh read establishes what actually changed. Native behavior must be accepted on a new city.

This extends the conversion in [0058](0058-local-osm-city-geometry.md) and supersedes the game map tools in [0017](0017-map-text-map-image-split.md) and [0020](0020-osm-carto-map-image.md). Historical recordings can keep their attachments.
