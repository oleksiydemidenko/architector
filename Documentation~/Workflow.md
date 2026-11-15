# Workflow

Architector separates what is agreed from what is being tried. This page describes the way of working the tool is built for. Change it to suit your team.

## Structure

- Keep two root groups: `Core` for the agreed architecture and `Workspace` for designs, experiments and test work.
- Model the important contracts. Nobody has to model the whole project.
- The graph holds structure and contracts. Code style, prohibitions and rationale stay in your documents.
- Name groups and layers by responsibility, without `V2`, `New` or `Final`.

## Reading

Search the code for names and text. Architector answers what the C# text does not show, so use it for those questions:

- **Placement and wiring:** where a component is placed, which serialized lists of other components hold it, whether a type is wired, created in code or dead (`layer_facts` with `type`, its `wiring`).
- **State access:** who writes and who reads a state type, where that runs and on which prefab instance (`architector-mcp slice <type>`, about 1,500 tokens with the lines themselves; `route <file|type>` names them only).
- **Rules over assets:** whether an edited prefab, scene or placed script breaks a project rule, such as a component missing from the lists that start it (`architector-mcp check <changed files>`, silent when clean).
- **Ownership and dependencies:** which layer owns a file (`route`), which layer pairs the code evidences (`code_evidence`), what a move would change (`scope_what_if`).
- **Agreed design:** the approved contracts and relationships of a layer you change or review (`digest <layer>`).

1. `context_route` (or `architector-mcp route`) names the layer of a file or type, its digest lines and the documents that mention it. For an unapproved layer it prints the design.
2. `architector-mcp digest` (MCP `architector_digest`) without layers only lists the approved layers by group with their node counts; it is cheap. Ask for content by name: a layer (`digest Core/Input Core/Player`) gives its full section with members and relationships; a group (`digest Core/Gameplay`) gives its layers in a compact form: per node its title, kind, type, the first sentence of its responsibility and the types that write and read its state. `--form compact` without layers gives every layer compactly, which is large on big graphs (about 90,000 characters for 60 layers); `--form full` gives the whole digest.
   "written by" and "read by" are generated from the behaviour facts `writes` and `reads` (the configured read and write calls in `code-facts.json`), capped with a total. Responsibilities are written by people or agents and can drift from the code; for "who writes or reads X" trust the generated lines and `layer_facts` over the description.
3. `layer_facts` gives a layer's files and types, its references to and from other layers, node candidates and how its scripts are wired in prefabs and scenes, each with file:line. For one type it gives its behaviour facts: what it reads, writes, subscribes to, assigns in other types and which Unity callbacks it has. A type that looks dead or test-only in C# is decided by its wiring: only `unwired` marks a dead-code candidate. `base_only` types live through their placed subclasses, `created_in_code` types are created by `AddComponent` or `CreateInstance`, `created_by_reflection` types by a reflection site, and `not_tracked` types cannot be seen in assets at all. Wiring is trusted only after the project lists its sandboxes and old copies in `code-facts.json` (`wiring.testPlaces`). To check an existing graph, start from `facts_audit`: it lists the contradictions between graph and facts with their evidence. After a tool update or a code change, `facts_diff` against a saved `facts_snapshot` shows what to re-check. These code facts (`code_evidence`, `layer_facts`, `scope_what_if`, `layer_dump`, `lookup`) come from the server without Unity.
4. `slice <type|file.cs>` answers "what writes and reads this, where does it run, on which object" in one reply: the prefabs that hold the type, per holder the writers and readers with the prefab they sit in, the text field of the holder that names that prefab (an asset id or address naming the prefab, for example the one loaded on every peer or only on the authority) and their start lists, and for each the lines inside the methods that ticks, callbacks, events or Unity run, with how each runs. For a component it gives placement, how its methods run, lifecycle effects and what it writes.
5. Use `layer_dump` or paged `layer_graph` only when editing a layer. They return much more than a digest section.

Keep results small: summary and search first, then the exact contract you need.

## Changing The Architecture

1. Agree the design first. In `Core`, write only the agreed result. `Workspace` may hold drafts.
2. `begin_work` with the current `expected_revision`. Keep the returned `work_token`. After a script reload the session waits for you: `work_status` shows `awaiting_agent_resume`; call `resume_work` with the same token. The revision keeps counting across the reload.
   Before changing a layer's scope or moving types between layers, run `scope_what_if` to see the dependencies and cycles it adds and the relationships that would lose their evidence.
3. `dry_run_plan`, fix what it reports and read `permissions_lost` (layer pairs left without a dependency while code still references across them), then `apply_plan` with the returned `plan_token`.
4. Inspect the result and the approval impact. Do not arrange while working; call `arrange_changed_layers` once before `end_work` to lay out new layers and place added nodes next to their neighbours; ordinary plans omit node coordinates. Preserve existing user layout during incremental edits. See the [Authoring Skill](../Skills~/architector-authoring/SKILL.md) for the tool parameters.
5. `end_work` before handing over, also when stopping with a problem.

Saved designs are Draft, or Changed for edited approved layers. Layout changes keep approval.

## Approval

Approval means a person agreed the design. Use `approve_layers` only when asked; on the command line it is `edit approve_layers --layers <id> --approve_changes true` for changed layers. `approve_changes=true` is the agent's statement that the user reviewed those layers. A linked node without any relationship blocks approval unless it is marked standalone with a reason (`set_standalone`, or the Standalone field in the Node Inspector); the mark lives in `architector.settings.json` and changes no layer file. The tool cannot prove it, so review the repository as the real enforcement. The same applies to group locks: they guard the tools, not the files.

## Implementing

Contract-preserving work needs no new approval. If the implementation has to change an approved contract, agree it and record it first.

After implementing:

1. Link declared nodes to real types and selected members.
2. Run conformance and read the remaining diagnostics. `layer_not_approved` is not a contract mismatch.
3. For layers with a code scope, run `code_evidence` (or `DependencyEvidence`; the totals are the same). Fix an undeclared dependency in the code, or agree to declare it. Do not widen a scope to silence the report.

Conformance checks compiled structure. It says nothing about lifecycle behaviour, method bodies or whether the feature works.

## Rules Over Assets

Some links live only in serialized data: a handler on a prefab runs only when another component of that prefab lists it. `rules.startedBy` in `Architector/code-facts.json` writes such a link down once (see the README), and `architector-mcp check <changed files>` (or `check --git`) answers it after each edit: one line per component that is missing from a list, nothing when everything is in place. `facts audit` reports the same findings for the whole project. A rule with false findings is worse than none: name the files whose lists code fills in `filledAtRuntime`, and fix a finding in the prefab rather than widening the rule. The optional Claude Code hooks (`skill install claude --hooks`) run `check` after edits and when the agent stops.

## Dependency cycles

**Window > Architector > Settings > Rules > Dependency Cycles** is shared with the team in `architector.settings.json`.

- **Reject** (default) refuses any change that closes a cycle of `dependency` relationships across layers and names the cycle, for example `Player/Movement → Camera/Rig → Player/Movement`. Use it when the graph states the intended architecture. A cycle inside one layer is allowed and reported as a `dependency_cycle` warning: layers are the unit of dependency direction, and types inside one layer may know each other.
- **Report** saves the cycles, so a map of existing code can keep every real dependency. Each cycle appears as a `dependency_cycle` warning in the conformance of every layer it passes through, in MCP `validate` and in the Relationship Inspector. Cycles do not block approval.

Do not relabel a real dependency as an event or merge unrelated nodes to avoid a cycle. Switching back to Reject is refused while a cycle across layers remains. `event`, `dataFlow` and `callback` relationships may form cycles under both rules.

## Relationship merging

In **Window > Architector > Settings > Optimization > Canvas**, **Relationship Merging** defaults to **Off**. Choose **3 or more** or **2 or more** to collect incoming relationships of the same type on one node side into a shared route tree, and outgoing ones from one node side into a tree that splits towards the targets. When every source of a group has a relationship of the same type to every target of the group, they share one trunk with a merge point and split points. Relationships that are not all connected that way never share a line, so a shared line never suggests a relationship that does not exist. Branches join existing paths where a short new connection can be routed, reducing long separate runs. All enabled modes combine parallel relationships between the same nodes when their type and preferred sides match, including below the tree threshold. The threshold counts logical relationships, including those carried by a combined line. **Maximum** uses the same threshold as **2 or more**. Different relationship types remain separate; a network that cannot be separated from others falls back to individual routes.

Junction dots show actual branch merges. Ordinary crossings keep their bridges and do not get junction dots. Click a branch to select its relationship, or repeatedly click the shared trunk to cycle through its members. The Relationship Inspector lists those members. Node selection highlights the branch and the shared portion that it uses. Arrange uses the same setting. These controls change presentation only and leave approval unchanged.
