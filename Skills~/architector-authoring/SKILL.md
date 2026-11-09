---
name: architector-authoring
description: Describe implemented or planned systems as Architector graphs, bind them to code, check conformance and dependency evidence, and prepare them for user approval. Use when adding or reworking architecture in a Unity project that uses Architector.
---

# Architector Authoring

The graph describes structure, contracts, ownership and dependency direction. Rationale and code style stay in project documents. This skill adds authoring conventions to the [Workflow](../../Documentation~/Workflow.md).

## Before Authoring

- Read the approved architecture through `context_route`, which prints the digest lines of the layer that owns a file or type. Query only the layers you edit (`project_summary`, `group_contents`, `layer_graph`).
- Check `work_status`. Only one agent work session can exist per Editor. Begin late, end early, and never take over another agent's lease. After a script reload `work_status` shows `awaiting_agent_resume`: call `resume_work` with the same `work_token` and continue.
- **If you can run shell commands, prefer the command line.** The server binary (its path is `cli` in `architector_status`) takes the same actions with the same JSON: `architector-mcp facts layer <layer>`, `facts evidence`, `facts what-if --move Type=Layer`, `facts dump <layer>`, `facts lookup <names>`, `query <action> --<parameter> <value>`, `edit <action> --plan plan.json`, `doctor`. Run `architector-mcp help` once for the list. Exit code 7 means the Editor is not reachable; 6 means the reply has `success=false`. When something does not work, run `doctor` and follow what it says.
- **Ask for code facts before reading code.** The `architector-mcp` server answers these `architector_query` actions itself, without Unity:
  - `layer_facts` with `target`: the layer's files and types, references to and from other layers with type pairs, declared or not, and node candidates with file:line (types used or inherited by other layers, Unity entry points, extension points). Read only the lines it points to. A layer is its id, its full path or a unique trailing part of it (`Group/Layer`).
  - `layer_facts` with `type` returns one type's public members, each with its `member_ref` (`{kind, name, parameters}`) for plans, its **behaviour facts** and its **wiring**. **Before reading a large type, read its behaviour facts** (`reads`, `writes`, `subscribes`, `unsubscribes`, `assigns`, `lifecycle`, `unpaired`, `loops`) and open only the lines they point to; `section` with `offset`/`limit` pages one section.
  - **Before deciding that a type is dead, unused or test-only, read its `wiring`**: the prefabs, scenes and assets that use the script, `disabled_in`, `listed_by` (lists of other components that point to it), `[SerializeReference]` uses and `status` (`wired`, `unwired`, `test_only`). The layer summary counts them. Wiring comes from the assets' YAML; references from code (`AddComponent`, reflection, strings) are in the other facts or not visible at all.
  - `code_evidence`: every layer pair with its references; `status=undeclared|cycle|unevidenced`, `pair`, `layer`; `include=references|owners|intra` pages full lists (reference rows end with the source and target layer). `binding` says how many engine and package assemblies came from Unity's project files; without them names inherited from engine base classes are not counted, and `binding.hint` says how to fix it.
  - `scope_what_if` with `moves` (`type`, `file` or `folder`, and `to`): before changing a scope or moving a type, see the undeclared pairs and cycles it adds or removes and the relationships that would lose their evidence.
  - `layer_dump` (nodes and relationships with ids, paged) and `lookup` (names to layer, node and member ids).
  - Replies are compact; use `offset`, `limit` and `next_offset`, or `output=file` for a full list.
  - **Project rules.** `code_evidence` shows the active rules (`rules.presets`, the `unity` preset by default) and `rules.suggestions`: calls and attributes the code repeats that no rule covers, each with the `folders` its uses come from. When a suggestion appears, read two or three of its examples. If it wires types together (dependency access, injection, Inspector wiring), propose to the user adding it to `Architector/code-facts.json` (`genericCalls`, `injectionAttributes`, `serializedAttributes`, or a preset: `zenject`, `vcontainer`, `addressables`). A `genericCalls` entry may be `{"name": "Get", "access": "read"}` so behaviour facts sort it into reads or writes. Suggestions that come only from vendor folders (`Assets/Plugins/**` and the like) are noise: propose `"suggestions": {"exclude": [...]}` instead. Also say so when you notice wiring the facts cannot see (reflection, strings).
- Read the system's code and its documents. Describe what exists and what is agreed, not what the code accidentally does. Responsibilities are yours to write from the code; the facts only point to it.

## Shape The Graph

- **Group**: a system or domain. **Layer**: one responsibility another developer can understand alone, usually 4 to 10 nodes. Split by ownership, lifecycle or dependency direction, never by node count alone.
- **Purpose** says what the layer is responsible for, in one or two plain sentences. It is part of the approved contract.
- **Nodes** are the types someone must know to use or extend the system. Leave helpers and private implementation out.
- Use **Linked** nodes for implemented types, **Manual** nodes for planned types, **Empty** nodes for roles without a type.
- Select only the members a consumer or extender relies on. Add base types and interfaces only when they matter to the contract.
- Fill **responsibility** on every node whose role is not obvious from its name: one line, what it owns or is for.
- **Relationships**: `dependency` A→B means A needs B; draw the direction the rule enforces. `implements` and `extends` are labels on a `dependency`, not relationship kinds. Use `event`, `dataFlow` and `callback` for runtime communication. When the code has a dependency cycle between layers, the project rule decides: under `reject` record the intended direction and report the cycle; under `report` record the real dependencies and leave the cycle visible. A mutual reference inside one layer (a graph type and its nodes) is allowed under both rules and reported as a warning: record both directions, or model the shared contract as an interface node both depend on. Never relabel a dependency to hide a cycle. Label a relationship when the reason is not obvious.
- **Every linked node takes part in its layer's contract.** A linked node without any relationship blocks approval. Add the relationship the code shows (a `dependency` to or from the node that uses the type or that it uses). When the type really stands alone, for example it is used only through serialized wiring, mark it with `set_standalone` and a reason taken from the evidence (`wiring`, `code_evidence`); never invent a relationship to satisfy the rule.
- Cross-layer relationships create their Portal pair by themselves. Use `portals` in the plan only to set its position.
- Leave node `x`/`y` and Portal positions out of ordinary plans. Architector supplies initial free positions; use Arrange after the graph is complete instead of planning coordinates node by node. Choose layers and relationships by architectural meaning.
- Set a **code scope** for every layer of agreed architecture (`layerScopes`: folders, single files or namespaces). The most specific entry wins.
- **Layers follow contract ownership, not role folders.** A shared vocabulary type belongs to the layer that owns the contract, usually the depended-upon layer.

## Apply Through The Tools

1. `begin_work` with the current revision.
2. Read narrowly: `node`, `relationships`, `dependency_path` or `layer_graph` with `detail=summary`. Find type keys with `type_search` (`Assembly:MetadataName`, generics as ``List`1``).
3. Name members in plans by the `member_ref` that `layer_facts` with `type` lists: `{"kind": "method", "name": "Advance", "parameters": ["Single"]}`. Without `parameters` any overload of the name matches; an ambiguous or missing member fails with the candidates. A `symbolKey` (`kind|Assembly:Type|Name|genericArity|parameters`, from `type_contract`) still works.
4. Write one semantic plan (groups, layers, `layerScopes`, nodes with `responsibility`, edges). Existing nodes may be named instead of identified: `"Node Title"` or `"Layer/Path::Node Title"`, also in `deletions` (`{"kind": "node", "target": "Layer::Node"}`); a relationship is deleted by id or by its nodes, `{"kind": "edge", "target": "Layer::Source -> Layer::Target", "relationship": "dependency"}`. Run `dry_run_plan`, fix the reported errors (`operation` names the plan item), read its `warnings` and `permissions_lost` (layer pairs the plan leaves without any dependency, with the `code_references` that would become undeclared), then `apply_plan` with `plan={"plan_token": "<token from the dry run>"}` instead of resending the plan.
   - `nodeContracts` on a linked node: leave out `members`, `baseType` or `interfaces` to keep them; an explicit empty list clears. `warnings` lists every member and interface the plan would remove.
5. Run `conformance` for the group and `code_evidence` (or `DependencyEvidence` from the repository root; both give the same totals). Fix the graph or report the gap.
6. Do not arrange while you work. Once, right before `end_work`, call `arrange_changed_layers` with the current `expected_revision` and `work_token`. It lays out every layer created in this work session and places nodes added to existing layers next to their neighbours, leaving the user's layout of everything else unchanged; layers in locked groups are reported as skipped. Use `arrange_layer` only when the user asks to rearrange a layer. Arrange changes presentation, not approval.
7. `end_work`, then report the layers, conformance counts, dependency evidence and open questions to the user. Approve only when the user asks, unless `capabilities` reports `agent_may_approve`; `approve_layers` needs `approve_changes=true` for layers changed since their last approval. Otherwise Architector asks the user to confirm in the Editor.
   - `linked_node_outside_scope` means a linked node sits in another layer than the one whose code scope owns its type; `architector_route` and the facts place the type by its scope. Move the node or fix the scopes.

## Designing New Systems

- Start from the digest and the relevant layers. Reuse their extension points.
- Before proposing a new public API or a change to agreed architecture, list the existing extension points you considered.
- Design in `Workspace/<task>` first, as Manual and Empty nodes with relationships into the agreed architecture. Agree it with the user, then implement, link and promote it.
- Report missing or unclear contracts to the user instead of guessing.
