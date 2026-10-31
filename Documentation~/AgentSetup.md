# Agent Setup

## 1. Choose Your Access

**File access works with every agent that has a shell.** Nothing to install beyond the .NET SDK. The agent reads the digest and runs `context_route`.

**The Architector MCP server works with every MCP client.** It is one small program for Windows, macOS or Linux. It needs no .NET SDK and no other Unity MCP package.

1. Open **Window > Architector > MCP Server** and press **Install Server**. The server goes to `~/.architector/mcp/<version>/`.
2. Copy the settings for your client from the same window:
   - Claude Code: a `claude mcp add` command.
   - Codex: a block for `config.toml`.
   - Cursor and other clients: a block for their MCP JSON file.
3. Restart the client if it does not pick the server up.

| Tool | Needs the Editor | Purpose |
| --- | --- | --- |
| `architector_status` | no | Which project is served, and whether the Editor is reachable |
| `architector_route` | no | From a file or type to its layer, digest lines and documents |
| `architector_digest` | no | The list of approved layers, or one layer's section |
| `architector_query` | yes | Project, layer, approval, conformance and type queries |
| `architector_edit` | yes | Work sessions, dry runs, plans and approvals |

You can also download the program for your system from the [releases page](https://github.com/oleksiydemidenko/architector/releases). The server finds the project from its working directory, from `--project <path>` or from the `ARCHITECTOR_PROJECT` variable. So one global entry in a client works for every Unity project you open there, if the client starts servers in the project folder.

The server runs on the machine with the project. It talks to the Editor through a bridge that listens on the loopback interface only and accepts a token that the Editor writes to `Library/Architector/agent-bridge.json` for each session. Turn it off in **Window > Architector > Settings > AI Integration**.

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
