# Architector

Machine-readable architecture for Unity projects. Architector keeps your architecture as a layered graph in plain JSON files next to the code: groups, layers, typed nodes, relationships, approvals and code scopes. People edit it in a Unity window. Coding agents read and edit it through tools. Conformance checks compare it with the compiled code.

It is built around one question: *what governs this piece of code, and what may it depend on?*

## What You Get

- **An architecture graph.** One layer describes one responsibility. Cross-layer links go through Portals, and only dependency relationships must stay acyclic.
- **Arrange.** One command lays a layer or a selection around its most connected nodes, in a compact grid with short routes.
- **Approval.** A layer can be approved as canon. Semantic changes mark it as changed until someone reviews them. Layout changes do not. Architector asks you to confirm every approval an agent requests.
- **A digest.** `Architector/Architecture.digest.md` is a deterministic text summary of the approved architecture, generated with every commit of the graph.
- **Conformance.** Linked nodes are checked against compiled types and selected members.
- **Dependency evidence.** A command-line tool compares real code dependencies with the declared ones.
- **Routing without Unity.** `context_route` goes from a file or type to its layer, its digest lines and the documents worth reading.
- **Agent access.** An MCP server for Windows, macOS and Linux, plus adapters for MCP for Unity by Coplay and Unity MCP by Ivan Murzak. Agents with a shell can use the command-line tools instead.

Architector models contracts, not everything. It does not check lifecycle behaviour, method bodies or gameplay correctness.

## Install

Unity 6000.3 or later. The MCP server needs nothing else. The .NET SDK is needed only for `DependencyEvidence` and for running `context_route` directly.

In Unity open **Window > Package Manager > + > Add package from git URL** and enter:

```
https://github.com/oleksiydemidenko/architector.git
```

## Getting Started

1. Open **Window > Architector > Window**.
2. Open **Window > Architector > Settings** and set the source roots to index, for example `Assets/Scripts`.
3. Create a group and a layer, then add a node and link it to a type from your project.
4. Commit the `Architector/` folder. It belongs in version control.
5. Open **Window > Architector > Integrations**, check **Setup Check**, press **Install Server** and connect your agent. See [Agent Setup](Documentation~/AgentSetup.md) for the details.

## Working Without Unity

Agents with a shell can read the architecture without an open Editor. Run from the project root:

```
dotnet run --project Packages/com.oleksiydemidenko.architector/Tools~/ContextRoute -- . Assets/Scripts/Player/PlayerMovement.cs
```

A package installed from Git lives in `Library/PackageCache/com.oleksiydemidenko.architector@<hash>/` instead of `Packages/`.

Reading works from files. Writing to the graph needs the Editor, because validation, the type index and Undo live there.

| Tool | Needs Unity | Purpose |
| --- | --- | --- |
| `context_route` | no | From a file or type to its layer, digest lines and documents |
| `Architecture.digest.md` | no | Approved architecture as text |
| `architector_status`, `architector_route`, `architector_digest` (MCP) | no | Project state, routing and the digest for MCP clients |
| `DependencyEvidence` | no | Undeclared layer dependencies, from sources |
| `architector_query` (`architector-query` with Ivan Murzak's server) | yes | Project, layer, approval, conformance and type queries |
| `architector_edit` (`architector-edit`) | yes | Work sessions, dry runs, plans and approvals |

Keep **Project Settings > Player > Run In Background** on when agents talk to the Editor. Otherwise the Editor stops answering while its window has no focus.

## Documentation

- [Agent Setup](Documentation~/AgentSetup.md): connect Claude Code, Codex, Cursor or any other agent.
- [Workflow](Documentation~/Workflow.md): how people and agents work with the graph.
- [Adding an Agent Integration](Documentation~/AddingAgentIntegration.md): write an adapter for another MCP server.
- [Authoring Skill](Skills~/architector-authoring/SKILL.md): conventions for agents that write architecture.

## Status

Architector is in development. It is developed on Unity 6000.3.16f1 on Linux. The MCP server has been used with Claude Code and Codex.

## Contributing

Issues and pull requests are welcome. Please open an issue before a large change. Run the EditMode tests in `Architector.Editor.Tests` and, if you touched the adapter, `Architector.Mcp.Editor.Tests`.

## License

[MIT](LICENSE.md)
