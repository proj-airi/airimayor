# First native-summary checkpoint in the 100k run

Status: frozen

Date: 2026-11-10. This is the first observation window of an ongoing fresh-city run, through record 17610. It is not a completed 100,000-population acceptance test. The player requested background game operation, observation of returned explanations and planning, and manual guidance where useful. Source design and measurement limits are in [the observation design](../research/2026-11-10-planning-observation-design.md).

## Setup and preserved evidence

- Source: `0020767d0a6837708f13c2b275d0302d54158501`. Windows `dotnet build` in `Mod` succeeded before launch, with 0 errors and 26 dependency/resource warnings. No product code changed during this observation.
- Deployed `airimayor.dll` SHA-256: `447F329A14A3E4FAB9809D616E3970653E2D4ABB3783E1CA5CD53E1941F14A12`.
- Game 1.6.2f1, new city `Summary100k-20261110`, River Delta, Normal mode; unlimited money, all-unlocked progression and all-unlocked tiles off. Loading completed at 11:30:42 +08:00.
- Saved connection: built-in mayor, `opencode-go`, `deepseek-v4.1-flash`, Chat Completions, context window 128,000. The saved credential was already present and was neither changed nor copied into this repository.
- Session directory: `C:\Users\super\AppData\LocalLow\Colossal Order\Cities Skylines II\ModsData\airimayor\sessions\20261110T033042Z--1a582899a4b34550aa25c149b45e3b80`. Sequence numbers below refer to its `records.jsonl`; asset references are relative to this directory.
- A stopped-turn snapshot through seq 13775 is preserved at `C:\Users\super\AppData\Local\Temp\airimayor-100k-20261110\checkpoint1-stopped`. Its `records.jsonl` SHA-256 is `573320B3C269E4FE200468992214AC69EBFAEB649BF1105FFF27DA2E4B18B55B`.
- Manual continuation requested a save at seq 13801. `Summary100k-20261110-checkpoint1.cok` exists in the user's game Saves directory, 50,427,524 bytes, written at 11:43:41 +08:00. The save call itself only reports asynchronous initiation.

Gameface CDP allowed reading and operating the game without OS mouse or keyboard injection. Background operation still consumes CPU/GPU resources. A five-minute Codex heartbeat observes the run and evaluates manual continuation; it does not add automatic continuation to the product.

## Confirmed failure: persisted text is not a summary

The first native compaction completed structurally but failed to summarize the conversation.

| Evidence | Observation |
| --- | --- |
| Seq 12502, `compaction.started` | Window 128,000; output reserve 16,384; input budget 111,616; trigger 55,808; target 22,323; eight recent groups preserved. |
| Seq 12504, summarizer input | The first system message instructs conversation summarization, including facts, decisions, preferences, future context and tool outcomes. Input message 2 contains the original 100,000 goal, ordinary-game restrictions, milestone saving, final report and Chinese-language preference. |
| Seq 12507, response | `finish_reason=stop`; input 53,667, output 124 tokens, of which 101 are reported reasoning. Output cap is 4,096, so this did not exhaust that cap. No typed tool call is present. |
| Seq 12508, `compaction.finished` | `changed=true`, `status=ok`, no exception. History falls from 40 messages / 56,086 estimated tokens to 19 / 18,987. |
| Seq 12510, next mayor input | The malformed summary is the first message. The original player instruction is removed. Recent guidance at message 15 repeats 100000, so the population goal is not wholly absent from the remaining request. |

The persisted summary's entire text is:

```text
[Summary]


<｜DSML｜ calls>
<｜DSML｜ invoke name="screenshot">

</｜DSML｜ invoke>
</｜DSML｜ calls>
```

The returned reasoning reacts to the last map result in the summarized prefix and proposes a screenshot. The first retained historical assistant message had already reacted to that same site and called a screenshot. This is observable continuation of the old exchange instead of a useful summary. It does not identify whether the model, provider or SDK caused the behavior.

The checkpoint's `status=ok` reflects the absence of an exception, not content quality ([RecordedCompaction.cs](../../Agent/RecordedCompaction.cs)). A useful population target surviving in a recent player message does not make the summary valid: the summary contains no city facts, decisions, constraints or pending checks. The original milestone-save and language preferences do not survive there. Fixed system instructions are supplied separately.

Key assets:

| Content | Asset |
| --- | --- |
| History before | `assets/d8aac8793fbccefb23f923811cabdba7bd0882033d555f9c421d81cdb5aa5d03` |
| History after | `assets/7a5d3d58fb8798e4bd1c5e1d19a3688c8cac99e15207e7901f11e64703be150d` |
| Summary message | `assets/ac55d7964fb1a51905f7aff0be79060949a7658f4446b84ac5cc953191930925` |
| Summarizer input references | `assets/84fef505b95e6a1269c823461e708bb558427739adc226730d463190739d9f9c` |
| Summarizer output references | `assets/3083ff0db394a3234ac3cca525979b8e6b57a1940ee7fdc195daa02c95e11a1e` |

A local `check-summary.mjs` resolves every recorded checkpoint, extracts messages marked `_is_summary`, and rejects the observed DSML marker. It exits 1 for seq 12508, while separately confirming that recent guidance still contains 100000. This is a deterministic check of this captured failure, not a general summary-quality classifier.

## Planning before and after guidance

Planning delays precede the first compaction. The first request offers 60 tools, including map images, screenshots and native todos; actual images reach later model inputs. Missing visual tools do not explain this run. Returned explanations consider the existing loop, water, owned land, the western highway barrier, and eastern/southern alternatives. Initial coordinate assumptions are refined with bounded map text. There is evidence of local alternative comparison, but not yet a measured citywide transport plan.

The mayor performs many observations before the first construction. A detailed intervention asks it to finish one inhabitable batch on existing frontage, provide water/power/sewage and modest jobs, reserve collector routes and a second external-access intention, and compare infill, unlocked density and two expansion directions after measuring the batch. The input is applied at **seq 11022**. Four roads at seq 10874, 10877, 11015 and 11018 were built before that boundary and cannot be credited to the intervention.

After guidance, the reply gives concrete siting reasons. Residential/commercial zoning succeeds at seq 12199–12205; a water tower, sewage outlet, coal plant and transformer succeed at seq 12208–12217. This is evidence of action following the guidance, not proof that guidance alone caused it: the mayor had already begun construction. There are not three independent baseline/post-guidance expansion batches in this window.

After the malformed summary was preserved and Stop/Continue exercised, a second instruction at **seq 13782** restates the target and ordinary-game rules, requests a checkpoint save and fresh reads, and repeats the alternatives/traffic checks. Subsequent observations find 15 building sewage warnings and disconnected utility segments. A sewage pipe and a correctly connected power cable succeed at seq 14826 and 14829. At seq 14899 the 15 building sewage warnings are gone, although one pipe warning and three power-line warnings remain. Thus “all warnings cleared” would be false. Population history at seq 15159 is **18** at game time 10:11.

The next residential zoning uses existing locations at (-749,-326) and (-404,-190), followed by industry at (-940,-470). The reply mentions northwest flat land and the western highway area, but this window does not yet establish independent collectors, a second external road connection, or comparable before/after traffic measurements. Only one `plan.changed` has appeared. Declaring a broad plan is not evidence of maintaining it through construction.

## Separate interruption: a milestone modal

Three advances at seq 14880, 15924 and 16086 return `completed=false`, `targetReached=false`, with the clock left paused. The latter two remain at 10:11. The mayor increases the requested duration from four to six hours and repeatedly reads the same population sample, then investigates occupancy and expenses.

Background DOM inspection shows the game's milestone-1 unlock modal. Closing its observed Close button allows the clock to move again; subsequent reads are at 10:18, 10:31 and 10:41. A fourth player message, applied at **seq 17610**, explains the interruption and asks the mayor to verify actual elapsed time before treating unchanged samples as city behavior. This manual intervention is a confound to any claim of uninterrupted autonomy. The record's clock result alone does not identify which UI caused a pause; see [BridgeSystem.cs](../../Mod/CS2MCP/BridgeSystem.cs).

## Interpretation and verification limits

The native-summary acceptance gate fails on content for this connection. Persistence and token reduction worked; useful summarization did not. The earlier planning delay and later modal handling are separate observed weaknesses. Neither proves that the previously reported one-direction urban sprawl was caused by compression. The city is still too small for a meaningful traffic-governance result.

The strongest untested explanation is that historical assistant/tool exchanges cause the summarizer to continue the conversation instead of following the new summary instruction. Provider handling of a no-tools request is another candidate: no tool definitions are recorded for the summary, and local configuration does not explicitly disable tool choice. SDK/wire mapping remains possible because the recording captures semantic SDK content, not raw transport. Correct summary instructions and the original goal are present in the semantic request; output exhaustion and cancellation do not explain this checkpoint. These are hypotheses, not verified causes or chosen fixes.

The offline trace-kit checker was run against the stopped-turn snapshot. It reports an incomplete source because `source.stopped` is absent: the city/session is still loaded even though its root turn was stopped. It found 13,776 records, 80 spans, 23 model calls and 55 tool calls; write evidence judged nine confirmed, zero diverged and three undetermined. This open-session snapshot is not a passed closed-recording acceptance gate.

The run continues on the same loaded session and unchanged product build. Future checkpoints must separately inspect summary text, next-request context, actual growth decisions and city outcomes. Reaching 100,000, verifying a final save and measuring traffic remain outside this frozen window; current unfinished inventory remains in [open-work.md](../open-work.md).
