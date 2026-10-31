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
4. Inspect the result and the approval impact.
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
