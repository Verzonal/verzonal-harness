# DeepSeek Harness for Windows

English | [中文](README.zh.md)

A native C# port of the harness runtime with a WinUI 3 desktop front-end. The TypeScript tree under `packages/` is the reference implementation and is untouched by anything here; `pnpm-workspace.yaml` does not reach this directory, so no TypeScript gate, bundler, or workspace command sees it.

The rationale, the alternatives it beat, and what it deliberately gives up are in [the port's Agent Note](../.agents/notes/implemented/architecture/2026-09-03-winui3-windows-desktop-port.md).

## Building and running

Needs the .NET 8 SDK. On any platform:

```sh
dotnet build Dsh.Portable.slnf     # the runtime, the capabilities, and the view-models
dotnet test Dsh.Portable.slnf      # every unit test
dotnet run --project src/Dsh.Cli -- --workspace . "list the markdown files"
```

`Dsh.Portable.slnf` is every project except the WinUI shell. It exists because the Windows App SDK builds only on Windows, and without the filter a Linux or macOS build of `Dsh.sln` would fail on a project that has nothing to do with the runtime.

On Windows, additionally:

```sh
dotnet build src/Dsh.App/Dsh.App.csproj -p:Platform=x64
dotnet run --project src/Dsh.App -p:Platform=x64
```

The app is unpackaged — a plain `.exe`, no MSIX, no store identity. `-p:Platform=x64` is required rather than optional: a WinUI 3 project declares concrete architectures and no `AnyCPU`.

`Dsh.Cli --fake` runs a scripted model instead of a real one, which is how the assembled harness is exercised without a key. `--dump-composition` prints the mounted plugin rows and the registered tools.

### Credentials

`DEEPSEEK_API_KEY`, optionally `DEEPSEEK_BASE_URL`. Resolution order is process environment, then `.credentials.yaml` in the harness home, then the workspace's `.env`, then the user's. The desktop app's settings page writes the harness-home file. Nothing reaches a session log.

## Layout

| Project | What it is |
|---|---|
| `Dsh.Cordis` | The plugin framework: contexts, services, `inject` gating, fibers, reversible effects, four dispatch modes |
| `Dsh.Util` | Home paths, atomic writes, line diff, ANSI |
| `Dsh.Llm` | Model vocabulary: messages, content blocks, stream chunks, the adapter seam |
| `Dsh.Llm.DeepSeek` | The DeepSeek provider: HTTP, spec-strict SSE, translation, serialization |
| `Dsh.Llm.Fake` | A scripted provider, for tests and for running with no key |
| `Dsh.Session` | The append-only event log, the surface, `DeriveMessages`, the store |
| `Dsh.Session.Persistence` | JSONL under the harness home |
| `Dsh.SystemPrompt` | Prompt sections and assembly |
| `Dsh.Tools` | The registry, JSON-schema validation, the guarded pipeline, render intents |
| `Dsh.Agent` | The agent interface, the inbox, the `agent/*` events, the registry |
| `Dsh.AgentLoop` | The turn/step driver and the tool-call scheduler |
| `Dsh.Fs`, `Dsh.Shell` | Capability seams and their local providers |
| `Dsh.Tools.Fs`, `.Shell`, `.Todo` | The model-facing tools |
| `Dsh.Interaction` | Approval, permission presets, the sandbox policy |
| `Dsh.Settings`, `Dsh.Credentials` | `settings.yaml` and credential resolution |
| `Dsh.Bundle.Base` | The composition — what a `cordis.yml` is in the TypeScript tree |
| `Dsh.App.Core` | Every view-model, the conversation projection, the markdown parser |
| `Dsh.App` | The WinUI 3 shell: XAML views only |
| `Dsh.Cli` | A console front-end over the same composition |

## Why the split at `Dsh.App`

Everything except `Dsh.App` targets `net8.0`. That is not an accident of packaging — it is what makes the port testable: all behavior, including the desktop application's own, is reachable from `dotnet test` on any machine, and only view markup is Windows-only.

So the rule for `Dsh.App` is that it holds no decisions. What a key press means, which rows a log projects into, when the composer is enabled, how an approval is answered — all of it lives in `Dsh.App.Core` under test. Two surfaces are C# presenters rather than markup because both draw unions whose arms need different elements: markdown blocks, and tool result cards chosen by the render intent the tool itself declares.

## What the app shows

The conversation is projected from the session log and nothing else. Live streaming and replaying a stored session go down the same path, which is why reopening a session reproduces the screen exactly rather than approximately.

Approval is a composer takeover rather than a dialog: it appears where a person is already looking and where they would otherwise be typing. It fails closed at every edge — no answerer, a closed window, or a withdrawn question all refuse. `allowed-once` is the only grant there is; nothing is remembered as permitted.

The settings page lists the live plugin rows, which is the desktop equivalent of dumping the composition and the thing that makes "everything is a plugin" visible rather than merely asserted.

## Not ported

Of the harness: subagents, workflows, compaction, the job tools, skills, and web search. The permission and sandbox layer is present with its presets and its fail-closed approval, but Windows ACL confinement is not implemented — the policy refuses writes outside the workspace rather than confining the process, so a tool that escaped the policy would not be contained by the operating system.

Of the app: the slash-command palette, the `@` reference picker, ask-user questions, plan review, and the goal bar. Approval is the only takeover implemented. Tool cards, the checklist and queue docks, the permission chip, and the context meter are all present.

On-disk *layout* matches the Node harness. Byte-level event equality with the TypeScript writer is unverified, and enum members serialize in camelCase where the reference uses snake_case.
