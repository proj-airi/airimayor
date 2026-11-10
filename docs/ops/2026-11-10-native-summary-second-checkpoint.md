# Repeated summary-content failure in the 100k run

Status: frozen

Date: 2026-11-10. Second checkpoint in the same loaded city and unchanged product build as [the first observation](./2026-11-10-native-summary-first-checkpoint.md). Session `20261110T033042Z--1a582899a4b34550aa25c149b45e3b80`; observations through seq 22230. The long run is still far below 100,000 population. No source, provider or context setting changed between checkpoints.

## Second checkpoint

At seq 20412, history compaction triggers at 55,840 estimated tokens, just above the configured 55,808 threshold. The before history has 92 messages. At seq 20418, it has 50 messages and 22,309 estimated tokens, with `changed=true`, `status=ok` and no exception.

The saved summary again contains no substantive facts:

```text
[Summary]


<｜DSML｜ calls>
<｜DSML｜ invoke name="list_zones">

</｜DSML｜ invoke>
</｜DSML｜ calls>
```

The request at seq 20414 still begins with the native conversation-summary system instruction. Its next message is the previous malformed screenshot summary. The response at seq 20417 contains only the displayed text: provider usage is 22,720 input tokens, 24 output tokens and zero reported reasoning tokens, against the 4,096 output limit. No tool definitions are recorded on this summary call. The subsequent ordinary mayor request at seq 20420 begins with the new malformed summary. The following retained historical assistant message says the industrial zone name was wrong and calls `list_zone_types`; the returned `list_zones` fragment again resembles continuation near the cut boundary rather than a summary.

Thus two successive checkpoints have the same failure class, with different tool names. This strengthens the evidence that the first case was not an isolated malformed response in this run. It does not establish a failure rate across models or distinguish model behavior from provider/SDK handling. The previous bad summary is demonstrably fed into the next summarization request. Recent player guidance still carries the 100000 target, so complete loss of the population goal remains an incorrect claim.

The local `check-summary.mjs` exits 1 for both checkpoints. Resolved before/after metadata, summary messages, relevant model records and city reads are frozen in `C:\Users\super\AppData\Local\Temp\airimayor-100k-20261110\checkpoint2-evidence.json`, SHA-256 `C4124717D651009A8D6D76B9EE5FC5423A147897CBFDB199B0562FEB7CE1DAF0`. The first stopped-turn snapshot and live session remain intact. No additional Stop was needed to preserve this checkpoint.

## Effect of the latest guidance

Before intervention, population at seq 18519 is 78 after a completed five-hour advance. Residential zoning counts at seq 18525 are 1,097 cells, 867 occupied and 230 empty. Returned reasoning incorrectly infers about 150 mostly empty houses from these cell counts, notices eight air-pollution warnings near the western services, and then chooses an eight-hour advance. Zoning cells do not establish building or household counts; the cause of slow immigration remains unverified.

A targeted message is received at seq 18954 and applied at **seq 18961**, after the eight-hour advance completes. It asks the mayor to distinguish zoning cells from buildings/households, inspect actual residences and highway access, measure pollution, and choose a justified small batch instead of merely waiting longer. The following calls are observable changes in behavior:

- Seq 19085 reads labor; 156 jobs and zero unemployment are reported.
- Seq 19089 reads road topology. Its isolated components are distant from the initial neighborhood; this alone does not establish every directional access route or traffic performance.
- Seq 19092 lists actual residences, finding 55 within the requested area and returning 12. This does not by itself establish their occupancy.
- Seq 19095 samples air pollution across the residential and western service area.

The reply then proposes healthcare, a separate industrial highway connection and residential growth toward cleaner air. A clinic succeeds at seq 20809. The proposed industrial road fails native validation with `InvalidShape` at seq 20833; subsequent local map reads follow. A declared connection is therefore not a completed traffic improvement. No new residential expansion or before/after traffic measurement appears in this window, and the native plan still has only its initial `plan.changed` event.

The latest recorded Population tool sample remains 78; the mayor's reply claims 81, while the later UI shows 80. Those are separate observations, not interchangeable samples. The guidance improved the next set of investigations, but city growth, pollution mitigation and sustained global planning are not yet accepted. Native summary content also remains a failed gate in [open-work.md](../open-work.md).
