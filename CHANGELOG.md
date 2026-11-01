# Changelog

## 0.2.1

- Fixed semantic plans failing with "Layer Portal pair must contain exactly two endpoints" when they delete the last relationship between two layers.
- `dry_run_plan` no longer changes the session state.
- A cross-layer edge in a semantic plan now creates the missing Portal pair. `portals` is optional and only sets the position of a pair, new or existing.
- The Architector window shows the package version.
- `capabilities` gives the real path of `DependencyEvidence`.
- `approve_layers`, `revoke_approvals` and `revert_to_approved` from an agent now need the user's confirmation in the Editor.
- Workflow modes can be added by another package. A mode can let the agent approve, shows a notice in the window header, and the baseline records that the agent approved the layer.
- Moving a node, or changing relationship visibility, no longer rewrites and rechecks the whole architecture.
- An agent request that times out before the Editor starts it is cancelled instead of running later.
- Context menus open at the cursor when another Editor window was drawn last.
- Replaced **Window > Architector > MCP Server** with **Window > Architector > Integrations**, a tab next to the Architector window. It installs the server, connects Claude Code and Codex with one button and lists the optional bridges. Other Unity MCP packages now need **Add Connection**.
- Settings groups are foldout boxes.
- Optional workflow modes are listed in **Agent Workflow** even when they are not installed, and **Integrations** installs, turns on and removes them.
- Added **Arrange** to the canvas context menu: it lays a layer or the selected nodes around their most connected nodes, in groups limited by **Settings > Arrange**. **Arrange As Centers** uses the selected nodes as the centers.
- A new relationship router: every relationship gets its own port, parallel routes run on separate tracks, corners are rounded, and routes are also rebuilt while dragging. Selected relationships are drawn above the others. The Grid Routing and High Quality Drag Routing settings are removed.

## 0.2.0

- The Architector MCP server is now a standalone program for Windows, macOS and Linux. It needs no .NET SDK.
- Added **Window > Architector > MCP Server**: installs the server and gives ready settings for Claude Code, Codex, Cursor and other clients.
- The server finds the Unity project from its working directory, or takes `--project`.
- Added `architector_status` and `architector_digest`, and server instructions for agents.

## 0.1.3

- Fixed the Editor bridge not starting in some projects.

## 0.1.2

- Added the Architector MCP server (`Tools~/ArchitectorMcp`): any MCP client connects directly, without a third-party Unity MCP package.
- Added a local Editor bridge for it, bound to the loopback interface and protected by a per-session token.

## 0.1.1

- Added an adapter for Unity MCP by Ivan Murzak (`architector-query`, `architector-edit`).
- Fixed `context_route` failing on a new project that has no groups or links yet.

## 0.1.0

First public version.

- Layered architecture graph with groups, layers, typed nodes, Portals and relationships.
- Approval baselines, a deterministic digest, conformance against compiled code and dependency evidence from sources.
- Transport-neutral agent API with an adapter for MCP for Unity by Coplay.
- `context_route`: file-based routing from a file or type to its layer and documents, without Unity.
