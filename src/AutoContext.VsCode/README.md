# AutoContext

AutoContext helps your AI coding agent follow your project's conventions. It
ships curated coding guidelines that shape the agent's answers, and quality
checks the agent can run to verify its own work — all managed from a
dedicated sidebar.

> **Work in Progress** — Guidance and checks are refined iteratively.
> Coverage and behaviour will continue to evolve as we incorporate feedback
> and expand language and framework support.

## What you get

- **Coding guidelines** — 79 curated files covering C#, F#, VB.NET,
  TypeScript, JavaScript, Python, Java, Go, Rust, Ruby, Swift, Kotlin, Dart,
  C, C++, Scala, SQL, PowerShell, Bash, CSS, HTML, and more — plus .NET
  frameworks (ASP.NET Core, Blazor, EF Core, WPF, …), web frameworks (React,
  Angular, Vue, Svelte, Next.js, Node.js, …), and tools (Git, Docker).
- **Quality checks** — Ask your agent to review a file, your project's
  dependencies, or a commit message, and it checks them against those same
  conventions.
- **Only what applies to you** — AutoContext looks at your project and uses
  only the guidance and checks that match the technologies you actually use.
- **Follows your `.editorconfig`** — Where your project already states a
  preference, checks follow it rather than imposing their own.
- **Rule-level control** — Switch off a single rule you disagree with without
  losing the rest of the file.
- **Shareable** — Export guidance into your repository so the whole team gets
  it, with or without this extension installed.

## Requirements

VS Code 1.100 or later with
[GitHub Copilot](https://marketplace.visualstudio.com/items?itemName=GitHub.copilot).

Nothing else to install — AutoContext is fully self-contained.

## Getting started

Install from the
[VS Code Marketplace](https://marketplace.visualstudio.com/) or
[Open VSX](https://open-vsx.org/), or install a `.vsix` manually from the
Extensions view (**Install from VSIX…**).

AutoContext configures itself for your project on first use. The first time it
offers its checks, VS Code asks you to confirm you trust them; accept the
prompt so your agent can use them.

Then open Chat in agent mode and try:

- *"Check this file for code style issues."*
- *"Validate my commit message against Conventional Commits."*
- *"Review this file for async problems."*

## The sidebar

AutoContext adds an activity-bar icon with two panels. Each shows an
**enabled / total** count in its header, and each has a **Show / Hide Not
Detected** filter in the `…` menu.

**Instructions** — the guidance your agent follows, grouped by category. Click
any entry to read it. Use the inline actions to turn it on or off.

**Tools** — the checks your agent can run, grouped by area (.NET, Workspace,
Web) and category. Turn any of them on or off.

Changes take effect immediately. Nothing needs restarting.

## Turning off a single rule

You rarely disagree with a whole file — usually it's one rule.

1. Click an entry in the **Instructions** panel to open it.
2. Each rule has a **Disable Instruction** / **Enable Instruction** action
   above it.
3. Click to toggle. Disabled rules are dimmed, tagged `[DISABLED]`, and
   left out of what your agent sees.
4. **Reset All Instructions** at the top re-enables everything.

## Configuration — `.autocontext.json`

Your choices are saved in a file called `.autocontext.json` in the root of
your workspace. It records which guidance and which checks you've turned
off, and which individual rules you've disabled.

You don't have to edit it by hand — the sidebar writes it for you — but it's
plain, readable JSON:

```jsonc
{
  "instructions": {
    "testing.instructions.md": {
      "disabled": true                  // this guidance is off entirely
    },
    "lang-csharp.instructions.md": {
      "disabledRules": ["INST0012"]     // file is on, this one rule isn't
    }
  },
  "mcpTools": {
    "analyze_nuget_references": {
      "disabled": true                  // this check is off
    }
  }
}
```

Anything not listed is on, so the file stays small — it records your
exceptions, not your entire setup.

**Commit it or ignore it.** Committing it gives your whole team the same
setup. Adding it to `.gitignore` keeps your choices personal. Both are
valid — pick whichever suits your team.

If you edit the file directly, the sidebar updates to match.

## Sharing guidance with your team

Exporting copies guidance into `.github/instructions/` in your repository.
Teammates using VS Code pick those files up automatically, even if they don't
have AutoContext installed.

From the **Instructions** panel header, click the export icon, check what you
want to export, and confirm.

An exported file becomes an **override**: it's yours now, and you can edit it
freely. AutoContext shows it with a distinct icon and steps aside. Use **Show
Original** to compare against the built-in version, or **Delete Override** to
go back to it.

When a built-in file gets updated and your copy falls behind, it's flagged as
**overridden (outdated)** so you can decide what to do. **Show Changelog**
tells you what changed.

## What your agent can check

| Area | Check | What it looks at |
|---|---|---|
| .NET | `analyze_csharp_code_style` | Coding style, member ordering, naming, async patterns, nullability |
| .NET | `analyze_csharp_project_structure` | File-scoped namespaces, one type per file, file/type name match |
| .NET | `analyze_csharp_testing_style` | Test class and method naming, assertion conventions |
| .NET | `analyze_nuget_references` | Duplicate references, floating or wildcard versions |
| Workspace | `analyze_git_commit_message` | Conventional Commits format and message content |
| Workspace | `read_editorconfig_rules` | The effective `.editorconfig` settings for a file |
| Web | `analyze_typescript_code_style` | TypeScript anti-patterns and style conventions |

Your agent can also search the full guidance catalogue on demand — to find
what applies to a particular file, or to answer a topical question. Anything
you've switched off is never returned.

## Commands

Available from the Command Palette, prefixed with **AutoContext:**

| Command | What it does |
|---|---|
| **Auto Configure** | Re-scan the workspace and enable what's relevant |
| **Enable / Disable Instruction** | Turn a single piece of guidance on or off |
| **Toggle Instruction** | Turn one rule on or off while reading a file |
| **Reset Instructions** | Re-enable every rule in the current file |
| **Export Instructions** | Start choosing guidance to export |
| **Confirm / Cancel Export** | Finish or abandon an export |
| **Show Original** | View the built-in version of something you've overridden |
| **Delete Override** | Remove your copy and go back to the built-in version |
| **Show Changelog** | See what changed in a piece of guidance |
| **What's New** | Open the release notes |
| **Show / Hide Not Detected** | Show or hide items that don't apply to your project |
| **Start MCP Worker** | Start a check provider from the sidebar |
| **Show Output** | Open the AutoContext output log |

## If something looks wrong

Open **View → Output** and choose **AutoContext** from the dropdown to see
what's happening. **MCP: List Servers** in the Command Palette confirms the
checks are registered.

## License

Copyright 2026 Eyal Alon. AutoContext is licensed under the
[Apache License 2.0](LICENSE). You can use, modify, and distribute it,
including commercially, at no cost.

Use of the AutoContext name and logo is subject to [TRADEMARKS.md](TRADEMARKS.md).

## Source

[github.com/soyeyalon/AutoContext](https://github.com/soyeyalon/AutoContext)
— see [CONTRIBUTING.md](https://github.com/soyeyalon/AutoContext/blob/main/CONTRIBUTING.md)
for contribution guidelines.
