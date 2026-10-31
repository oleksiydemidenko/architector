# Adding an Agent Integration

Architector owns the canonical repository, validation, approval workflow, and the transport-neutral `ArchitectorAgentApi` in the `Architector.Agents.Editor` assembly. You may not need an adapter at all: the [Architector MCP server](AgentSetup.md) is available to MCP clients. A Unity MCP integration is only an optional transport adapter. It must remain removable without changing architecture data or breaking the core Editor UI.

## 1. Describe the package

Return one `ArchitectorAgentIntegrationDescriptor` from your probe with a stable lowercase ID, display name, Unity package name, and setup/documentation URL. The registry discovers it, so no core file changes. The stable ID is persisted only for the Enabled/Disabled preference.

## 2. Isolate package references

Put all compile-time references to the external package in a dedicated assembly definition. Use an asmdef Version Define and matching Define Constraint so the adapter assembly is omitted when the package is absent or too old. Core, persistence, and UI assemblies must never reference package APIs.

## 3. Implement the probe

Implement `IArchitectorAgentIntegrationProbe` in the optional adapter assembly:

- `Descriptor` describes the integration; a package listed in the core registry returns that entry.
- `Probe` returns `Connected` when the package and adapter are usable, or `Active` only when its transport is actually running.
- `OpenSetup` opens the package's own setup window or documentation.
- Do not install packages, start servers, change ports, or mutate external configuration during a probe.

The registry already reports `Disabled` for a disabled descriptor and `Unavailable` when the package or compatible adapter is missing.

## 4. Expose Architector tools

`Editor/McpMurzak` is a complete example in three files. Register two tools and forward their parameters to `ArchitectorAgentApi.Query` and `ArchitectorAgentApi.Edit` with your integration ID. Use `QueryJson` and `EditJson` when the transport works with JSON text. `ArchitectorAgentResult` carries `success`, `message`, `code`, `error` and `data`; map it to the transport's response type. The API already enforces expected-revision checks, work-lease tokens, dry-run behavior, approval rules, validation and one atomic Undo transaction per apply, so an adapter holds no Architector logic.

## Draft authoring and review

In Safe mode the workflow is: save the architecture in the Editor, end the work session, let the user inspect it, approve the reviewed layers, then implement their contracts.

- `dry_run` / `dry_run_plan` validate without writing.
- `apply` / `apply_plan` save architecture without granting approval. New layers remain Draft; semantic changes to approved layers become Changed; layout-only changes preserve approval.
- Do not pass `approve_changes` to save or preview operations. Combined save-and-approve is rejected.
- `approve_layers` is a separate operation with an array of layer IDs and the current `expected_revision`. In Safe mode it requires `approve_changes=true` as the agent's explicit confirmation that the user reviewed and approved those layers. This is a protocol assertion, not independent proof of human consent.
- End work with `end_work` before handing the diagram to the user, otherwise the workspaces remain locked. Resume/start a session for later changes as required by the work-token contract.
- Never use approval revocation, direct JSON hash edits, or a temporary Free-mode switch to simulate draft authoring.
- Saving a draft is not authorization to implement it. Safe implementation approval is an agent workflow rule, not a sandbox around arbitrary script-editing tools.
- Free mode retains the existing autonomous approval policy. Graph validation, revision checks, work leases and Undo remain in force in both modes.
- Repository/gateway save APIs do not accept an auto-approval flag. UI approval and MCP `approve_layers` use the separate approval operation.

## 5. Verify optionality

Test all four states where the package permits them: Disabled, Unavailable, Connected, and Active. Compile once with the package installed and once with it absent. Removing the adapter must leave Architector's repository, Settings window, manual editing, and canonical files operational.
