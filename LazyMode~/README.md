# Architector Lazy Mode

A workflow mode for [Architector](https://github.com/oleksiydemidenko/architector). The agent writes and approves layers on its own. You do not need to open Architector or follow its work. In development.

You do not need to understand the architecture in this mode. Architector is less effective without your control.

## What It Does

- Makes **Lazy** in **Agent Workflow** usable. The mode is stored per user and per project, and the agent cannot change it.
- The agent can approve, revoke and revert layers without asking you.
- Layers approved this way are marked **By Agent**, and **Confirm** records that you reviewed one.
- While the mode is on, a grey button with a yellow triangle stays in the Architector window header. It opens the mode setting.
- Locked groups stay locked.

## Install

Needs Architector 0.2.1 or later. Open **Window > Architector > Integrations** and press **Install Package** under **Lazy Mode**, then **Turn On**. Selecting **Lazy** in **Agent Workflow** in the settings opens the same window.

You can also add this package in **Window > Package Manager** from a Git URL:

```
https://github.com/oleksiydemidenko/architector.git?path=/LazyMode~#v0.2.1
```

## Remove

Press **Remove Package** in **Window > Architector > Integrations**. Architector returns to Safe, and layers keep their **By Agent** mark.
