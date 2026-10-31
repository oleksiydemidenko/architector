# Agent Setup

## 1. Choose Your Access

**File access works with every agent that has a shell.** Nothing to install beyond the .NET SDK. The agent reads the digest and runs `context_route`.

**Editor access adds graph editing.** Install one of the supported MCP servers and connect your agent to it:

- [MCP for Unity by Coplay](https://github.com/CoplayDev/unity-mcp): tools `architector_query` and `architector_edit`.
- [Unity MCP by Ivan Murzak](https://github.com/IvanMurzak/Unity-MCP) 1.6.0 or later: tools `architector-query` and `architector-edit`. Both take one `parameters` string with a JSON object, for example `{"action":"project_summary"}`.

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
