# Playground

This is the in-browser Fade development environment. It runs the Fade
compiler, LSP, and VM entirely client-side so you can write, debug, and
ship a project without installing anything.

## Layout

The page is organized into dockable panels. Drag a tab to rearrange,
double-click a tab to maximize, or close a panel from the **View** menu.

- **Editor** — Monaco with the `fade` language: syntax highlighting,
  diagnostics, hover docs, go-to-definition, find-references, rename.
- **Workspace** — files in your active project, plus per-project
  `fade.json` manifest. Right-click for create/rename/delete.
- **Problems** — every diagnostic the LSP is currently reporting.
- **Help** — what you're reading now. Tabs at the top jump between this
  Playground guide, the language reference, and the command catalog.
- **AI Chat** — chat-style assistant that can read your files, search
  docs, and propose edits.
- **Game / Console** — runtime output. The Game panel is the canvas for
  `monogame` projects; the Console panel collects `print` output for
  any project.
- **Tests** — runs your `test` blocks via the same VM the game uses.
- **Debugger** — breakpoints, step over/in/out, evaluate expressions.

## Projects

Every project is a folder with a `fade.json` manifest, one or more
`.fbasic` sources, and an optional `commandDlls` list. Two templates
ship today:

- **`web`** — pure FadeBasic with the `FadeBasic.Lib.Web` command set
  (`prompt$`, `wait ms`, etc.). Renders nothing.
- **`monogame`** — FadeBasic plus the `Fade.MonoGame.Lib` commands
  (`sprite`, `texture`, `sync`, …). The Game panel hosts the
  MonoGame canvas.

Switch the project type by editing `fade.json`'s `"type"` field. The
Help tab's command list and the AI's retrieved docs both follow the
active type.

## Files persist locally

Everything you create lives in the browser's OPFS storage. Closing the
tab keeps your work; clearing site data wipes it. Use the **Workspace**
panel's import / export actions to round-trip projects through `.zip`.

## Sharing

The **Share** button uploads the active project to a hosted gist-style
endpoint and copies a link. The recipient opens the link, the
Playground forks the project into their workspace, and they're editing
their own copy.
