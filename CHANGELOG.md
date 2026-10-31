# Changelog

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
