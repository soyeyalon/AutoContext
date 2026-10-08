# Contributing

Contributions are welcome. Before submitting, please review the following.

## License

AutoContext is licensed under the [Apache License 2.0](LICENSE). By submitting a contribution, you agree that it is licensed under the same terms, as described in section 5 of the license. There is no separate agreement to sign.

## Getting Started

1. Fork the repository and clone your fork.
2. Install prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Node.js](https://nodejs.org/) 18+.
3. Run `pwsh ./build.ps1` to compile and test everything.

## Development Workflow

- Create a feature branch from `main`.
- Keep commits small and focused. Follow [Conventional Commits](https://www.conventionalcommits.org/).
- Run `pwsh ./build.ps1` before pushing — all tests must pass.
- Open a pull request against `main`.

## Accepted Contributions

- Bug fixes with a clear description of the problem
- New instruction files for technologies not yet covered
- Improvements to existing MCP tool checkers
- Documentation fixes

## Large Changes

Please open an issue before submitting large refactors or features that materially change the scope of the project.

## Code Style

The project follows its own coding standards via AutoContext's instruction files. See `src/AutoContext.VsCode/instructions/` for the full set of guidelines.
