# Changelog

All notable changes to AxiomCode.TwinCAT.CodeAnalyser are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed
- **Repository split into Library + MCP Server projects.**
  The existing csproj is now `src/AxiomCode.TwinCAT.CodeAnalyser/`
  (Library, `IsPackable=true`) and contains parsing, compliance, PackML,
  alarm and IO extraction services consumable as a NuGet package by any
  .NET application.
  A new `src/AxiomCode.TwinCAT.CodeAnalyser.McpServer/` project hosts
  the MCP-server entry point: `Program.cs`, `Tools/`, embedded HTML
  templates and `appsettings.json`.
- Added repo-level `Directory.Build.props`, `global.json`, `nuget.config`,
  `LICENSE`, `.github/workflows/ci.yml`, `.github/workflows/release.yml`.

### Migration notes for consumers
- TwinStack and any other internal consumer that references this repo
  via `<ProjectReference>` should keep doing so for now — the file path
  is unchanged. Once `v1.0.0` is published to GitHub Packages, consumers
  can switch to `<PackageReference>`.
- The MCP-server executable path used by `.mcp.json` has moved from
  `src/AxiomCode.TwinCAT.CodeAnalyser/bin/Release/net8.0/AxiomCode.TwinCAT.CodeAnalyser.exe`
  to
  `src/AxiomCode.TwinCAT.CodeAnalyser.McpServer/bin/Release/net8.0/AxiomCode.TwinCAT.CodeAnalyser.McpServer.exe`.
  The `.mcp.json` file in `D:\AXIOM-DATA\GITHUB\.mcp.json` has been
  updated accordingly.

### Deferred (next branch)
- Adopt `AxiomCode.Mcp.Common`, `AxiomCode.Mcp.Hosting` and
  `AxiomCode.Build.Common` packages from the shared infrastructure
  repo. This is deferred until local GitHub Packages auth is in place.

[Unreleased]: https://github.com/axiomatrix/AxiomCode.TwinCAT.CodeAnaliser/compare/HEAD...HEAD
