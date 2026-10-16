# Open work

Current inventory. Vocabulary: [CONTEXT.md](../CONTEXT.md). Decisions: [adr/](./adr/). How to update this tree: [AGENTS.md](./AGENTS.md). Frozen audits stay in dated `ops/` files and are not this list.

## Not implemented

Code still missing.

- AIRI plugin
- `map_image` hillshade
- `map_image` POI icon set
- Pedestrian/cycle-specific map classes
- Zoning-cell landuse fills (volume)

## Awaiting live acceptance

Code exists; a previous save is not the final gate. Close the game before DLL redeploy. Mac cannot `dotnet build` without `CSII_TOOLPATH`; Windows compile is a gate before live acceptance.

- Agent runtime split: on a new city, send, interrupt, and the plan strip match the previous loop. A player message waits out an in-progress time advance. Interrupt cancels that advance and restores the clock.
- External mayor: on a new city, Agent set to OpenCode answers in chat, a city tool runs, Continue opens another turn, and Stop cancels the turn. Built-in remains the default. An OpenCode turn does not run `bash`, `read`, `grep`, or any tool whose name is not `city_`.
- External tool images: on a new city with an external agent, one `map_image` and one `screenshot` show a thumbnail above the text result.
- Owned conversations: on a new city, once with Built-in and once with OpenCode, the agent delegates one task, the root turn can continue, Stop does not interrupt that task, and the report returns on the root chat. The composer still only talks to the root. An OpenCode conversation that asks to read outside the city ends instead of staying on 运行中.
- Specialized-industry loop from hub through extractor area to vehicles and production — not yet proven in a live city.
 - Traffic governance as a product loop. The accepted-run intervention persisted, but traffic notifications stayed at 2 and the same-road congestion/volume aggregates worsened after one simulated hour.
  - Native road facts (`roadClass`/`speedKmh`/`carLanes`/`highwayRules`/`zonable` on `list_prefabs`/`list_networks`/`map_text`, category-first `map_image` style, `flowPercent` on `traffic`): live values observed (Small Road 33 vs wiki Two-Lane 30, highway 100 matches); still needs UI group names, ramp lane counts, highway rendering, and the 70 flow target.
- Responses API path (`GetResponsesClient().AsIChatClient()`): first live run clean on Responses + muse-spark via Console Go (tool calls fine, 0 errors); needs a long multi-turn run before it shares default-path status with Chat Completions.
- Chat UI rewrite (docked panel with en-US/zh-HANS chrome, bindings catalog, event-sourced transcript, tool rows): Windows `dotnet build` + webpack/`tsc` pass; needs in-game look/feel acceptance — docking offsets, stick-to-bottom scroll, live tool running/done rows, composer focus vs game hotkeys, and a language-switch check.
- Chat tool images: the preview keeps its ratio from the JPEG header in the page. Non-square screenshot (16:9) and map image artifacts now exist; still needs the in-game preview ratio check.
- Responses API with vision (no per-turn fallback by decision): first live Responses run attached `map_image`/`screenshot` images with clean following turns; still needs a long vision run before it replaces Chat Completions as the vision path.
- `map_image` road width now scales from native meters with the frame (zoomed roads match building lots; citywide stays near one pixel). Citywide and bounded site PNGs now exist; still needs the visual width comparison.
- OSM Carto `map_image` (rails/metro/tram as tracks and visible over water, building/park fills, tunnel vs ground vs bridge; names stay in `map_text`; no stop-to-stop transit overlay). Citywide and zoomed site PNGs now exist; still needs the visual inspection.
- Thinking-model `reasoning_content` echo (request pipeline re-attaches stored reasoning to Chat Completions assistant messages): echo pipeline already active on a live non-thinking run; still needs a live thinking-model run proving the turn after a text answer no longer 400s and cache ratios stay green.
- Plan: timeline survival across autonomous continuation already observed. Still needs the chat plan strip check: steps after `set_plan`, and the empty state when there is no plan, not a transcript line.
- Simulation clock: `get_simulation` reads pause and speed; `set_simulation` advance (1–8 in-game hours) restores the previous clock, and pause sets the city paused. Pause during an advance hands the clock back immediately. Paused-start advance with `completed` already observed. Still needs a positive speed other than 8 running until the requested hours elapse. On OpenCode, an advance of 2 in-game hours returns after those hours elapse with `completed` set.
- Service fee prices (`list_fees` / `set_budget` kind=fee): `list_fees` read already observed (minimum 0, electricity ordinary price with full range). Still needs: on a new city, writing electricity's ordinary price succeeds and the panel shows that price.
- Network connection shapes: on a new city, a simple curve between two existing road nodes shares those nodes; a parallel copy sits beside the source path; a ground route that would need a bridge is still rejected.
- Connection tests: on the main menu, Test connection reports HTTP status and the provider error without logging the API key, and Test ACP reports a one-sentence reply or a concrete failure (command not found, process exit, handshake, not signed in, or no reply). Neither test applies settings.
- `replace_road_type`: on a new city, two points on one road replace that edge or chain (at most 64) with another road prefab; points that are not connected are rejected with the parallel-road error; a hard building is named and a growable is left for the game to clear.
- Codex row: on a new city, Agent set to Codex launches the installed `codex-acp` adapter, answers in chat, and a city tool runs. It does not run a shell or read or write files. If the adapter asks to sign in, Test ACP says not signed in and includes the agent's sentence.
- Panel control: with `-uiDeveloperMode`, on a new city, snapshot then click/fill/press drives a flat panel; Send and Stop in the mayor chat are not pressed; with the flag absent, the command and the test say the launch flag is required. The setting does not open port 9444.
- Pointed places: on a new city, point at a building and a road; the sent message shows those cards and the city shows no marker. Move the camera away and click a card from that message: the view returns to the building or the road's midpoint. Demolish that building and click its card: the camera stays, the card name stays, and the click tells the player it cannot be found. A ground point with an empty composer still sends, and a later click on that card returns to the same coordinates. After pointing, the agent can still place a building.
- Session record ([0046](./adr/0046-session-record.md)): on a new city, `ModsData\airimayor\sessions\<started>--<session>\` holds `session.json` with head, mod version, and city, and gets `ended` after leaving to the main menu. A `screenshot` and a `map_image` call each leave `calls/<call>/image.png` (the map also `meta.json` and `source.geojson`). A delegated task leaves its record under `conversations/` with the parent session and turn. Every timeline line has `schema`, `seq`, `session`, and `clock` with a frame and in-game time; a turn's lines carry its turn and later city lines carry none. A `build_network` call writes `function.start` and then `function` with the same call id, a measured `queuedMs`, and `submitted`/`applied`/`completed` anchors whose frames do not decrease. `model-calls.jsonl` has one line per generation and per compaction, and each `generation` names its model call. The system prompt appears once under `blobs/` and the tool list under `catalogs/`. With OpenCode, the timeline has `turn.start`/`turn.finish` with a stop reason and reply, `function` events for city tools, and `acp.tool` rows, and `session.json` lists the external gaps.
- Roadside objects: on a new city, `place_building` puts a bus stop on the near side of a road; `list_transit_stops` shows it and `add_transit_line` can use it. A mailbox or bicycle rack placed the same way sits on the roadside.
