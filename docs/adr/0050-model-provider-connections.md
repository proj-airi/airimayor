# Model providers own discovery and SDK construction

Status: accepted

Keep the native Harness from [0048](0048-native-maf-harness.md) and its `IChatClient` boundary. `Agent/ModelProviders.cs` owns provider presets, model discovery, SDK construction and required request headers. OpenAI Responses and Chat Completions use `Microsoft.Extensions.AI.OpenAI`; Anthropic Messages uses the official `Anthropic` SDK's adapter. Provider integration does not introduce another agent loop or alter Harness orchestration.

A provider is separate from its request protocol. The initial presets are OpenAI, OpenCode Go, OpenRouter, Anthropic and a custom OpenAI-compatible service. Model-list and inference addresses are independent: Go's public catalog is `/inference/go/v1/models`, while its OpenAI inference base is `/inference/go/openai/v1`. Go uses the application's own User-Agent and one stable `x-opencode-session` value per conversation, including background calls. Public catalogs receive no key; authenticated catalogs require the inference origin. Provider identity comes from the selected preset rather than URL guessing. Unsupported protocol or provider errors are surfaced without retrying another protocol. See the [Go integration contract](https://opencode.ai/v2/docs/console/go/), [IChatClient abstraction](https://learn.microsoft.com/en-us/dotnet/ai/ichatclient), and [Anthropic SDK](https://github.com/anthropics/anthropic-sdk-csharp).

`ProviderConnections` owns immutable connection values and temporary catalogs indexed by provider. Switching providers retains each key, model, addresses, protocol and context budget. A catalog result is accepted only while its source settings still match; refreshing never chooses a model, applies settings or reopens chat. A manual model ID remains usable even when absent from the list. Changing a server's origin clears a reused key. Catalog metadata does not overwrite the player's context budget.

The settings first choose Built-in mayor or External agent. Built-in settings contain provider selection and connection details; external settings contain only the Registry agent and its checks. City permissions have their own tab. Applying captures the selected connection for a fresh chat. Connection checks capture the currently edited settings without applying them or changing the city's chat. External agents retain their own models and sign-in under [0049](0049-registry-external-mayors.md).

Saved connections use one versioned native-settings property. Unknown shapes, providers and protocols are rejected. The old flat connection fields have no migration: keys, models and agent selection must be entered again. This replaces only 0048's Responses-only transport decision; its Harness behavior remains in force.

## Considered options

- A single configurable OpenAI endpoint with `/models` appended cannot describe Go's separate catalog or Anthropic's protocol.
- A new proxy service or broad provider framework would add deployment and runtime ownership while leaving provider discovery and the game settings unresolved. Existing SDK adapters fit the current .NET/Unity boundary; the provider module contains the remaining differences.
