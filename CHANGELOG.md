# Changelog

## 0.2.3

- Added **Relationship Merging** under Settings > Optimization > Canvas: Off (default), 3 or more, 2 or more, and Maximum. Incoming relationships of one type and target side grow a shared route tree by short added connections. Long runs are shared; junction dots mark real merges rather than a terminal rail before the node. Different networks occupy separate tracks, and ordinary crossings keep their bridges. All enabled modes combine parallel relationships with matching type and preferred sides into a thicker arrow, preserving their identities; Maximum uses the same threshold as 2 or more.
- Shared strokes draw once, retain branch selection, cycle member relationships on repeated trunk clicks, and list members in the Relationship Inspector. Node highlighting, background routing, drag previews and Arrange use the shared geometry.

- Authoring instructions omit node coordinates and finish new layers with Arrange. The Coplay MCP adapter exposes the layer, selection and center parameters for `arrange_layer`.
- Settings starts with Update: automatic daily checks and manual checks, a prerelease option, intervening changelog entries, and an explicit update button. An available update opens the section and marks its header with a warning icon.
- Updates require the platform's MCP server and checksums to be published, and compatible Unity requirements. Architector and an installed Lazy Mode package update together through Package Manager. Local clones are left to Git.
- Install Server verifies the downloaded server against the release's SHA-256 checksum before replacing an existing file. Package updates, server installation and client reconnection remain explicit actions.
- Setup Check refreshes after server and package installation, preference changes and when the Integrations window regains focus.

## 0.2.2

- Settings opens only AI Integration and Utilities after a recompile.
- Relationship routes no longer make small loops next to a node. A route may turn into its port from any direction, ports are part of the routing grid, and separating parallel lines keeps every segment's direction.
- Routes choose among the node sides that face each other, and facing nodes connect with straight lines where their sides overlap. On a 59-layer graph this removes all loops, 39% of turns and 48% of crossings.
- A full routing pass takes about three times as long, 66 ms on the densest layer of that graph, measured in Mono. It runs when a node is dropped or the graph changes, not during a drag.
- Lines now bridge each other where they cross. Usually the horizontal line hops; next to a corner the corner shrinks or the vertical line hops instead, and crossings close together share one bridge.
- Neighbouring ports swap when that removes a crossing or two lines sharing one track, which also undoes small knots on large nodes.
- The canvas does less on every repaint: nodes outside the view are skipped, member rows, fitted text and icons are cached, navigation state is compared without JSON, and the mouse wheel no longer writes a file on every step.
- Added **Route In Background** to Optimization, on by default: relationships are calculated on a background thread after a change and appear a moment later, without stalling the window. A new change replaces a calculation that is still running.
- Added **Hide Routes While Dragging** to Optimization, on by default: the relationships of a dragged node are hidden and appear again once routed after the drop. With it off, only the relationships of moved nodes are routed on each step.
- Arrange checks its layouts with the real router, which leaves 39% fewer crossings on the same graph. The densest layer takes about a second.

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
- Added **Focus View** to the canvas context menu; Arrange also focuses its result.
- Added hotkeys through the Unity Shortcuts Manager. The **?** button in the window header lists the current keys, marks unassigned or repeated ones, and opens **Edit > Shortcuts**.
- Added the `node`, `relationships`, `dependency_path` and `setup_check` queries, `detail=summary` for `layer_graph`, and `offset` with `next_offset` for long results. `truncated` is replaced by `next_offset` in `find`, `type_search` and `type_contract`.
- Added the `arrange_layer` and `relink_node` actions. A failed plan names the failing `operation`, and an unknown or ambiguous layer returns candidates. The MCP server lists its actions and parameters in its tool schemas.
- Added **Relink Type** to the node context menu, a **Setup Check** in **Integrations** and **Window > Architector > Review** for approving layers in bulk.
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
