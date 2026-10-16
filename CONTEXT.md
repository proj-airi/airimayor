# AIRI Mayor

The in-game AI mayor: a Gameface chat UI talks to a C# loop that enqueues construction and city tools onto the simulation thread. The default mayor runs in the game and uses an API key. An optional external mayor is a separate agent process.

## Language

### Runtime

**Agent**:
The in-game mayor session: one chat, tools queued onto the simulation thread. The default head runs in the game. An external head launches OpenCode.
_Avoid_: MCP client, apeira

**External mayor**:
An agent process the chat drives with prompt, cancel, and the update stream. City tools are handed to that process as a tool server. The player signs in to that agent outside the game.
_Avoid_: a second chat, the built-in endpoint and API key, a filesystem or a terminal for the agent

**Model-facing surface**:
The tools and text the model is allowed to call or see.
_Avoid_: HTTP route, backend handler, catalog row (those may exist without being model-facing); engine system and type names; HTTP routes in descriptions or result notes; loop jargon (autonomous continuation, on the surface)

**Mayor playbook**:
The city-building playbook baked into the system prompt (city building, utility networks, transit lines).
_Avoid_: engineering skills under `.agents/` or `~/.agents/`

**Traffic governance**:
The mayor's congestion loop over existing tools: a time advance plus fresh reads plus topology QA, `list_networks` ranked by congestion or traffic volume, existing road writes, then `set_simulation` advance and re-measure.
_Avoid_: a new traffic tool, adding lanes to every local street, treating degree-1 dead ends as automatic errors

**Wait simulation**:
Reading and setting the simulation clock. `get_simulation` reads whether the city is paused and the current speed. `set_simulation` can advance in-game time and then restore the previous speed and pause, or pause the city immediately. The player owns the clock. An advance result is wait mechanics only: hours/completed/targetReached/note. It carries no city snapshot; the model builds the snapshot itself from read tools (demand, notifications, city_services, budget) after every advance.
_Avoid_: forced pause as the product runtime, polling, a nested overview/problems digest on the advance result, waitedMs, SimWait internals

**Context budget**:
How many tokens the loop treats as the window. Always the player-set WindowTokens; the loop never parses the model name. The request shape follows the player-set API kind (Chat Completions or Responses).
_Avoid_: Endpoint, provider, or model name as the source of the window

**Compaction**:
Summarizing older turns when estimated tokens reach the compact threshold.
_Avoid_: deleting the session, starting a new chat

**Owned conversation**:
A conversation the current chat opened for one task. The final text comes back on this chat. The player cannot address it.
_Avoid_: a second chat, the player sending a message to that conversation, a conversation this client does not own, a follow-up, reading or cancelling it as its own tool

**Session record**:
The directory one chat session leaves behind as evidence: its once-only facts, its timeline, its model calls, the files its tool calls returned, and the records of the conversations it owned. A record without an end was cut short. What it cannot show is named as a known gap, never filled in.
_Avoid_: agent log, timeline file (the timeline is one part), telemetry, a set of files gathered by hand for acceptance

**Write evidence**:
The city ledger read before and after a write: treasury, development points, building, network edge, and transit line counts, frame, and pause. It lives in the session record. The model never reads it. A write is judged confirmed, diverged, or undetermined from it.
_Avoid_: a snapshot on the write result, a retry trigger, proof while the simulation runs

**Plan**:
The one list of steps declared with `set_plan`. Each step has content, a priority (high, medium, or low), and a status (pending, in progress, or completed). Only another `set_plan` replaces the whole list; player messages and autonomous continuation leave it in place.
_Avoid_: a statutory plan, `goal` and `success` as the plan, compact JSON as the live plan, gating the tool catalog, the plan prompting a time advance, a live note as the plan, several plans at once

### Construction

**Prefab**:
An exact named game asset. The Agent picks one before placing or building.
_Avoid_: service `role` as a `place_building` argument

**place_building**:
The write that places one standalone prefab and pose.
_Avoid_: `find_placement`, `find_infrastructure_candidate`, preview-then-commit

**build_network**:
The write that constructs one linear connection: a straight segment, a curve that leaves and joins along existing roads, a two-part bend, or a frontage copy beside an existing road.
_Avoid_: `build_road`, `place_road`, `build_bridge`, a separate curve or parallel tool, a control point on a straight segment

**Ground**:
Default road mode: follow terrain; reject water and steep grades instead of rewriting the route.
_Avoid_: implied bridge, auto-elevate

**Grade-separated**:
Explicit road mode for a bridge, elevated road, or tunnel. The model must ask for it.
_Avoid_: `build_bridge`, silent promotion from a failed ground path

**Native validation**:
The game's ordinary placement and apply checks. The product does not bypass them.
_Avoid_: Anarchy, `force`, collision bypass

**Auto-connect**:
Placement-owned follow-up that attaches matching water, sewage, or low-voltage networks.
_Avoid_: Agent-drawn pipes or cables as the happy path

**Network**:
Roads, pipes, and cables as linear infrastructure.
_Avoid_: "road" as a synonym for every utility line

**Typed network**:
A top-level linear edge classified as road, fresh water, sewage, or low-voltage from native prefab layers. Auto-connect, list, demolish, and topology QA share that identity.
_Avoid_: a second utility-versus-road taxonomy; high-voltage as part of this set

**Isolated component**:
A connected set of typed-network edges that does not reach an outside connection (roads) or a road edge (pipes and cables).
_Avoid_: treating every degree-1 dead end as isolated

**Road facts**:
Native per-prefab road identity on `list_prefabs`, `list_networks` and `map_text`: UI-group category, speed limit, car-lane count, highway-rules flag, zoning flag.
_Avoid_: width as the highway test; a new traffic tool

**Operational area**:
An owner-linked lot polygon on a facility (storage or extractor). The current product expands only.
_Avoid_: district, a standalone area with no owner

**Specialized-industry hub**:
An independently placeable building prefab that declares an extractor Operational area.
_Avoid_: extractor facility, decorative or animated equipment inside an extraction site

**Roadside object**:
A small object that sits on the side of a road or track, such as a stop, mailbox, or bicycle rack.
_Avoid_: a road feature, a station building, a station sub-stop

**Transit stop**:
A passenger or cargo boarding object. A roadside stop is a roadside object; a station sub-stop comes with the station building. Line tools connect stops that already exist.
_Avoid_: treating the station building as the stop

**Transit line**:
An ordered loop of transit stops applied through the native route tool. Vehicles stay native.
_Avoid_: Gameface `transportLines$` as the write path; a vehicle or production tool

### Perception and authority

**Pointed place**:
A building, road, pipe, cable, or ground point the player marked from the city view, kept as a card on that message. A ground point's coordinates still frame the camera later; a building or road does while it stands, and the next click says it cannot be found once it is gone.
_Avoid_: a pin drawn on the map, a prop, a tool the Agent calls, a district, a Move It selection that relocates the object

**Clip**:
A place named inside a message that moves the camera on click. The player sends clips as cards on the message; the Agent writes them inline, and the chat renders both the same way.
_Avoid_: an attachment row, a second copy of the place list, a persistent highlight

**MAP_TEXT**:
Budgeted semantic-vector text from `map_text`. Spatial evidence, not construction approval.
_Avoid_: heightmap, 8×8 samples as the Agent interface

**Map image**:
Undistorted PNG overview from `map_image`: citywide by default, zoomed extent with x+z+radius or xMin/zMin/xMax/zMax bounds. OSM Carto look: scale-dependent density of land, water, parks, buildings, roads, and rails. Names stay in MAP_TEXT. Appears only when the player enabled visual tools.
_Avoid_: street names in the PNG; stop-to-stop transit overlays; Carto or QGIS as the product renderer; model-parsed GeoJSON; treating the map as construction approval

**Player permission**:
A durable setting that shows or hides one group of city tools. The groups are peers: construction, demolition, treasury and policy, development points, the clock, visual tools, saving, diagnostics, and panel control. A hidden tool is not offered.
_Avoid_: per-call `force`, a confirmation modal after the setting is already on, a read-only/mayor preset, one checkbox per tool

**Diagnostic tools**:
The default-off zone-block readout. Not a permission bypass.
_Avoid_: development tools, development / acceptance tools, anarchy mode

**Panel control**:
A default-off permission to press on-screen panels. It is not limited by the other permissions, and it does not draw on the city.
_Avoid_: a system shell, one tool per panel, world drag gestures
