# Level terrain with an OSM edit

Use one new closed OSM way with `area=yes`, `airimayor:terrain=level`, and `airimayor:target_height` in city meters. The way draws the proposed grading area. Game elevations, water observations and the map's recorded heightfield describe the result of city simulation.

Preview the grading with `edit_osm` and the editable district revision. The preview reports the native brush footprint and the current elevations in the proposed level area. It issues a terrain revision for that proposal and the native ground samples in its affected area. Submit the same proposal with that terrain revision to apply. Recheck the construction permission, district revision and terrain revision before the first stroke.

Use a polygon mask with an inward transition in the native Level brush. The native tool checks the brush footprint against the city's boundaries and unlocked tiles. Read actual elevations after applying the brush and report measured error from the requested height. Report partial progress explicitly if the ground has already changed when the job stops.

Submit terrain grading separately. Road and building placement samples the current ground before making native definitions. Read the resulting district before placing assets on the graded ground.
