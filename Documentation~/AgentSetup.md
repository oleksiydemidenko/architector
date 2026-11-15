# Agent Setup

## 1. Choose Your Access

**File access needs an agent with a shell.** Nothing to install beyond the .NET SDK. The agent runs `context_route`.

**The Architector MCP server is for MCP clients.** It is one program for Windows, macOS or Linux. It needs no .NET SDK and no other Unity MCP package.

1. Open **Window > Architector > Integrations** and press **Install Server**. The server goes to `~/.architector/mcp/<version>/`.
2. In the same window press **Connect** next to Claude Code or Codex. This needs their command line tool. Cursor and other clients take a block for their MCP JSON file from **Manual Setup**.
3. Restart the client if it does not pick the server up.

| Tool | Needs the Editor | Purpose |
| --- | --- | --- |
| `architector_status` | no | Which project is served, and whether the Editor is reachable |
| `architector_route` | no | From a file or type to its layer, digest lines and documents; for the node it names, the types that write and read its state |
| `architector_digest` | no | The approved layers: without layers an index by group with node counts; full sections for named layers, a compact form for groups (node, type, one-line role, writers and readers from the code) |
| `architector_query` | yes | Project, layer, approval, conformance and type queries |
| `architector_edit` | yes | Work sessions, dry runs, plans and approvals |

You can also download the program for your system from the [releases page](https://github.com/oleksiydemidenko/architector/releases). The server finds the project from its working directory, from `--project <path>` or from the `ARCHITECTOR_PROJECT` variable. So one global entry in a client works for every Unity project you open there, if the client starts servers in the project folder.

The server runs on the machine with the project. It talks to the Editor through a bridge that listens on the loopback interface only and accepts a token that the Editor writes to `Library/Architector/agent-bridge.json` for each session. Turn it off in **Window > Architector > Settings > AI Integration**.

**If you already use a Unity MCP server,** Architector can add its tools there instead:

- [MCP for Unity by Coplay](https://github.com/CoplayDev/unity-mcp): tools `architector_query` and `architector_edit`.
- [Unity MCP by Ivan Murzak](https://github.com/IvanMurzak/Unity-MCP) 1.6.0 or later: tools `architector-query` and `architector-edit`. Both take one `parameters` string with a JSON object, for example `{"action":"project_summary"}`. This plugin starts in cloud mode and logs an authorization error until you sign in or switch it to a local server.

Install the package and press **Add Connection** for it in **Window > Architector > Integrations**. Check the state in **Window > Architector > Settings > AI Integration**.

Keep **Run In Background** on in Player settings, or the Editor stops answering while its window has no focus.

Another MCP server needs a small adapter: see [Adding an Agent Integration](AddingAgentIntegration.md).

## Updates

Open **Window > Architector > Settings > Update**. It checks when Settings opens, at most once a day if **Check Automatically** is on. **Check for Updates** checks immediately. **Include Prereleases** is on by default while Architector is in development. A ready update opens the foldout and marks it with a warning icon; you can collapse it again.

The section shows the installed version and the latest compatible published version under **Available**, including when both are the same. A green check after the Update header means the release check succeeded, the packages are current and the matching MCP server is installed. An optional Lazy Mode package must match Architector when installed. An unchecked or failed check does not show the green check.

It offers an update only after its server and checksum files are published for your system and its Unity requirements are satisfied. **Update** updates Architector and an already installed Lazy Mode package together; Unity reloads assemblies when scripts change. Finish text edits and end agent work before updating. Play Mode, compilation, unsaved Editor windows and other installations block the operation.

After a package update, Architector automatically installs or repairs the matching server, including after `git pull`. It waits for compilation, package operations and active agent work to finish. Downloads must pass the release's SHA-256 check. Existing default Codex/Claude registrations for this project are moved to the matching server; restart those clients afterward. Registrations with custom arguments, environment or working directory, and registrations for other projects, are left for their owner to manage. A failed move attempts to restore the previous registration. Initial connections and manual-client configuration remain in **Integrations**.

If automatic server setup fails, Update shows the error and **Retry Server Setup**. The previous server is retained; failed setup is not retried in a loop. **Install Server** in Integrations remains available for repair. Commit `Packages/manifest.json` and `Packages/packages-lock.json` to share the package version with the team. Agent Checks is a separate package and is not changed by this updater.

**Auto Cleanup** is on by default. After successful server setup it removes unused recognized server versions, keeping the current/previous versions, registered project references, known client references and running servers. The local `.architector/mcp/.projects` records track project usage. Unknown files and symbolic-link paths are preserved; cleanup problems are shown without failing a successful installation. This does not clear Unity's general package cache. Projects or manually configured clients unknown to this machine's records cannot be fully discovered; keep a needed version referenced or disable Auto Cleanup when managing such installations externally.

A `file:` or embedded installation is shown as a local clone, with no update offer. Update that clone with Git; its path is local to that machine. Update preferences and cached results are local Editor preferences and never modify the architecture or approval state.

Arrange settings are under **Settings > Workspace Geometry > Arrange**.

## Integration Catalog

**Integrations** keeps Setup Check at the top and lists extensions and MCP transports below it. Lazy Mode comes before the Architector server. Entries expand to show their existing installation, activation and connection controls. Search names or tags, combine terms such as `MCP Coplay`, or use `tag:Extension` and `#MCP`. The tag buttons combine with text search. Lists longer than six entries have page controls and support PageUp/PageDown when you are not editing text. Searching resets to the first page and preserves each entry's expanded state.

## 2. Tell The Agent

Add this to your `AGENTS.md`, `CLAUDE.md` or Cursor rules. Keep it short: every line is read in every session.

```markdown
## Architecture

Architecture lives in `Architector/`. Do not edit its JSON by hand.

- Search the code for names and text. For what code does not show (which layer owns a file,
  where a component is placed and which serialized lists hold it, who writes or reads a state type) run
  `dotnet run --project <architector package>/Tools~/ContextRoute -- . <file or type>`
  or, with the server installed, `architector-mcp route <file or type>`.
- `Architector/Architecture.digest.md` is for people. Do not read it whole; `architector-mcp digest <layer>` gives one layer.
- To change the architecture, use `architector_edit` with a work session. Saving does not approve.
  Architector asks the user to confirm every approval.
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

## 5. Optional: Checks After Edits (Claude Code)

When the project has absence rules (`rules.startedBy` in `Architector/code-facts.json`), the agent can be told about a broken rule right after it edits a prefab, scene or script:

```
architector-mcp skill install claude --hooks
```

This adds two hooks to `.claude/settings.local.json` (your own settings, not committed): `check` for the file after an Edit or Write of a `.cs`, `.prefab`, `.unity` or `.asset` file, and `check` for the files git sees as changed when the agent stops. Both print nothing when the rules hold; findings reach the agent, and stopping a second time always goes through. On a fresh clone the check says the code index is being built and does not wait. Edits made through Unity tools instead of Edit or Write are caught by the stop hook. `architector-mcp skill hooks` shows whether they are installed; turn them off with `architector-mcp skill hooks remove`, or delete the two entries with `check --hook` from that file. `skill sync` points them at the current server version.

Every call of the server, the command line and the hooks is logged to `Library/Architector/calls.jsonl` with its size and duration, for measuring how agents use the tools; `ARCHITECTOR_CALL_LOG=0` turns it off.

## Several Agents

Only one agent work session can exist per Editor. A session locks the Architector windows while the agent works, if window locking is on. End it before handing the result to a person.
