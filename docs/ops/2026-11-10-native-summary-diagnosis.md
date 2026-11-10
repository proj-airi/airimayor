# Native summary task-boundary failure: diagnosis evidence

Status: frozen

Date: 2026-11-10. Source snapshot: `0020767d0a6837708f13c2b275d0302d54158501`. This audits saved HTTP experiments for the built-in mayor's OpenCode Go `deepseek-v4.1-flash` Chat Completions connection. It makes no implementation decision. The two live content failures and their persistence are documented in [checkpoint one](./2026-11-10-native-summary-first-checkpoint.md) and [checkpoint two](./2026-11-10-native-summary-second-checkpoint.md).

## Established boundary

The integration fails to distinguish conversation material from the task currently being requested. The native summary instruction reaches the provider request as its first system message, but failed responses answer or continue the historical city task. This is broader than malformed tool markup: a two-message case with no tools, images or historical reasoning answers the old player request with game advice instead of summarizing it.

Saved raw responses contain DSML text or null content before SDK response parsing. Local response parsing therefore did not invent those replay failures. This establishes failure at the application/provider boundary; it does not identify the responsible provider component or distinguish model behavior from gateway processing. Adding a final summary request, or presenting history as material to summarize, changes the observed task performed in the tested cases. These observations do not establish a universal remedy or a cross-model failure rate.

The product constructs the native summarizer over the same selected client, limits output to 4,096, and persists the native reducer's result. It does not supply a custom summary prompt or perform content-quality validation. `status=ok` means no exception was observed. Sources: [ClientSession.cs:72–97](../../Agent/ClientSession.cs#L72-L97), [RecordedCompaction.cs:21–30](../../Agent/RecordedCompaction.cs#L21-L30), [49–61](../../Agent/RecordedCompaction.cs#L49-L61).

The Chat Completions adapter forwards historical reasoning as `reasoning_content` and moves tool-returned images into user image messages. Its header adapter adds the conversation identifier and only rewrites the token-limit field when the route declares that compatibility. Sources: [ModelProviders.cs:130–135](../../Agent/ModelProviders.cs#L130-L135), [180–200](../../Agent/ModelProviders.cs#L180-L200), [213–258](../../Agent/ModelProviders.cs#L213-L258). Actual saved replay requests use `max_completion_tokens: 4096`, contain no tool definitions, and retain the native summary system instruction.

## Saved experiment evidence

Evidence root: `C:\Users\super\AppData\Local\Temp\airimayor-summary-debug`. Each label below has `.request.json`, `.response.json` and `.summary.txt` files under `bin\Debug\net10.0`. Eighteen request/response hashes and usage are indexed in `results.json`, SHA-256 `e29bb3c0af633493b5e3ee68f5f1844c0e1e005ef3a940c80ab922c3a6aca976` at this audit. All 18 hash pairs were checked against the files. Request bodies exclude authorization headers. The replay harness made these eighteen real provider requests; the independent evidence review only read their saved artifacts.

The replay program loads the live before-history assets as `ChatMessage`, invokes MAF's `SummarizationCompactionStrategy` with the product thresholds, and captures the HTTP body after the product adapter. Each invocation creates a fresh diagnostic conversation identifier. Its variant handler edits that body before transmission. Source: local `Program.cs:17–47,57–121`; the scalar-corrected version's SHA-256 is `711e3aff1db60ff2f4ae19fe8e1a2ecac3222154251be318302e2255f8bf75ae`. The earlier version was `a3eba69d9fba89e0912e30a00baa3e71a8203f2d8f01292f1b28d66cf48887dd`.

| Label | Request change / message count | Manual content verdict |
| --- | --- | --- |
| `baseline1`, `baseline2` | Same full native-path replay body, 46 messages | Null content / screenshot DSML, respectively; both continue city work in returned reasoning. |
| `minimal-tail` | Summary system, original user task, final historical assistant and two results; 5 messages | Screenshot DSML; still continues city work. |
| `minimal-tiny` | Same shape, short synthetic reasoning and tool results; 5 | Invented construction-call DSML. |
| `tiny-no-reasoning` | Remove the tiny historical reasoning; 5 | City-inspection DSML. Reasoning echo is not necessary for failure. |
| `tiny-tool-none` | Explicit `tool_choice=none`; 5 | City-inspection DSML remains ordinary text. Disabling tool choice did not produce a summary. |
| `tiny-map-only`, `tiny-map-repeat` | Original user task plus one map-call/result pair, no reasoning; 4 | Both produce city-inspection DSML. Requests are byte-identical; different tool names do not change the failure class. |
| `tiny-tool-only` | Remove the historical user task from the map-only case; 3 | Summary-shaped response describing the map result. |
| `tiny-user-only` | Summary system plus original player request; 2 | **Semantic failure without DSML:** offers two ways to help play the game and a development guide. |
| `tiny-user-only-final` | Add a fresh user instruction to summarize the preceding conversation; 3 | Requirement-only summary; correctly says no game observations or work have occurred in this fixture. |
| `tiny-transcript` | Put historical messages inside a user message introduced as a conversation to summarize; 2 | Summary-shaped response preserving the task and short map/camera results. |
| `full-no-reasoning` | Remove all historical reasoning from the early full replay; 46 | Time-advance DSML; no useful summary. |
| `full-final-request` | Append only the fresh user summary instruction to the early full replay; 47 | Summary-shaped response containing task, observations, plan and constraints. |
| `full-transcript` | Frame history as source material; retain images as separate parts of that user message; 2 | Summary-shaped response. This changes both role structure and image placement. |
| `checkpoint2-final-request` | Append the fresh user summary instruction to the second checkpoint replay; 71 | Summary-shaped response, with factual/actionability limitations below. |
| `scalar-restored-baseline` | Correct scalar tool-result deserialization, full replay; 46 | Two prefab-query DSML blocks; no summary. |
| `scalar-restored-final` | Same corrected full replay plus fresh user summary instruction; 47 | Summary-shaped Chinese response retaining goal, rules, observations and plan. |

All eighteen saved responses finish with `stop`; the largest reported output is 2,940 tokens, below the 4,096 cap. The two early full baseline bodies have identical SHA-256 `33351a03d3d7b175b54bea5b2c44a0391bc732666bb26fc8984f9eb737d0de85`. Offline `verify-controls.mjs` passed deep-equality checks: both full pairs and the user-only pair differ only by the appended user summary instruction, and the map-only repeat is identical to its original. Model and output limit remain unchanged in each pair. The map-only repeat shares request hash `e76af1bb0bf30ee76f6f099af288f98a0ad8bad67f63cb54930d496e7c10e4fe`.

The corrected baseline/final pair reports input/output tokens of 37,603/2,731 and 37,609/1,383, respectively. Their request hashes are `09c9a0fa064f2769169e802e565d3cd8b704c799941ffac62bd70315f4af2b6a` and `16acf8063c21d6b762203caafa417af3dfef277a4a42d59659b80607d6783761`. This pair reproduces the task-interpretation difference after correcting the scalar replay artifact.

## Classifier and fidelity limits

The replay's `failed` check (`Program.cs:49–53`) detects a leading DSML block, `[Summary unavailable]`, or empty summary. It does not test whether a response summarizes its input. `tiny-user-only` receives `failed=false` despite answering the historical request. “No DSML” is consequently insufficient for acceptance. A manual semantic check must distinguish summarization from continuation before checking factual retention.

Summary-shaped output also needs factual review. `checkpoint2-final-request` instructs the next mayor to call `list_zones`, whereas the actual catalog tool is `list_zone_types` ([catalog:1244](../../Mod/Host/ToolCatalog.json#L1244)). The source error says the zone is unknown and refers to `/zones`; it does not establish a callable `list_zones` tool. Other statements, such as declaring an expansion direction best or inferring usable outside access, can promote earlier reasoning into facts. No complete fact-by-fact acceptance audit of the longer summaries was performed.

The early full replays are not byte-exact captures of the game's original HTTP request. Their initial deserialization restores AI-content arrays but leaves scalar tool-result strings as `JsonElement`; some outgoing tool text consequently contains an extra JSON-string layer. The later `scalar-restored-*` pair uses `GetString()` for those values (`Program.cs:26–27`) and reproduces the failure/control difference. The tiny fixtures replace relevant tool text directly, and `tiny-user-only` has no tool result to serialize. The original game-wire body is still unavailable; corrected replay fidelity does not make it a byte-exact capture. Early artifacts remain unchanged.

These are finite samples through one model connection, without a fixed decoding seed or visibility into gateway internals. Reported input-token counts differ between some requests despite semantically shared history; no provider-cache or token-accounting conclusion follows. The experiment identifies an integration failure in task interpretation, not proof of a particular model mechanism, an SDK-wide defect, or the cause of the city's earlier planning delays. Useful summary persistence, factual fidelity, later planning behavior and reaching 100,000 remain separate questions.
