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

- Offline replay performance: reopen the long recording in the rebuilt viewer; Play/Pause responds promptly, event scrolling remains continuous, and turn/run/model/tool groups can be expanded and zoomed without blocking the page.

- Native Harness ([0048](./adr/0048-native-maf-harness.md)): on a new city, pointed places work through the selected provider connection. Applying connection/model/permission settings opens a new chat.
- Provider connections ([0051](./adr/0051-models-dev-catalog-routing.md)): in native settings, Built-in mayor and External agent expose their own fields, and city permissions occupy a separate tab. Models.dev populates the provider/model lists without a fixed provider whitelist. Switching providers preserves each local key/selection/override after Apply and game restart. Switching models automatically changes protocol, address, context/output limits and image capability; manual protocol/address/context choices remain explicit overrides. Refresh model catalog preserves the selected model and running chat; an offline restart uses the validated cache, and a failed refresh keeps it. A cold offline install can configure a custom service. Check server models filters only its source connection; a late result cannot populate another connection. Manual model IDs survive refresh. Editing, refreshing or testing leaves the applied connection intact until Apply; a removed model stays selected and reports an actionable error. On a new city, OpenAI Responses and Anthropic Messages each answer and complete a city-tool round trip including a city image; text-only models omit visual tools. Go retains its stable conversation header. Version-1 connections require re-entry.
- External mayor ([0049](./adr/0049-registry-external-mayors.md)): on a new city, choose External agent and the Registry populates the Agent list; Refresh agents preserves the selected id and a failed refresh keeps the cached list. Select OpenCode and Codex in turn: first use prepares the registry version, the agent waits for Send, answers in chat, a city tool runs, and Stop cancels the turn. Completed replies stay idle; Built-in remains the default. The prompt directs city-only work; no agent-specific permissions are injected and ACP permission requests are not filtered by tool name. Leaving the city closes package-runner descendants as well as the parent agent process.
- External tool images: on a new city with an external agent, one `map_image` and one `screenshot` show a thumbnail above the text result.
- External owned conversations: on a new city with OpenCode, the agent delegates through the city task tools, the root turn continues, Stop leaves that task running, and its report returns on the root chat. The composer talks only to the root. City-only guidance reaches the child; its completion or failure ends the running row without a tool-name permission filter.
- Built-in background tasks: on a new city, native start/wait/results retrieves one child's city findings. Completion does not wake an idle root; Stop leaves the child running, and leaving the city stops it. A failed child can continue without repeating a completed city write.
- Specialized-industry loop from hub through extractor area to vehicles and production — not yet proven in a live city.
 - Traffic governance as a product loop. The accepted-run intervention persisted, but traffic notifications stayed at 2 and the same-road congestion/volume aggregates worsened after one simulated hour.
  - Native road facts (`roadClass`/`speedKmh`/`carLanes`/`highwayRules`/`zonable` on `list_prefabs`/`list_networks`/`map_text`, category-first `map_image` style, `flowPercent` on `traffic`): live values observed (Small Road 33 vs wiki Two-Lane 30, highway 100 matches); still needs UI group names, ramp lane counts, highway rendering, and the 70 flow target.
- Chat UI rewrite (docked panel with en-US/zh-HANS chrome, bindings catalog, event-sourced transcript, tool rows): Windows `dotnet build` + webpack/`tsc` pass; needs in-game look/feel acceptance — docking offsets, stick-to-bottom scroll, live tool running/done rows, composer focus vs game hotkeys, and a language-switch check.
- `map_image` road width now scales from native meters with the frame (zoomed roads match building lots; citywide stays near one pixel). Citywide and bounded site PNGs now exist; still needs the visual width comparison.
- OSM Carto `map_image` (rails/metro/tram as tracks and visible over water, building/park fills, tunnel vs ground vs bridge; names stay in `map_text`; no stop-to-stop transit overlay). Citywide and zoomed site PNGs now exist; still needs the visual inspection.
- Plan: on a new city, external ACP plan updates show priority and in-progress state.
- Simulation clock: with an external OpenCode mayor, an advance of 2 in-game hours returns after those hours elapse with `completed` set.
- Service fee prices (`list_fees` / `set_budget` kind=fee): `list_fees` read already observed (minimum 0, electricity ordinary price with full range). Still needs: on a new city, writing electricity's ordinary price succeeds and the panel shows that price.
- Network connection shapes: on a new city, a simple curve between two existing road nodes shares those nodes; a parallel copy sits beside the source path; a ground route that would need a bridge is still rejected.
- Connection tests: on the main menu, Test connection uses the edited provider/model and reports HTTP status and the provider error without logging the API key. Test ACP uses the edited external agent and reports a one-sentence reply or a concrete failure (command not found, process exit, handshake, not signed in, or no reply). Neither test applies settings or changes an active city's chat.
- `replace_road_type`: on a new city, two points on one road replace that edge or chain (at most 64) with another road prefab; points that are not connected are rejected with the parallel-road error; a hard building is named and a growable is left for the game to clear.
- Panel control: with `-uiDeveloperMode`, on a new city, snapshot then click/fill/press drives a flat panel; Send and Stop in the mayor chat are not pressed; with the flag absent, the command and the test say the launch flag is required. The setting does not open port 9444.
- Pointed places: on a new city, point at a building and a road; the sent message shows those cards and the city shows no marker. Move the camera away and click a card from that message: the view returns to the building or the road's midpoint. Demolish that building and click its card: the camera stays, the card name stays, and the click tells the player it cannot be found. A ground point with an empty composer still sends, and a later click on that card returns to the same coordinates. After pointing, the agent can still place a building.
- Session record ([0052](./adr/0052-trace-kit-session-records.md)): on a new city, native child calls have their own session/run identities and origin after the root stops, and their background branches are inspectable in the offline viewer. With an external OpenCode agent, turn replies and `acp.tool` events remain, with external model internals declared unavailable. After an abrupt game-process interruption, the recording stays visibly incomplete and its flushed output remains viewable.
- Roadside objects: on a new city, `place_building` puts a bus stop on the near side of a road; `list_transit_stops` shows it and `add_transit_line` can use it. A mailbox or bicycle rack placed the same way sits on the roadside.
