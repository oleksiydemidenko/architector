# Architector

Architecture for Unity projects that both people and coding agents can read and edit. Architector keeps your architecture as a layered graph in plain JSON files next to the code. People edit it in a Unity window, agents edit it through MCP tools, and conformance checks compare it with the compiled code.

It answers one question: *what governs this piece of code, and what may it depend on?*

> [!WARNING]
> **Experimental, in active development.** Architector is being tried on several projects, and each first run still turns up problems that are then fixed. Versions are `0.x`: the file format, the MCP actions and the UI may change between minor versions, and not every change comes with a migration. Pin a release tag, keep the `Architector/` folder in Git, and expect rough edges. Reports of what breaks or confuses you help most right now.

## What It Is

- **A layered graph.** Groups organise layers, and each layer describes one responsibility with typed nodes and relationships (`dependency`, `dataFlow`, `event`, `callback`). Cross-layer links go through Portals.
- **Linked to the code.** A node can be linked to a C# type and selected members. Conformance reports when the compiled code no longer matches.
- **Reviewed.** A layer can be approved as the agreed design. Changes to an approved layer mark it as changed until someone reviews them; layout changes do not.
- **Readable by agents.** An MCP server, a deterministic text digest of the approved layers, and a router from a file to its layer let an agent read only what it needs.
- **Laid out for people.** Orthogonal routing on the node grid, merged lines for shared ends, and Arrange for new layers.

It is not a code generator and it does not prove behaviour: conformance checks structure, not method bodies or gameplay.

## Who It Is For

Unity teams, often small, that work with coding agents and want the agent to follow an agreed architecture instead of rediscovering it from the code every time. It pays off in projects with several systems and more than one person or agent changing them.

## Install

Requires Unity 6000.3 or later.

In Unity open **Window > Package Manager > + > Add package from git URL** and enter a pinned version:

```
https://github.com/oleksiydemidenko/architector.git#v0.3.2
```

The MCP server is installed from inside Unity. The .NET SDK is needed only for the `DependencyEvidence` and `ContextRoute` command-line tools.

## Getting Started

1. Open **Window > Architector > Window**.
2. In **Window > Architector > Settings**, set the source roots to index, for example `Assets/Scripts`.
3. Create a group and a layer, add a node and link it to a type from your project.
4. Commit the `Architector/` folder.
5. Open **Window > Architector > Integrations**, check **Setup Check**, press **Install Server** and connect your agent. See [Agent Setup](Documentation~/AgentSetup.md).

[Workflow](Documentation~/Workflow.md) explains how people and agents share the graph: work sessions, approval, dependency cycles, and when to arrange.

## Working Without Unity

Reading works from files; writing needs the Editor, because validation, the type index and Undo live there.

| Tool | Needs Unity | Purpose |
| --- | --- | --- |
| `architector_status`, `architector_route`, `architector_digest` (MCP) | no | Project state, routing and the approved architecture |
| `context_route` | no | From a file or type to its layer, digest lines and documents |
| `DependencyEvidence` | no | Code dependencies between layers that the graph does not declare |
| `architector_query` code facts: `code_evidence`, `layer_facts`, `scope_what_if`, `layer_dump`, `lookup` | no | Who owns which types, every reference between layers with file:line, node candidates, what a type reads, writes and subscribes to, where its scripts are wired in prefabs, scenes and assets, what a scope move would change, layer ids by name. Answered by the server itself, the same totals as `DependencyEvidence` |
| `architector_query` | yes | Layers, approval, conformance and type queries |
| `architector_edit` | yes | Work sessions, plans, arrangement and approvals |

The server binary is also a command line for agents and scripts that can run shell commands, with the same JSON replies: `architector-mcp facts layer Core/Input`, `architector-mcp query layer_graph --target Core/Input --detail summary`, `architector-mcp edit dry_run_plan --plan plan.json`, `architector-mcp doctor`, `architector-mcp help`. `architector_status` gives the binary's path. `architector-mcp skill install claude` copies the authoring skill into the project.

Keep **Project Settings > Player > Run In Background** on when agents talk to the Editor; otherwise it stops answering while its window has no focus.

## Known Limitations

These are known gaps, each tracked for a future version.

- A node without relationships cannot be approved, even when it is a real standalone entry point.
- Only your own source roots are indexed: Unity and other external types cannot be linked as verified contracts.
- Relationships wired in the Inspector (UnityEvent, Timeline, prefab overrides) are not visible to Architector.
- `DependencyEvidence` and the code facts check dependencies between layers rather than individual types and read only `Assets/`; the command-line tool needs the .NET SDK, the server does not. The server analyses the code when it starts (seconds; longer on thousands of files) and again on the next code facts call after any C# or Architector file changed.
- Conformance does not notice changed method bodies, so descriptions of behaviour can go stale silently.
- On Windows, a project in a deeply nested folder can exceed the 260-character path limit for the files in `Architector/Links`.
- Arrange takes several seconds on layers with about 60 relationships.

## Versioning And Releases

Architector follows [Semantic Versioning](https://semver.org/) in its `0.x` form: a minor version may contain breaking changes, a patch version does not. Every change is listed in the [Changelog](CHANGELOG.md). Every release is a [GitHub release](https://github.com/oleksiydemidenko/architector/releases) with the MCP server built for Windows, macOS and Linux. **Settings > Update** checks for compatible releases and updates the package and its server together.

## Documentation

- [Agent Setup](Documentation~/AgentSetup.md): connect Claude Code, Codex, Cursor or another agent.
- [Workflow](Documentation~/Workflow.md): how people and agents work with the graph.
- [Adding an Agent Integration](Documentation~/AddingAgentIntegration.md): write an adapter for another MCP server.
- [Authoring Skill](Skills~/architector-authoring/SKILL.md): conventions for agents that write architecture.

## Feedback And Contributing

Please open an issue when something fails, surprises you or is hard to understand; say what you expected and which Unity version you use. Open an issue before a large pull request. Run the EditMode tests in `Architector.Editor.Tests` and, if you touched the adapter, `Architector.Mcp.Editor.Tests`.

## License

[MIT](LICENSE.md)
