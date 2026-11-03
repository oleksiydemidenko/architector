# Workflow

Architector separates what is agreed from what is being tried. This page describes the way of working the tool is built for. Change it to suit your team.

## Structure

- Keep two root groups: `Core` for the agreed architecture and `Workspace` for designs, experiments and test work.
- Model the important contracts. Nobody has to model the whole project.
- The graph holds structure and contracts. Code style, prohibitions and rationale stay in your documents.
- Name groups and layers by responsibility, without `V2`, `New` or `Final`.

## Reading

1. Start from `context_route` for the file or type in hand.
2. For an approved layer, read only the printed digest lines. For an unapproved layer, the tool prints its design.
3. Use `layer_graph` only when editing a layer. It returns much more than a digest section.

Keep results small: summary and search first, then the exact contract you need.

## Changing The Architecture

1. Agree the design first. In `Core`, write only the agreed result. `Workspace` may hold drafts.
2. `begin_work` with the current `expected_revision`. Keep the returned `work_token`.
3. `dry_run_plan`, fix what it reports, then `apply_plan` with the returned `plan_token`.
4. Inspect the result and the approval impact. Do not arrange while working; call `arrange_changed_layers` once before `end_work` to lay out new layers and place added nodes next to their neighbours; ordinary plans omit node coordinates. Preserve existing user layout during incremental edits. See the [Authoring Skill](../Skills~/architector-authoring/SKILL.md) for the tool parameters.
5. `end_work` before handing over, also when stopping with a problem.

Saved designs are Draft, or Changed for edited approved layers. Layout changes keep approval.

## Approval

Approval means a person agreed the design. Use `approve_layers` only when asked. `approve_changes=true` is the agent's statement that the user reviewed those layers. The tool cannot prove it, so review the repository as the real enforcement. The same applies to group locks: they guard the tools, not the files.

## Implementing

Contract-preserving work needs no new approval. If the implementation has to change an approved contract, agree it and record it first.

After implementing:

1. Link declared nodes to real types and selected members.
2. Run conformance and read the remaining diagnostics. `layer_not_approved` is not a contract mismatch.
3. For layers with a code scope, run `DependencyEvidence`. Fix an undeclared dependency in the code, or agree to declare it. Do not widen a scope to silence the report.

Conformance checks compiled structure. It says nothing about lifecycle behaviour, method bodies or whether the feature works.

## Dependency cycles

**Window > Architector > Settings > Rules > Dependency Cycles** is shared with the team in `architector.settings.json`.

- **Reject** (default) refuses any change that closes a cycle of `dependency` relationships and names the cycle, for example `Player/Movement → Camera/Rig → Player/Movement`. Use it when the graph states the intended architecture.
- **Report** saves the cycles, so a map of existing code can keep every real dependency. Each cycle appears as a `dependency_cycle` warning in the conformance of every layer it passes through, in MCP `validate` and in the Relationship Inspector. Cycles do not block approval.

Do not relabel a real dependency as an event or merge unrelated nodes to avoid a cycle. Switching back to Reject is refused while a cycle remains. `event`, `dataFlow` and `callback` relationships may form cycles under both rules.

## Relationship merging

In **Window > Architector > Settings > Optimization > Canvas**, **Relationship Merging** defaults to **Off**. Choose **3 or more** or **2 or more** to collect incoming relationships of the same type on one node side into a shared route tree, and outgoing ones from one node side into a tree that splits towards the targets. When every source of a group has a relationship of the same type to every target of the group, they share one trunk with a merge point and split points. Relationships that are not all connected that way never share a line, so a shared line never suggests a relationship that does not exist. Branches join existing paths where a short new connection can be routed, reducing long separate runs. All enabled modes combine parallel relationships between the same nodes when their type and preferred sides match, including below the tree threshold. The threshold counts logical relationships, including those carried by a combined line. **Maximum** uses the same threshold as **2 or more**. Different relationship types remain separate; a network that cannot be separated from others falls back to individual routes.

Junction dots show actual branch merges. Ordinary crossings keep their bridges and do not get junction dots. Click a branch to select its relationship, or repeatedly click the shared trunk to cycle through its members. The Relationship Inspector lists those members. Node selection highlights the branch and the shared portion that it uses. Arrange uses the same setting. These controls change presentation only and leave approval unchanged.
