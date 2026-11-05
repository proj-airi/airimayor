# Loop-owned mayor mandate

Status: built-in plan ownership, iteration and continuation superseded by [0048](0048-native-maf-harness.md). City writes remain ungated by the plan. Earlier plan-shape changes are in [0033](0033-chat-plan-entries.md).

Playbook text and compaction `current_plan` did not stop reactive one-tool turns: autonomous continuation asked the model to review the whole city, and a 30-generation cap chopped a plan into fresh citywide reviews. The loop now holds a **mayor mandate**. The model declares it with `set_plan`; player messages leave it in place ([ADR-0030](0030-player-message-keeps-plan.md) replaces “player messages clear it”); autonomous continuation does not clear it either. City writes are not gated on it. Each model round pins one live note; that note is not an append-only history copy. The player sees it as chat chrome (snapshot + `plan` event), not a transcript line. `set_simulation` stays player-clock playbook ([ADR-0029](0029-player-owned-clock.md), [ADR-0031](0031-explicit-pause.md)): advance when the next decision needs simulated time, not because the mandate entered a verifying phase.

This beat prompt-only continuation, treating compact JSON as the live plan, gating the tool catalog, a verifying wait FSM, a closed `kind` enum, and adding a congestion write tool. The per-turn generation cap is deleted: a turn ends when the model stops calling tools, a generation times out, the player steers or interrupts, or the city unloads.

## Considered Options

- **Prompt-only playbook and continuation.** Rejected: live logs already ignored the traffic loop and the “compare two or three directions” instruction.
- **Compact JSON as the plan object.** Rejected: the summarizer is not an execution authority and is wiped on every compact.
- **A new traffic tool.** Rejected: traffic governance is wait, reads, topology QA, and existing road writes.
- **Gating writes until `set_plan`, and blocking growth while traffic verifying.** Rejected: the catalog is not a second permission layer; verifying after every road write trained the model to wait.
- **A closed `kind` enum on `set_plan`.** Rejected: after verifying died it no longer branched loop behaviour; `goal` already names the plan.

## Consequences

`set_plan` is loop-local, not a simulation route. Busy `Send` no longer cancels an in-flight tool batch; see [ADR-0030](0030-player-message-keeps-plan.md).
