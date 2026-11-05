# External mayor is an ACP child

Status: accepted

Catalog, installation and permission choices superseded by [0049](./0049-registry-external-mayors.md). The transport choice remains in effect.

The built-in loop stays the default. The Agent option is one list: Built-in, then one row per command. Adding an agent adds a row. OpenCode's row launches `opencode` with arguments `acp`. When the player picks a command row, the chat still calls `Prompt`, `Cancel`, and the update stream. That head speaks ACP on the process stdin and stdout (`dotacp.client`), and passes city tools on `session/new` as one stdio MCP server. The server is a separate executable on the official `ModelContextProtocol` package. It forwards each call to a loopback listener in the game, which uses `IAgentTools`, so construction still runs on the simulation thread. It does not download an agent. The client does not offer a filesystem or a terminal, and it does not call `authenticate`. The spawned process denies OpenCode's own computer tools and leaves the city tools. It does not change the player's OpenCode config.

## Considered Options

- **HTTP MCP inside the game, using `ModelContextProtocol.AspNetCore`.** Rejected: that host needs ASP.NET Core, and HTTP MCP is optional. Every ACP agent must accept stdio. OpenCode also refuses MCP-over-ACP and SSE.
- **Speak MCP on the game process stdin.** Rejected: the agent spawns the MCP process and owns that stdin. The game is already the ACP client.
