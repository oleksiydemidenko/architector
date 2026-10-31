# Agent Setup

## 1. Choose Your Access

**File access works with every agent that has a shell.** Nothing to install beyond the .NET SDK. The agent reads the digest and runs `context_route`.

**The Architector MCP server works with every MCP client.** It needs the .NET SDK and no other Unity MCP package. Tools: `architector_route` works from files, `architector_query` and `architector_edit` go to the open Editor through a local bridge.

Register it in your client. `<package>` is `Library/PackageCache/com.oleksiydemidenko.architector@<hash>`, and the hash changes when the package updates.

Claude Code:

```
claude mcp add architector -- dotnet run --project <package>/Tools~/ArchitectorMcp -- <project path>
```

Codex, in `config.toml`:

```toml
[mcp_servers.architector]
command = "dotnet"
args = ["run", "--project", "<package>/Tools~/ArchitectorMcp", "--", "<project path>"]
```

Cursor and most other clients, in their MCP JSON file:

```json
{
    "mcpServers": {
        "architector": {
            "command": "dotnet",
            "args": ["run", "--project", "<package>/Tools~/ArchitectorMcp", "--", "<project path>"]
        }
    }
}
```

The bridge listens on the loopback interface only and accepts a token that the Editor writes to `Library/Architector/agent-bridge.json` for each session. Turn it off in **Window > Architector > Settings > AI Integration**.

**If you already use a Unity MCP server,** Architector can add its tools there instead:

- [MCP for Unity by Coplay](https://github.com/CoplayDev/unity-mcp): tools `architector_query` and `architector_edit`.
- [Unity MCP by Ivan Murzak](https://github.com/IvanMurzak/Unity-MCP) 1.6.0 or later: tools `architector-query` and `architector-edit`. Both take one `parameters` string with a JSON object, for example `{"action":"project_summary"}`. This plugin starts in cloud mode and logs an authorization error until you sign in or switch it to a local server.

Architector detects the package and registers its tools. Check the state in **Window > Architector > Settings > AI Integration**.

Keep **Run In Background** on in Player settings, or the Editor stops answering while its window has no focus.

Another MCP server needs a small adapter: see [Adding an Agent Integration](AddingAgentIntegration.md).

## 2. Tell The Agent

Add this to your `AGENTS.md`, `CLAUDE.md` or Cursor rules. Keep it short: agents follow what such files say, and every line is read in every session.

```markdown
## Architecture

Architecture lives in `Architector/`. Do not edit its JSON by hand.

- Before changing an unfamiliar file or type, run
  `dotnet run --project <architector package>/Tools~/ContextRoute -- . <file or type>`
  and read only the digest lines and documents it prints.
- `Architector/Architecture.digest.md` is the approved architecture. Do not read it whole.
- To change the architecture, use `architector_edit` with a work session. Saving does not approve.
  Only the user approves a layer.
```

## 3. Choose Document Folders

`context_route` ranks documents by how well they match the query. It looks in `Documentation` and `docs` by default. To change that, create `Architector/context-route.json`:

```json
{
    "documentRoots": ["docs/design", "docs/technical"]
}
```

For one run, use `--docs=docs/design,docs/technical`.

## 4. Give Authoring Agents The Skill

If an agent will write architecture, copy `Skills~/architector-authoring/` into your agent's skills folder, for example `.claude/skills/` or `.agents/skills/`, or point the agent at it from your instructions.

## Several Agents

Only one agent work session can exist per Editor. A session locks the Architector windows while the agent works, if window locking is on. End it before handing the result to a person.
