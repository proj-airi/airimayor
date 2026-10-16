# Docs index

Vocabulary: [`CONTEXT.md`](../CONTEXT.md). Current open work: [`open-work.md`](./open-work.md). How agents write this tree: [`AGENTS.md`](./AGENTS.md). Dated notes under `guide/` / `research/` / `ops/` are evidence or history; accepted decisions live under `adr/` with sequential numbers. Frozen M1 smoke is in [`../archive/docs/`](../archive/docs/).

## ADR

| Doc | Decision |
| --- | --- |
| [0001-in-process-meai-loop.md](./adr/0001-in-process-meai-loop.md) | In-process MEAI loop; not apeira, SK, MAF, or an external process |
| [0002-native-validation.md](./adr/0002-native-validation.md) | Ordinary native validation; no Anarchy or `force` |
| [0003-one-step-building-placement.md](./adr/0003-one-step-building-placement.md) | `place_building` is the only model-facing building write |
| [0004-linear-networks.md](./adr/0004-linear-networks.md) | Linear write (`build_network` since [0027](./adr/0027-tool-surface-renaming.md)); ground vs grade-separated; no silent promotion |
| [0005-player-permissions.md](./adr/0005-player-permissions.md) | Settings, not flags (grouping superseded by [0042](./adr/0042-player-permission-groups.md)) |
| [0006-budgeted-local-map.md](./adr/0006-budgeted-local-map.md) | `terrain`/`LOCAL_MAP` superseded by 0017; now `map_text` MAP_TEXT plus separate `map_image` |
| [0007-session-lifecycle.md](./adr/0007-session-lifecycle.md) | Session follows the loaded city; data under `ModsData` |
| [0008-context-budget-auto-custom.md](./adr/0008-context-budget-auto-custom.md) | Superseded; window is always player-set WindowTokens, request shape follows player-set API kind |
| [0009-typed-network-graph.md](./adr/0009-typed-network-graph.md) | One typed network behind list, demolish, and topology QA |
| [0010-native-transit-lines.md](./adr/0010-native-transit-lines.md) | Transit lines via Route Tool apply; roadside stops are placed by [0044](./adr/0044-roadside-objects-share-place-building.md) |
| [0011-specialized-industry-hub-identity.md](./adr/0011-specialized-industry-hub-identity.md) | Specialized-industry role follows declared extractor Operational areas |
| [0012-docked-chat-transcript.md](./adr/0012-docked-chat-transcript.md) | Docked chat on `GameBottomRight` + `Portal`; bindings catalog; event-sourced transcript |
| [0013-carto-map-export.md](./adr/0013-carto-map-export.md) | `map_image` via Carto reflection; shares the vision switch |
| [0014-tool-image-preview.md](./adr/0014-tool-image-preview.md) | Tool image previews are thumbnails over the event channel |
| [0015-map-style-and-extent.md](./adr/0015-map-style-and-extent.md) | Light map style; bounded extents (style superseded by 0020) |
| [0016-map-layering-port.md](./adr/0016-map-layering-port.md) | Port road layering; label deferral superseded by 0020 |
| [0017-map-text-map-image-split.md](./adr/0017-map-text-map-image-split.md) | map_text and map_image stay separate tools |
| [0018-bounded-map-game-crs.md](./adr/0018-bounded-map-game-crs.md) | Bounded `map_image` forces the Game CRS; no Transform mirroring |
| [0019-vendored-map-source.md](./adr/0019-vendored-map-source.md) | Native map source; Carto reflection removed after acceptance |
| [0020-osm-carto-map-image.md](./adr/0020-osm-carto-map-image.md) | `map_image` targets OSM Carto (labels superseded by 0022) |
| [0021-osm-carto-stroke-painter.md](./adr/0021-osm-carto-stroke-painter.md) | `map_image` paints OSM Carto lines, not Bresenham squares |
| [0022-map-image-geometry-only.md](./adr/0022-map-image-geometry-only.md) | `map_image` is geometry; names stay in `map_text` |
| [0023-map-image-no-route-overlay.md](./adr/0023-map-image-no-route-overlay.md) | `map_image` omits stop-to-stop transit overlays |
| [0024-map-image-ways-not-grade-splits.md](./adr/0024-map-image-ways-not-grade-splits.md) | Ways are the road graph; elevation is paint |
| [0025-map-image-native-node-ways.md](./adr/0025-map-image-native-node-ways.md) | Ways walk native nodes; width is paint |
| [0026-road-facts-category-first.md](./adr/0026-road-facts-category-first.md) | Native road facts; category first, width never promotes |
| [0027-tool-surface-renaming.md](./adr/0027-tool-surface-renaming.md) | Verbs first, no shape suffixes, one finance write |
| [0028-mayor-mandate.md](./adr/0028-mayor-mandate.md) | Loop-owned city plan; no write gating; no per-turn generation cap |
| [0029-player-owned-clock.md](./adr/0029-player-owned-clock.md) | Player owns the clock; an advance restores speed/pause; no snapshot on the advance result |
| [0030-player-message-keeps-plan.md](./adr/0030-player-message-keeps-plan.md) | Player messages keep the plan; a reply stops, a tool step finishes including advance; Cancel cancels (see 0032) |
| [0031-explicit-pause.md](./adr/0031-explicit-pause.md) | `get_simulation` reads the clock; `set_simulation` can advance or pause |
| [0032-agent-runtime-tool-port.md](./adr/0032-agent-runtime-tool-port.md) | In-process loop is `AgentRuntime`; city tools are a port. An external head uses ACP and MCP ([0036](./adr/0036-external-mayor-acp.md)) |
| [0033-chat-plan-entries.md](./adr/0033-chat-plan-entries.md) | Built-in plan and the chat strip are one step list; `set_plan` replaces it and returns it |
| [0034-edge-grade-not-elevation.md](./adr/0034-edge-grade-not-elevation.md) | An edge is bridge, tunnel, or ground; an embankment stays ground |
| [0035-mod-identity-airimayor.md](./adr/0035-mod-identity-airimayor.md) | The mod ships as `airimayor`; display name `AIRI Mayor`; no migration |
| [0036-external-mayor-acp.md](./adr/0036-external-mayor-acp.md) | Optional external mayor: ACP child plus a stdio MCP server over the city tool port |
| [0037-owned-conversations.md](./adr/0037-owned-conversations.md) | Both mayors may open conversations from the current chat; the player cannot address them |
| [0038-one-task-tool.md](./adr/0038-one-task-tool.md) | One `task` tool; the report is the root agent's next turn; Stop leaves it running |
| [0039-acp-tool-preview.md](./adr/0039-acp-tool-preview.md) | External tool rows use the same in-game JPEG thumbnail, paired to the ACP tool call |
| [0040-advance-hands-over-on-pause.md](./adr/0040-advance-hands-over-on-pause.md) | An advance hands over only on pause; a lower positive speed keeps waiting |
| [0041-network-connection-shapes.md](./adr/0041-network-connection-shapes.md) | `build_network` shape is a straight segment, a tangent-locked curve, a smooth bend, or a frontage copy |
| [0042-player-permission-groups.md](./adr/0042-player-permission-groups.md) | Nine peer permission checkboxes; hidden tools stay out of the catalog |
| [0043-pointed-places.md](./adr/0043-pointed-places.md) | A pointed place is a card on the player message, not a mark drawn in the city |
| [0044-roadside-objects-share-place-building.md](./adr/0044-roadside-objects-share-place-building.md) | Roadside objects share `place_building`; the snap stays inside the write |
| [0045-message-clips.md](./adr/0045-message-clips.md) | Agent places are inline `<clip>` tags; click moves the camera, no persistent highlight |
| [0046-session-record.md](./adr/0046-session-record.md) | One self-contained directory per session; one envelope with the simulation clock; model calls and known gaps recorded |
| [0047-write-evidence.md](./adr/0047-write-evidence.md) | Before/after city ledger on writes stays in the record; an offline check judges confirmed, diverged, or undetermined |

## Guide

| Doc | Topic |
| --- | --- |
| [2026-08-06-windows-onboarding.md](./guide/2026-08-06-windows-onboarding.md) | Windows setup, build/load `Mod/` |
| [ui-icons.md](./guide/ui-icons.md) | Launcher illustration on the game's floating button |

## Research

| Doc | Topic |
| --- | --- |
| [2026-08-06-csharp-agent-runtimes.md](./research/2026-08-06-csharp-agent-runtimes.md) | C# agent stacks (superseded by [0001](./adr/0001-in-process-meai-loop.md)) |
| [2026-08-06-agent-runtime-gameface-requirements.md](./research/2026-08-06-agent-runtime-gameface-requirements.md) | apeira/xsai vs Gameface Web APIs |
| [2026-08-06-gameface-feature-support.md](./research/2026-08-06-gameface-feature-support.md) | Official Coherent support + CS2 empirical notes |
| [2026-08-06-research-in-game-ai-mods.md](./research/2026-08-06-research-in-game-ai-mods.md) | Other games’ in-game LLM mod patterns |
| [2026-08-06-agent-runtime-compression-observability.md](./research/2026-08-06-agent-runtime-compression-observability.md) | Long-run compression and tool-call observability |
| [2026-08-10-cs2-mod-hot-reload.md](./research/2026-08-10-cs2-mod-hot-reload.md) | CS2 data/UI/C# hot-reload feasibility |
| [2026-08-11-tool-deepening-next-seams.md](./research/2026-08-11-tool-deepening-next-seams.md) | Prefab roles, road features vs replacement, operational areas (partially superseded) |
| [2026-08-11-cs2-map-image-mod.md](./research/2026-08-11-cs2-map-image-mod.md) | CS2MapView / Carto map-image options |
| [2026-08-12-cs2-sewage-outlet-placement-rules.md](./research/2026-08-12-cs2-sewage-outlet-placement-rules.md) | Shoreline, road, and pipe requirements for pumps and outlets |
| [2026-08-13-cities-skylines1-agent-skill-road-planning.md](./research/2026-08-13-cities-skylines1-agent-skill-road-planning.md) | CS1 road-topology QA ideas transferable to CS2 |
| [2026-08-13-compact-local-map-and-route-anchoring.md](./research/2026-08-13-compact-local-map-and-route-anchoring.md) | `LOCAL_MAP` design; auto route anchoring out of scope ([0004](./adr/0004-linear-networks.md), [0006](./adr/0006-budgeted-local-map.md)) |
| [2026-08-14-cs2-multi-instance.md](./research/2026-08-14-cs2-multi-instance.md) | Two CS2 processes on one Windows PC (not supported) |
| [2026-08-15-ui-gameface-host.md](./research/2026-08-15-ui-gameface-host.md) | Chat UI is Gameface-hosted; the web is not a host |
| [2026-09-17-agent-game-observation.md](./research/2026-09-17-agent-game-observation.md) | 2026 survey: game-agent observation (structured vs pixels vs hybrid) |
| [2026-09-20-wait-simulation-background-patterns.md](./research/2026-09-20-wait-simulation-background-patterns.md) | Wait/background patterns; current clock is [0029](./adr/0029-player-owned-clock.md) |
| [2026-09-21-ai-urban-planning.md](./research/2026-09-21-ai-urban-planning.md) | 2026 survey: real-world AI for urban planning (academia, industry, cities) |

## Ops

| Doc | Topic |
| --- | --- |
| [2026-08-16-20k-live-acceptance.md](./ops/2026-08-16-20k-live-acceptance.md) | In-progress 0→20k Windows live-acceptance evidence ledger |
| [2026-08-15-windows-game-dll-handoff.md](./ops/2026-08-15-windows-game-dll-handoff.md) | Frozen `Game.dll` ILSpy paste-back (LV electricity + 8-step cap) |
| [2026-08-12-tool-surface-audit-and-open-work.md](./ops/2026-08-12-tool-surface-audit-and-open-work.md) | Frozen audit (not current open work) |
| [2026-08-10-gameplay-capability-backlog.md](./ops/2026-08-10-gameplay-capability-backlog.md) | Superseded post-10k backlog |
| [2026-08-10-placement-utilities-handoff.md](./ops/2026-08-10-placement-utilities-handoff.md) | 10k handoff evidence |
| [2026-08-09-10k-loop-task.md](./ops/2026-08-09-10k-loop-task.md) | Historical 10k task brief |
| [2026-08-08-windows-mcp-game-debug-loop.md](./ops/2026-08-08-windows-mcp-game-debug-loop.md) | Hands-off Steam/Gameface control loop |
| [2026-08-07-sewage-handoff.md](./ops/2026-08-07-sewage-handoff.md) | Historical sewage-session handoff |
| [2026-08-07-chat-ui-debug-computer-use-handoff.md](./ops/2026-08-07-chat-ui-debug-computer-use-handoff.md) | Chat UI / Gameface CDP handoff |
| [scripts/2026-08-07-gameface-cdp/](./ops/scripts/2026-08-07-gameface-cdp/) | CDP probe helpers for `-uiDeveloperMode` :9444 |
| [2026-08-06-in-game-agent-fixes-handoff.md](./ops/2026-08-06-in-game-agent-fixes-handoff.md) | Early in-game agent handoff |
| [2026-08-06-windows-toolchain-pitfalls.md](./ops/2026-08-06-windows-toolchain-pitfalls.md) | Steam/Scoop/Unity/`f2c1`/UI template traps |
