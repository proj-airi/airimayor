# Native summary ordering controls

Status: frozen

Date: 2026-11-10. Follow-up to the [native-summary diagnosis](./2026-11-10-native-summary-diagnosis.md), using the same OpenCode Go `deepseek-v4.1-flash` Chat Completions connection and product snapshot `0020767`. This records isolated provider experiments, not a production change or live-city acceptance.

## Finding

Instruction position is a confirmed factor in these controlled examples. Moving the exact native summary system message from first to last changes the observed task from continuing historical city work to summarizing it, in both the two-message fixture and the full reconstructed history. No wording, role, duplication, or additional reminder changes in those comparisons. A second identical full request also produces a summary.

Changing only the instruction role from system to user works in the tiny fixture but fails in the full history. Position therefore explains more of these observations than a general claim that the system role is broken. The controls do not establish the provider's internal mechanism or a universal ordering rule.

## Evidence

The product uses the native summarizer over the selected client: [ClientSession.cs:72–97](../../Agent/ClientSession.cs#L72-L97), [RecordedCompaction.cs:21–30](../../Agent/RecordedCompaction.cs#L21-L30). Historical reasoning and images pass through the [Chat Completions adapter](../../Agent/ModelProviders.cs#L213-L258).

Local evidence root: `C:\Users\super\AppData\Local\Temp\airimayor-summary-debug`. Each table label identifies `.request.json`, `.response.json`, and `.summary.txt` under `bin\Debug\net10.0`. The [seven-control manifest](<C:/Users/super/AppData/Local/Temp/airimayor-summary-debug/order-results.json>) records hashes and usage; its SHA-256 is `006d9d79f5c34fd312f7563fe2526a6130fba812f0dfc32d48bccfe4c38100c0`. The preceding full baseline is indexed in `results.json` and the prior diagnosis.

The [offline verifier](<C:/Users/super/AppData/Local/Temp/airimayor-summary-debug/verify-order.mjs>) passed all seven comparisons: five specified order/role changes, an unchanged tiny baseline, and the identical full repeat. [Program.cs:91–99](<C:/Users/super/AppData/Local/Temp/airimayor-summary-debug/Program.cs#L91-L99>) implements those transformations. Verification compares parsed complete request bodies, not just message counts.

| Label | Change | Manual response verdict |
| --- | --- | --- |
| `order-tiny-baseline` | Two messages: summary system, old user task | Fails: answers the old task with game advice; no DSML. |
| `order-tiny-system-last` | Move exact system message last | Summary of requirements. |
| `order-tiny-user-first` | Change only summary role to user | Summary of requirements. |
| `order-tiny-user-last` | Move last and change role to user | Summary of requirements. |
| `scalar-restored-baseline` | Full reconstructed history, 46 messages, system first | Fails: raw DSML `list_prefabs` calls. |
| `order-full-system-last` | Move exact system message last | Summary containing goal, rules, facts and pending work. |
| `order-full-user-first` | Change only summary role to user | Fails: raw DSML `take_screenshot`. |
| `order-full-system-last-repeat` | Identical full system-last request | Another summary containing goal, rules and facts. |

## Limits

“Summary” denotes the task performed, not verified factual accuracy. The full repeat, for example, adds an unsupported requirement to resume simulation before construction. A marker-only classifier misses the tiny baseline's semantic failure.

All seven new responses finish with `stop`, below the 4,096 output limit. This does not estimate reliability. Full-history reconstruction restores scalar tool-result strings but is not a byte-exact original game request; each experiment uses a fresh diagnostic conversation identifier. Provider model versus gateway responsibility remains unknown. The independent audit only read saved evidence and ran the offline verifier; no game or provider calls were made during this review.
