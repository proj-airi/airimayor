# AGENTS.md

Guidance for agents working in **airimayor**. Vocabulary: [CONTEXT.md](CONTEXT.md). Decisions: [docs/adr/](docs/adr/). Current open work: [docs/open-work.md](docs/open-work.md). How to write docs: [docs/AGENTS.md](docs/AGENTS.md).

## Product shape

```text
Gameface UI (chat) ↔ Cohtml bindings ↔ C# Agent + ToolQueueSystem (UIUpdate)
 ↓
 Unity ECS / native tools
```

| Path | Role |
| --- | --- |
| `Mod/` | Shippable product (C# + `Mod/UI`) |
| `archive/` | Offline POCs + frozen M1 smoke — not the product root |
| `docs/` | Numbered ADRs, current [open-work.md](docs/open-work.md), dated evidence; how to write them: [docs/AGENTS.md](docs/AGENTS.md) |

When a domain term is used, match [CONTEXT.md](CONTEXT.md). When a seam or module is designed, use the **codebase-design** skill. When a durable choice is made, add or update a numbered ADR — do not leave it only in chat or an ops audit.

## Hard constraints

- API keys stay in settings or the environment, never in the repo.
- Real Windows + in-game load is the authority; `archive/` is historical.
- The product has not shipped. Prefer the correct foundation over compatibility: reject unknown on-disk shapes, do not write migrations, and do not add model-facing shims or aliases.
- Use native validation. No Anarchy, `force`, or collision bypass. Construction recovery belongs inside the write tool.
- Tools enqueue onto the simulation thread. Do not apply construction from the UI or chat thread.
- Road and building geometry goes through the local OSM map. Follow [ADR 0058](docs/adr/0058-local-osm-city-geometry.md) when changing the conversion or the city tools.
- Mayor-facing text is a city contract: prompts, tool descriptions, result notes, and chat chrome speak city terms. Engine, HTTP, ECS, and loop jargon stay in code.
- Delete unused model-facing tools for real (catalog, HTTP, and handler together). Network is not a junk drawer.
- The player owns the clock. Timed waits advance in-game time then restore speed and pause. Do not force pause as the product runtime.
- The player runs live acceptance on a new city and pastes logs. Agents judge pass/fail from those logs.
- Budgeted map text shrinks by character budget; list tools keep a row limit.
- Fix the cause; no defensive wrappers.
- Repo language is English (comments, ADRs, README, commit subjects).
- Design it twice and pick the cleaner design. No unsolicited docs.

## Skills

When the task matches, read the skill before acting — not a menu to skip.

- Seam or module design → `codebase-design`
- A domain term crystallizes, or a durable choice needs an ADR → `domain-modeling`
- Something is broken, throwing, or slow → `diagnosing-bugs`
- Gathering sources into a dated research/ops note → `research`

## Commits

`type(scope): English subject` — e.g. `feat(mod): …`, `docs(adr): …`.

Complete, verify, commit, and push one work item at a time. Do not accumulate unrelated completed items. Preserve unrelated user changes already in the worktree.

## Docs

Follow [docs/AGENTS.md](docs/AGENTS.md). `CONTEXT.md` is glossary only. ADRs are numbered. Current inventory is `docs/open-work.md` only.
- Completing work that changes unfinished inventory **must** edit `docs/open-work.md` in the same change (or same session before done). Do not leave it for the user to remember.

## Verification

- Repository policy: `npm ci`, then `npm run lint` and `npm run test:lint`. Run `npm run lint:ai` for semantic review of changed files; policy and exclusions live in [alint.config.json](alint.config.json). Lint does not establish in-game acceptance.
- C#: `cd Mod && dotnet build` (close the game before redeploying the DLL).
- UI-only: `cd Mod/UI && npm run build` (needs `CSII_USERDATAPATH`).
- Prefer targeted checks over inventing large test suites unless asked.
- After logic changes: deslop; comments and docs must match the code; use codebase-design language if a seam moved.
- Before claiming done: if unfinished inventory changed, `docs/open-work.md` is updated (not implemented vs awaiting live acceptance; delete passed gates).
