---
name: architector-authoring
description: Describe implemented or planned systems as Architector graphs, bind them to code, check conformance and dependency evidence, and prepare them for user approval. Use when adding or reworking architecture in a Unity project that uses Architector.
---

# Architector Authoring

The graph describes structure, contracts, ownership and dependency direction. Rationale and code style stay in project documents. This skill adds authoring conventions to the [Workflow](../../Documentation~/Workflow.md).

## Before Authoring

- Read the approved architecture through `context_route`, which prints the digest lines of the layer that owns a file or type. Query only the layers you edit (`project_summary`, `group_contents`, `layer_graph`).
- Check `work_status`. Only one agent work session can exist per Editor. Begin late, end early, and never take over another agent's lease.
- Read the system's code and its documents. Describe what exists and what is agreed, not what the code accidentally does.

## Shape The Graph

- **Group**: a system or domain. **Layer**: one responsibility another developer can understand alone, usually 4 to 10 nodes. Split by ownership, lifecycle or dependency direction, never by node count alone.
- **Purpose** says what the layer is responsible for, in one or two plain sentences. It is part of the approved contract.
- **Nodes** are the types someone must know to use or extend the system. Leave helpers and private implementation out.
- Use **Linked** nodes for implemented types, **Manual** nodes for planned types, **Empty** nodes for roles without a type.
- Select only the members a consumer or extender relies on. Add base types and interfaces only when they matter to the contract.
- Fill **responsibility** on every node whose role is not obvious from its name: one line, what it owns or is for.
- **Relationships**: `dependency` A→B means A needs B; draw the direction the rule enforces. Use `event`, `dataFlow` and `callback` for runtime communication. Label a relationship when the reason is not obvious.
- Cross-layer relationships create their Portal pair by themselves. Use `portals` in the plan only to set its position.
- Leave node `x`/`y` and Portal positions out of ordinary plans. Architector supplies initial free positions; use Arrange after the graph is complete instead of planning coordinates node by node. Choose layers and relationships by architectural meaning.
- Set a **code scope** for every layer of agreed architecture (`layerScopes`: folders, single files or namespaces). The most specific entry wins.
- **Layers follow contract ownership, not role folders.** A shared vocabulary type belongs to the layer that owns the contract, usually the depended-upon layer.

## Apply Through The Tools

1. `begin_work` with the current revision.
2. Read narrowly: `node`, `relationships`, `dependency_path` or `layer_graph` with `detail=summary`. Find type keys with `type_search` (`Assembly:MetadataName`, generics as ``List`1``).
3. Member symbol keys follow `kind|Assembly:Type|Name|genericArity|parameters`. Take them from `type_contract`.
4. Write one semantic plan (groups, layers, `layerScopes`, nodes with `responsibility`, edges). Run `dry_run_plan`, fix the reported errors (`operation` names the plan item), then `apply_plan` with `plan={"plan_token": "<token from the dry run>"}` instead of resending the plan.
5. Run `conformance` for the group and `DependencyEvidence` from the repository root. Fix the graph or report the gap.
6. Before `end_work`, call `arrange_layer` once for each new layer after its nodes, members, relationships and Portals are complete. Pass `target`, the current `expected_revision` and `work_token`; use the returned revision for the next call. Optional `centers` selects meaningful center node IDs. Preserve existing user layouts during incremental edits; use optional `nodes` for a selected area when rearrangement is needed, and rearrange an entire existing layer only when that reflow is part of the task. Arrange changes presentation, not approval.
7. `end_work`, then report the layers, conformance counts, dependency evidence and open questions to the user. Approve only when the user asks, unless `capabilities` reports `agent_may_approve`. Otherwise Architector asks the user to confirm in the Editor.

## Designing New Systems

- Start from the digest and the relevant layers. Reuse their extension points.
- Before proposing a new public API or a change to agreed architecture, list the existing extension points you considered.
- Design in `Workspace/<task>` first, as Manual and Empty nodes with relationships into the agreed architecture. Agree it with the user, then implement, link and promote it.
- Report missing or unclear contracts to the user instead of guessing.
