# Changelog

## 0.3.7

Code facts checked against a large approved graph: wiring statuses that were wrong, behaviour facts that missed or misread code, and two new tools for checking a graph against the facts. Existing fields keep their names and meaning; new fields are additive. Both caches (`code-index-*.bin` and `wiring-index.json`) change format and rebuild by themselves on the first call.

**Wiring facts:**

- **Script assets are found from the file that declares the type.** Every part of a partial type counts, a file name that differs only in letter case counts, and so does the only placeable class of a file with another name. Such a type is `wired` when assets use the GUID. `script_name` names the file and the match (`exact`, `case`, `single_class`); `name_warning` explains the risk of the last two.
- **Abstract and generic types are never `wired` themselves.** A generic base that shares its file and name with the placed class (`Turret<T>` and `Turret`) is `base_only`.
- **Prefab-variant overrides count.** A `[SerializeReference]` value that a variant adds by override (`managedReferences[<rid>]`) places its type; the variant appears in `places` with `"via": "variant_override"`.
- **Managed references list their places.** `places` names the file and the object of every `[SerializeReference]` use (`"via": "managed_reference"`), as for MonoBehaviours.
- **Types only `[SerializeReference]` can store.** A `[Serializable]` class that fits a `[SerializeReference]` field, is never a by-value serialized field and is never created with `new`, is `unwired` when no asset stores it; `siblings_in_assets` counts the stored types of the same field. When a condition fails, the type stays `not_tracked` and `note` says why.
- **`created_by_reflection`.** A method that lists types with `GetTypes`, filters them by a project base type and creates them with `Activator.CreateInstance` makes the matching subclasses `created_by_reflection` instead of `unwired` or `not_tracked`. `code_evidence` lists the sites in `reflection_sites`.
- **`base_of` has a total and pages:** `base_of_total`, and `section=base_of` with `offset`/`limit`.
- **Test places take file patterns.** `wiring.testPlaces` (the clearer name of `testFolders`) takes globs over full paths, for example `**/* OLD.prefab`. `layer_facts` reports the active patterns in `wiring_test_places`.

**Behaviour facts:**

- **Writes.** A non-generic overload of a `genericCalls` method (`Add(instance)` next to `Add<T>()`) is listed like the generic call. New `memberCalls` rules in `code-facts.json` list calls on receivers of a given type, for example `Set` on a parameter base.
- **`assigns` leaves out copies.** Assignments to locals, by-value parameters and struct copies are no longer reported as another type's state.
- **Unity callbacks only on Unity types.** `lifecycle`, `unpaired` and `loops` appear only on `MonoBehaviour` and `ScriptableObject` types.
- **`recurring`:** per-frame work outside `Update` with file:line: frame waits in loops (`UniTask.Yield`, `NextFrame`, `DelayFrame`, `yield return null`), polling calls (`UniTask.WaitUntil`, `WaitWhile`, `EveryUpdate`) and the calls named in `recurringCalls`. `recurring_note` states what stays unseen.
- **Inherited callbacks pair.** `unpaired` counts the callbacks of project base classes; `inherited_lifecycle` names the base that supplied the pair.

**Graph data and conformance:**

- **Nodes with Unity's full `metadataName` are linked again.** The code facts doubled the namespace (`Ns.Ns.Type`), so `node_candidates[].node` stayed `null` and `facts dump` showed the doubled name. `node_candidates` also gets `node_title`.
- **An allowed cycle inside one layer no longer lowers the state.** It is a `dependency_cycle` diagnostic with the new severity `info`. Conformance answers add `counts.info` and `warning_kinds`, so an `Incomplete` state names its causes.

**Command line and plans:**

- `--output file` writes the full answer, every list unpaged, unless `--limit` is given.
- List parameters take a repeated flag (`--layers A --layers B`) or a comma list (`--layers A,B`).
- A linked plan node may name its type by `typeNamespace` and `typeName`; failures name the fields and list candidate keys.
- A responsibility over 240 characters fails with `operation` naming the plan item.
- `layer_facts` with a short `type` name shared by a generic and a non-generic type picks the non-generic one, and several matches fail with `type_ambiguous` and the candidates instead of an internal error.

**New tools:**

- **`facts audit [<layer>]`** (MCP `facts_audit`): the contradictions between the graph and the facts, each with node, type and evidence: nodes on `unwired`, `test_only` or `created_in_code` types, often used or extension types without a node (`--min-uses`, default 20), dependency relationships without evidence or drawn on types that hold none of the references, nodes without relationships and without a standalone mark.
- **`facts snapshot`** and **`facts diff <a> [<b>]`** (MCP `facts_snapshot`, `facts_diff`): save the facts under `Library/Architector/snapshots` and list what changed between two snapshots or since one: types added and removed, owner, wiring, behaviour, member and evidence changes.

## 0.3.6

- **The 0.3.5 changes are published.** The 0.3.5 release was not published, because its Windows build failed a test. The test compared the project path as text. On Windows, the temporary folder can come as a short 8.3 path (`RUNNER~1`) while the server reports the long path. The test now checks that both paths point to the same folder. The package code is the same as in 0.3.5.

## 0.3.5

- **The digest and code facts say what is checked.** The digest header has two lines: what is checked against code (linked types, contract members, base types and interfaces, `dependency` relationships between layers with a code scope) and what is authored and not checked (purposes, responsibilities, event, callback and dataFlow relationships). Code facts answers carry `project`, the root the server reads. The wiring summary of `layer_facts` has `wiring_note`: not tracked means no evidence either way.

- **Indexing project types no longer freezes the window.** The first loading step used to be one main-thread call: reflection over every compiled assembly and reading and parsing every source in scope. Now only the Unity calls run on the main thread (the assembly list and script GUIDs); reflection, reading and parsing run on worker threads, in parallel, while the loading bar moves with the files done. Reflection covers only the assemblies that hold indexed sources; other types are looked up on first use. Assembly names are read once per assembly instead of once per type. Looking up an indexed type by key no longer scans the list.

## 0.3.4

**Behaviour changes:**

- **Packages referenced as projects now bind.** When Unity generates project files for packages and plugins, `Assembly-CSharp.csproj` refers to them as `<ProjectReference>`, and 0.3.3 left their types unseen. Every `ProjectReference` is now followed transitively:
  - A project whose sources lie under `Assets` is the project's own and is skipped, because the index compiles those sources itself.
  - Any other project is read from `Library/ScriptAssemblies/<AssemblyName>.dll`, or, when Unity has not compiled it yet, compiled from its sources.
- **Relative `HintPath` entries are found.** They are now resolved from the project file's folder, not from the folder the server runs in. Before, package assemblies in `Library/ScriptAssemblies` were silently left out.
- **Totals.** With these two changes, members inherited from package base classes bind to the package. Totals change only where binding becomes correct. `code_evidence` and `DependencyEvidence` stay equal.

**`binding` in `code_evidence`:**

- `project_references` with `compiled`, `from_sources` and `missing`.
- `missing_assemblies`: assemblies that are neither compiled nor available as sources.
- `unseen_base_examples`: each base class that stays unseen, with one type that inherits it and how many do.
- `hint` names the missing assemblies first.

**Wiring facts:**

- Managed references whose `type:` line Unity wrapped onto the next line are read, and so is the block form. The wiring cache is rebuilt once.
- Every type now has a wiring status, and the layer summary counts each one:

  | Status | Meaning |
  | --- | --- |
  | `wired` | Placed, or managed-referenced, in a scene, prefab or asset |
  | `test_only` | Used only in test or sample folders |
  | `base_only` | An abstract, generic or other base class that placed types inherit; `base_of` lists them |
  | `created_in_code` | Created by `AddComponent<T>` or `CreateInstance<T>`; `reason` gives the line |
  | `unwired` | A script type that nothing places, creates or inherits from; `reason` explains abstract and generic ones |
  | `not_tracked` | Assets cannot show it (serialized by value, or not serialized); its absence proves nothing |

- Editor-only `ScriptableObject`s (windows, inspectors, drawers, importers, settings providers) are no longer counted as placeable.

## 0.3.3

0.3.2 was tagged but never released: its server build failed, because the trimmed server could not save the wiring cache. 0.3.3 is 0.3.2 with that fixed. The wiring cache is now written and read without reflection. Everything listed under 0.3.2 below ships in 0.3.3.

## 0.3.2

**Behaviour changes:**

- **Evidence binds through Unity's project files.** When `Assembly-CSharp.csproj` exists, the code index adds the engine and package assemblies it references, so members inherited from engine and package base classes bind to them and never to a project type with the same name. A type name where C# needs a value is no longer counted. Totals can change in both directions: false references drop out, and true references that needed engine types to bind are now counted. `code_evidence` and `DependencyEvidence` stay equal. `code_evidence` reports `binding` (`unity_references`, `unseen_base_types`, a `hint` when the project files are missing).
- **The Reject cycle rule applies between layers.** A dependency cycle inside one layer is allowed and reported as a `dependency_cycle` warning ("inside one layer"). Switching to Reject is refused only while a cycle across layers remains.
- **`architector_route` answers by scope owner.** A file belongs to the layer whose code scope owns it, by the rule of the code facts. A node linked to the type in another layer is named next to it, and conformance reports it as the new warning `linked_node_outside_scope`.
- **Isolated linked nodes.** The error now says what relationship is expected. **`set_standalone`** (or the Node Inspector's **Standalone** field) marks a linked node that has no relationship on purpose, with a reason. The error then becomes a `standalone_node` warning and no longer blocks approval. The mark is kept in `architector.settings.json`, so no layer file or approval changes.

**Code facts:**

- **Wiring facts.** `layer_facts` reads prefabs, scenes and assets as YAML, without Unity. For each script it reports:
  - where it is used, by its `.meta` GUID;
  - its `m_Enabled` state;
  - which lists of other components point to it (`listed_by`);
  - its `[SerializeReference]` uses.

  Each type gets a status: `wired`, `unwired` or `test_only`. Test and sample folders are set in `code-facts.json` with `wiring.testFolders`; the default is `**/Tests/**`, `**/Test/**`, `**/Samples/**`, `**/Examples/**`. The layer summary counts the statuses. Every file's result is cached in `Library/Architector/wiring-index.json`.
- **Behaviour facts.** `layer_facts` with `type` lists, each with file:line:
  - `reads`, `writes` and `uses`, from the generic-call rules. `genericCalls` entries may be `{"name", "access": "read"|"write"}`; otherwise the method name decides.
  - `subscribes` and `unsubscribes`: `+=` and `-=` on events and delegates.
  - `assigns`: fields and properties of other types it sets.
  - `lifecycle`: its Unity callbacks, with `loops` and an `unpaired` `OnEnable`/`OnDisable`.

  `section` with `offset`/`limit` pages one of them.
- **Member references for plans.** Every member in `layer_facts` with `type` carries `member_ref` (`{kind, name, parameters}`). Plans accept it wherever they take a member, so a plan can be written without asking the Editor for symbol keys.
- **Layer paths.** Every action, in the server, the command line and the Editor gateway, takes a layer id, its full path or a unique trailing part of it (`Group/Layer`). Errors have one shape: `layer_not_found` or `layer_ambiguous`, with `target` and `candidates`.
- `include: "references"` rows end with the source and target layer, and `include: "intra"` rows with the layer.
- `status: "unevidenced"` lists only the unevidenced pairs, also without `layer`.
- Rule suggestions name the `folders` their uses come from. `code-facts.json` `suggestions.exclude` (globs) leaves vendor folders out.

**Plans and work sessions:**

- `deletions` accept node names (`Layer::Node`, no separate `layer` needed), and relationships by their nodes (`Layer::Source -> Layer::Target`) with an optional `relationship` kind and `label`.
- `dry_run_plan` returns `permissions_lost`: layer pairs the plan leaves without any dependency. Through the server and the command line, each pair also has `code_references`, the references in code that would become undeclared.
- The graph revision is also kept in `Library/Architector/revision.txt`, so it keeps counting after an Editor restart and a package update, not only after a script reload.

**Command line and skill:**

- Options may come before the command (`architector-mcp --project <path> status`). A mistyped command answers `unknown_command`; before, the binary started the MCP server and printed nothing. Every failure prints JSON.
- `skill install` and `skill sync` install `Workflow.md` next to the skill and point its links there.
- `help edit`, the Workflow and the skill now cover:
  - `approve_layers --approve_changes true`;
  - `implements` and `extends` as labels on a dependency;
  - `permissions_lost`, `set_standalone`, `member_ref`, wiring and behaviour facts.

## 0.3.1

**Windows open at once.** No Architector window hangs white while it loads any more:

- The Architector window and workspace windows draw their frame at once and load in steps after it is shown: the architecture, project types, approvals, every layer's checks and the open layer. A loading bar glides between steps and counts layers ("Checking layers 4 / 12"). Input waits until loading ends.
- The Layer Inspector never checks a layer while it draws:
  - A check expected to be very short runs at once.
  - A longer check keeps the last shown result on screen until the new one is ready, so switching layers does not flicker.
  - "Checking conformance…" appears only for a wait expected to take half a second or more.
  - A layer whose check fails shows "Conformance unavailable" and is not checked again until the architecture or the code changes.
- After an edit, the Explorer's error and change marks are rebuilt in the background, one layer per step. The old marks stay until the new set is complete, so an edit no longer waits for a check of the whole project.
- **Review** checks layers one by one with a counting loading bar when it opens. Later checks that took under half a second run quietly.
- **Integrations** runs its Setup Check after the window is shown, with a loading bar only for the first check.

**Agent work locks Integrations and Review too.** While an agent works, Integrations shares the Settings lock and Review shares the workspace lock: the same banner, greyed content and no input. The Settings **Update** foldout now greys out with the rest.

**Window layout:**

- The Explorer starts below the tab bar. Beside the tab bar, a strip of square quick buttons holds **New Workspace Window** (moved from the tab bar) and **Review**, with an eye icon and an outlined count of layers waiting for review.
- Integrations colours its tags: **Extension** and **MCP** each have their own colour on the filter buttons and in the entry headers.
- The confirmation for clearing tabs has **Do not show again**. **Settings > Utilities > Confirm Clearing Tabs** turns it back on.
- A click on an empty spot of the canvas no longer refits the view twice. Fit Layer On Open follows a canvas size change only until you first click, scroll or press a key in the canvas.

## 0.3.0

**Code facts for agents.** Building a graph over an existing large project, agents spent most of a session reading code for facts a program can compute. The MCP server now answers them itself, without Unity, from one Roslyn pass over `Assets` that it keeps in memory and refreshes after files change:

- `code_evidence`: every reference between layers with source type, target type and file:line, grouped by layer pair with declared, undeclared, cycle and unevidenced status; owners of every type; same-layer references. The totals equal `DependencyEvidence`, which now runs on the same engine with unchanged output.
- `layer_facts`: a layer's files and types (kind, base type, interfaces, public members), its references to and from other layers with type pairs, and node candidates by rule (used or inherited by other layers, Unity entry points, extension points), each with file:line and the node it is linked to. No responsibilities are generated.
- `scope_what_if`: for types, files or folders moved to other layers, the undeclared pairs and cycles added and removed, the relationships that would lose their evidence and the linked nodes that would change layer. Changes nothing.
- `layer_dump`: a layer's nodes and relationships with ids, without layout, paged. `lookup`: layer, node and member ids by name.
- Rules for how types are wired, reported per type by `layer_facts` (counts in the type list, full lists with file:line for one `type`): generic calls (`Get<Container, Parameter>`), injected types (`[Inject]` parameters, fields, properties) and Inspector-wired fields (`[SerializeField]`). The **unity** preset is on by default (`GetComponent` family, `Find…ByType`, `CreateInstance`, `Resources.Load`, `[SerializeField]`, `[SerializeReference]`); **zenject**, **vcontainer** and **addressables** are ready to switch on. `Architector/code-facts.json` chooses `presets` and adds `genericCalls`, `injectionAttributes` and `serializedAttributes`. Rules add facts only; they never change the dependency evidence.
- `code_evidence` reports the active rules and **rule suggestions**: generic calls with the project's own types and attributes on fields and parameters that the code repeats at least ten times but no rule covers, each with an example. Agents check a few examples and propose them to the user.
- Replies are compact (counts and the most frequent type pairs); full lists come by `offset`/`limit` paging or `output: "file"`. The same answers are available from the command line: `dotnet run --project Tools~/DependencyEvidence -- <root> --facts=<action> --args=<json>`.
- The server binary grows from about 12 to about 19 MB (Roslyn and .NET reference assemblies inside); it still needs no .NET SDK.

**Fast after the first analysis.** The analysed code (types, owners' inputs, references, members, rule facts) is saved under `Library/Architector` and reused until a C# file, the csproj or `code-facts.json` changes. Editing the graph never repeats the analysis: layer scopes, owners and evidence are applied on top in a fraction of a second. On a project of about 1,500 files the first analysis takes about 10 to 15 seconds; a restarted server answers its first call in under a second, and `DependencyEvidence` runs in 0.4 s instead of 10 s.

**A command line in the same binary.** `architector-mcp` started with a command is the `architector` command line, so agents that can run shell commands need no tool schemas in their context:

- `facts evidence|layer|what-if|dump|lookup` (the code facts, identical JSON to the MCP actions), `query <action>` and `edit <action> [--plan plan.json]` (any gateway action through the open Editor), `status`, `doctor` (project, data, code index, Editor, package and agent skills, each with what to fix), `commands` (every command as JSON) and `help [command]`.
- Every reply is JSON with `success`; `--pretty` indents it. Exit codes: 0 success, 1 internal error, 2 usage, 4 setup missing, 6 `success=false`, 7 Unity Editor not reachable (retry when it is open).
- `skill install <claude|codex|all>` copies the packaged `architector-authoring` skill into the project (`.claude/skills`, `.agents/skills`) with a version stamp; `skill sync` keeps the copies it installed at the package version. A copy it did not install is never replaced without `--force`.
- `architector_status` reports the binary's path as `cli`, so an agent connected over MCP learns it can use the command line.

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
- The Settings button and the buttons beside it in the header have the same 3 px margin on top, bottom and right, and **F** with the pointer over the layer tabs scrolls to the tab of the open layer. The Hotkeys popup is as wide as its longest description.
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
