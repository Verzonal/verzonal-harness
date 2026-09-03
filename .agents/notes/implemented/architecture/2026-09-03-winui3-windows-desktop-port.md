# Agent Note: WinUI 3 Windows desktop port

Status: implemented

## Problem

The harness ships as a CLI and a browser Web UI served by a local Node server. Neither is a Windows desktop application, and reaching one by wrapping the existing server in a window would deliver the features without the architecture: a shell over a sidecar inherits none of the properties that make this codebase what it is — everything is a plugin, registrations are reversible effects, model-visible ⟺ logged, capability seams are complete. A conversion has to keep those expressible, which means porting the runtime rather than embedding it.

## Decision

`windows/` holds a native C# port of the harness runtime under its own .NET solution, with a WinUI 3 front-end. The TypeScript tree is untouched and remains the reference implementation; `pnpm-workspace.yaml` globs do not reach `windows/`, so no existing gate, bundler, or workspace command sees the new tree.

The project layout mirrors `docs/architecture.md` one project per role — `Dsh.Cordis`, `Dsh.Llm`, `Dsh.Session`, `Dsh.Tools`, `Dsh.SystemPrompt`, `Dsh.Agent`, `Dsh.AgentLoop`, the capability projects, `Dsh.Bundle.Base` for the composition, and two front-ends over it: `Dsh.Cli` and `Dsh.App`.

**Every project targets `net8.0` except `Dsh.App`.** That single line is what makes the port verifiable: the runtime, the capabilities, and all application view-models (`Dsh.App.Core`) build and run their tests on any platform, and only XAML views carry the `net8.0-windows10.0.19041.0` moniker. `Dsh.Portable.slnf` selects the portable set; `.github/workflows/windows-app.yml` runs those tests on `ubuntu-latest` and compiles the full solution on `windows-latest`.

Two front-ends over one composition is the shape the TypeScript tree already has (`dsh-web-app` and `dsh-headless` over `dsh-base`). Here it also carries a verification role: `Dsh.Cli` drives the assembled harness end to end where no Windows machine exists.

### What the view layer is allowed to be

`Dsh.App` holds views and nothing else. Every decision — what a key press means, which rows a log projects into, how a card is chosen — lives in `Dsh.App.Core` behind unit tests. Two surfaces are C# presenters rather than markup, because both draw unions whose arms need different elements: markdown blocks, and tool result cards selected by the render intent the tool itself declares. A tool the app has never seen still draws correctly, and a presenter that throws costs its own card rather than the conversation.

The conversation is projected from the session log and nothing else. Live streaming and replaying a stored session go down the same `Apply` path, which is what makes a resumed session reproduce the screen exactly; a test drives both and compares the rows.

### Deliberate deviations

`System.Threading.Lock` and Zstandard are unavailable on `net8.0`, so the port uses `object` monitors and writes plain `.jsonl` while reading both framings through `ZstdSharp`. `Dsh.App` alone disables warnings-as-errors: the XAML compiler emits partial classes this repository does not own, and holding the build hostage to their warnings across SDK updates buys nothing.

## Alternatives considered

**A WebView2 shell over the running Node server.** The fastest route to a window, and the one that fails the request: the result is the existing Web UI in a frame, with the harness still running as JavaScript in a sidecar process. No architectural invariant would be expressed in the desktop application at all, and the "conversion" would be a packaging change.

**Electron or Tauri.** Same objection, plus a second runtime to ship. Tauri would at least make the host native, but the host would still be a browser shell around the same server.

**.NET MAUI instead of WinUI 3.** MAUI buys cross-platform reach the request did not ask for and costs fidelity on the one platform it did: WinUI 3 is the native Windows presentation layer, and Mica, the custom title bar, and the theme resource set are first-class there.

**A thin C# client over the Node server's JSON-RPC.** Tempting because `packages/sdk` already defines the protocol. It would have produced a native window quickly, but the harness would still not exist in C#, and every capability seam would collapse into one RPC boundary — the opposite of the property being preserved.

**Porting only the view-models and calling TypeScript for the loop.** A halfway version of the above with the same defect at the seam that matters most: the agent loop is the thing whose behavior the invariants describe.

## Consequences

The port owns a second implementation of the runtime. That is the cost, and it is real: a change to the turn/step driver or the session vocabulary now has two homes, and nothing mechanically checks that they agree.

What it buys is that the Windows application *is* the harness rather than a client of it. Confinement is one place the C# side is stronger than the original — `System.Security.AccessControl` and `System.Security.Principal` reach the Win32 ACL and token APIs directly, where the Node implementation goes through a helper.

## Testing

`dotnet test Dsh.Portable.slnf` covers the Cordis lifecycle and all four dispatch modes, the session log's `seq == index` rule, surface placement, replacement shadowing, crash repair and unknown-event refusal, the tool pipeline's monotonic denial and fail-closed approval, the turn/step driver and its cancellation paths, the DeepSeek SSE reader and serializer, and the application layer's projection, composer rules, and approval takeover.

The `windows-latest` job compiles `Dsh.App`. Nothing in this repository launches it: a green build proves the views compile and link, not that the window draws correctly.

## Deferred

On-disk *layout* matches the Node harness — home directory, path shape, header line, format version, settings and credential resolution order — and the C# side round-trips its own logs. Byte-level event equality with the TypeScript writer is unverified, and enum members serialize in camelCase where the reference uses snake_case. Subagents, workflows, compaction, and the job tools are not ported.
