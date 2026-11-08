# Changelog

## 0.3.0

**Code facts for agents.** Building a graph over an existing large project, agents spent most of a session reading code for facts a program can compute. The MCP server now answers them itself, without Unity, from one Roslyn pass over `Assets` that it keeps in memory and refreshes after files change:

- `code_evidence`: every reference between layers with source type, target type and file:line, grouped by layer pair with declared, undeclared, cycle and unevidenced status; owners of every type; same-layer references. The totals equal `DependencyEvidence`, which now runs on the same engine with unchanged output.
- `layer_facts`: a layer's files and types (kind, base type, interfaces, public members), its references to and from other layers with type pairs, and node candidates by rule (used or inherited by other layers, Unity entry points, extension points), each with file:line and the node it is linked to. No responsibilities are generated.
- `scope_what_if`: for types, files or folders moved to other layers, the undeclared pairs and cycles added and removed, the relationships that would lose their evidence and the linked nodes that would change layer. Changes nothing.
- `layer_dump`: a layer's nodes and relationships with ids, without layout, paged. `lookup`: layer, node and member ids by name.
- Project conventions are optional: `Architector/code-facts.json` with `genericCalls` (for example `Get` reports `Get<Container, Parameter>` per type) and `injectionAttributes` (for example `Inject` reports the injected parameter, field and property types). `layer_facts` shows their counts per type and the full lists with file:line for one `type`. Without the file nothing extra is computed.
- Replies are compact (counts and the most frequent type pairs); full lists come by `offset`/`limit` paging or `output: "file"`. The same answers are available from the command line: `dotnet run --project Tools~/DependencyEvidence -- <root> --facts=<action> --args=<json>`.
- The server binary grows from about 12 to about 19 MB (Roslyn and .NET reference assemblies inside); it still needs no .NET SDK.

**Plans and work sessions:**

- Semantic plans accept a node's title instead of its id: `"Node Title"` or `"Layer/Path::Node Title"`.
- **Behaviour change:** `nodeContracts` on a linked node keeps an omitted `members`, `baseType` or `interfaces`; an explicit empty list clears it. Before, an omitted list silently cleared the node's members. `dry_run_plan` and `apply_plan` return `warnings` listing every member and interface a plan removes.
- The revision keeps counting across a script reload instead of starting again at 1.
- After a script reload `work_status` shows `next_step`: call `resume_work` with the same work token. Skill and Workflow say so.
- `layer_graph` is paged: 40 nodes and 40 relationships per reply by default, with `offset`, `limit` and `next_offset`.

**Editor:**

- Editing nodes and relationships is about 15 times faster: on 50 layers an edit with the window's refresh drops from about 3 s to 0.2 s. Only the layers an edit touches are cloned, validated, encoded and reinstalled; approval hashes and conformance are recomputed only where they can change.
- `layer_graph` with `detail=summary` returns the layer as compact text: paths and names instead of IDs, no empty fields, incoming relationships folded into one line per source layer.
- **Settings > Workspace Geometry > Fit Layer On Open** (on by default): opening a layer runs Focus View (**F**) instead of returning to the last saved position, and runs it again while the canvas settles (the inspector appearing, a window resize) until you move the view.
- **Arrange** in the Explorer context menu: on a layer, on several selected layers and on a group (every layer below it), one layer at a time with a progress bar at the bottom of the canvas, inside the window: **Stop** keeps the arranged layers, **Cancel And Undo** returns them; editing waits until it ends. Several layers or a group skip layers already arranged whose nodes, sizes and relationships have not changed since; **Arrange All Again** on a group arranges them anyway. The mark lives in the local user state, which agents never read.
- **Changes since approval** rows open what changed: a click selects and focuses the node, Portal or relationship. Removals and changes of the layer itself are not clickable.
- **Ctrl+F** with the pointer on the canvas opens a search bar in its top-left corner: nodes whose title or linked type name contains the text are selected and stay at full strength while the rest of the canvas fades to about a third. **Enter** focuses the found nodes, **Esc** or **×** closes the search.
- **1** toggles the zoom: to 100%, and pressed again, to the farthest zoom out, around the canvas center.
- The active layer tab stays visible when the inspector slides out and narrows the tab strip.
- Conformance messages wrap inside their box again; after a script reload the window kept a copy of the message style without word wrap.
- The Unlock and Lock windows grow with their content: after a wrong password the buttons stay visible, and the password field is cleared and focused for another try.
- Planned and By Agent next to the layer name sit on the line of the title.
- Renaming a layer, in the window or through a semantic plan, retitles the Portals that lead to it; their stored titles used to keep the old name. `ArchitectorRepository.RefreshAllPortalTitles()` repairs titles left stale by earlier renames.
- The Hotkeys button (**?**) moved from the header to the canvas's top-right corner, clear of the layer tabs and the inspector, and moves with the inspector. In the header the agent-work button covered it.
- **Settings > Arrange > Improvement Time** (5 s by default) limits how long Arrange improves a layer; the layout itself is always complete.
- An available update shows its version in yellow and a warning icon in Settings and on its integration in Integrations; the change list is a foldout sized to its text.
- The Project panel shows **Review (N)** when layers are changed, approved by an agent or in draft.
- After you drag nodes, the inspector keeps what it shows: a click only selects, and clicking the selected node or relationship again opens it. Clicking empty canvas or opening a layer returns to one click.
- After a click on a layer in Explorer, Up and Down open the previous and next layer of that list in one preview tab; a click on the canvas gives the arrows back to node navigation.
- **Turn Checks Off** on a group (context menu): conformance skips its layers and says how many it skipped, dependency cycles inside them are allowed, and they cannot be approved. A relationship between such a layer and a checked one is a conformance error and blocks approval of the checked layer. Only a person can switch it, never in locked groups or groups with approved layers. Listed in **Settings > Rules**.

## 0.2.7

- **Settings > Rules > Dependency Cycles.** Reject (default) refuses a dependency cycle and names it; Report keeps cycles that exist in the code and lists them as warnings.
- Relationship lines follow the node grid and stay evenly spaced; merged lines also share outgoing ends, and merged lines no longer overlap.
- Agents arrange once at the end of their work with `arrange_changed_layers`; Arrange itself is faster.

## 0.2.6

- Package updates automatically install or repair the matching SHA-256-verified MCP server after reload, also when received through Git. Existing default Codex/Claude connections for the project move to the new server; custom connections are preserved. Setup failures expose a retry action and retain the previous version.
- Added Auto Cleanup, on by default, for unused owned server versions. Current/previous versions, registered projects, known client references and running binaries are retained; unknown files and symbolic-link paths are skipped.
- Integrations share an optional update-provider contract and common status, revalidation, guarded installation, error/retry handling and reload recovery. Architector and Lazy Mode use one provider.
- Arrange settings are inside Workspace Geometry.

## 0.2.5

- Update shows the latest compatible release under Available even when it is already installed. A green check after the header confirms a successful check, current packages and the matching installed MCP server.
- Integrations keeps Setup Check above a searchable catalog with tag buttons, name/tag queries and paging. Lazy Mode appears before the Architector MCP server.
- Extensions can register catalog entries with stable IDs, tags and detail controls without changing the Integrations window. MCP probes use the same catalog and can declare extra tags.

## 0.2.4

- Package version labels and update completion refresh after Package Manager registers a change, even when Unity skips assembly reload because no scripts changed.

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
