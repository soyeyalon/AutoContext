---
name: "copilot (v1.0.0)"
description: "Instructions that govern how Copilot should behave: instruction precedence, safety, prompt etiquette, and operational boundaries."
---
# Copilot Instructions

## Instruction Precedence
When two instructions disagree, the higher-level one wins. Within a single response, if two correct-looking choices conflict, prefer the higher concern:

1. Security / legality / logical correctness (no bugs, no undefined behavior)
2. Quality (reliability, maintainability, robust error handling)
3. Performance / memory
4. Style / formatting

For tier-1 violations, stop generating and surface a warning. For lower-tier conflicts, surface a warning inline but continue.

If unsure which instruction applies, generate a concise plan explaining the dilemma and stop; await user approval before continuing.

## AutoContext

This file and the AutoContext instruction files loaded with it are provided by the AutoContext extension. Together, they define the active AutoContext guidance for the current task and take precedence over generic assistant behavior.

- **`copilot.instructions.md`** — Defines host-level operational constraints. If workspace guidance conflicts with this file, this file wins.
- **`autocontext.instructions.md`** — Defines AutoContext usage: how to discover applicable instructions, apply the `## Applying the Rules` loop, and satisfy MCP-tool obligations.
- **`.editorconfig`** — Defines style configuration. Explicit `.editorconfig` style rules override instruction-file style defaults.

Before generating, editing, or reviewing files, discover which AutoContext instructions apply by calling the appropriate tool:

| Trigger                                        | Example                                                            | Returns                                                                                          | Call                                                |
|------------------------------------------------|--------------------------------------------------------------------|--------------------------------------------------------------------------------------------------|-----------------------------------------------------|
| Find rules for a file before touching it       | `{ applyTo: "src/Foo.cs" }`                                        | Listing rows: `name`, `label`, `description`, `version`, `applyTo`, `hasChangelog`, categories   | `list_autocontext_instructions_files`               |
| Search AutoContext guidance by topic           | `{ query: "ConfigureAwait" }`                                      | Ranked hits with `excerpts[]`, including `section`, `sectionLevel`, and `anchor`                 | `search_autocontext_instructions_files_by_content`  |
| Find rules by metadata                         | `{ predicate: { "sections.heading": "Security" } }`                | Listing rows, plus `matchedAnchors` when querying `sections.*` metadata                        | `search_autocontext_instructions_files_by_metadata` |
| Read a known rule file, or selected sections   | `{ name: "lang-csharp.instructions.md", sections: ["security"] }`   | Normalized markdown body, or only the requested sections in document order                       | `get_autocontext_instructions_file`                 |

## Prompt Instructions
- **Do** read the `README.md` and other documentation files to understand the project structure and requirements.
- **Do** follow the `## MCP Tool Validation` section of every loaded instructions file — call the named MCP tool and treat any reported violation as blocking.
- **Do** consult the `## Workflow Instruction Triggers` and `## Workflow MCP Tools Triggers` tables below for triggers that don't fire from a file glob.
- **Do** use cross-platform PowerShell commands and scripts (e.g., `pwsh -Command 'dotnet test; ./scripts/coverage.ps1'`).
- **Do** respect existing config files (`.editorconfig`, `.gitignore`, `.csproj`, `.fsproj`, `GlobalSuppressions`, etc.); only change them when necessary, with rationale.
- **Do** act immediately; pause for approval only on multi-file, major-refactor, or multi-phase work.
- **Do** clean up after yourself – delete any temp or redundant files you create.
- **Do** fix one category of errors completely before moving to the next.
- **Don't** run any git command that changes repository state (`git add`, `git rm`, `git commit`, `git push`, `git reset`, `git checkout`, `git merge`, `git rebase`, etc.) without explicit user permission — read-only commands (`git status`, `git diff`, `git log`, `git show`) are fine.
- **Don't** omit `--gpg-sign` (`-S`) from `git commit` when the repo or global config has `commit.gpgSign = true`; always honour the user's signing settings.
- **Don't** create markdown report files unless explicitly requested by the user.
- **Don't** create a production folder named `Support` or `support` — those names are reserved for .NET test-support folders (see [dotnet-testing#INST0011]; root principle in [testing#INST0019]).
- **Don't** retrofit newly-added instruction rules onto pre-existing code that doesn't already trip them — apply new rules only to code you're touching for another reason. A rule landing in `*.instructions.md` is forward-looking; treat legacy violations as known debt, not an action item. Don't open speculative cleanup edits, and don't insert ignore comments either. If a rule is important enough to enforce retroactively, the user will say so explicitly.

## Workflow Instruction Triggers
When the user asks you to do one of these tasks, read the listed instruction file and follow it.

| Trigger                                              | Load                                  |
|------------------------------------------------------|---------------------------------------|
| Reviewing a diff, PR, or auditing a change           | `code-review.instructions.md`         |
| Drafting a git commit message                        | `git-commit.instructions.md`          |
| Writing, reviewing, or planning tests                | `testing.instructions.md`             |
| Designing or refactoring system structure / DI / SoC | `design-principles.instructions.md`   |
| Designing or reviewing a REST API                    | `rest-api-design.instructions.md`     |

## Workflow MCP Tools Triggers
When you do one of these tasks, call the listed MCP tool and fix anything it reports before continuing.

| Trigger                                                                              | Call                            |
|--------------------------------------------------------------------------------------|---------------------------------|
| Drafting a git commit message                                                        | `analyze_git_commit_message`    |
| Generating, reformatting, or reviewing code (resolve indent, charset, EOL, etc.)     | `read_editorconfig_rules`       |
