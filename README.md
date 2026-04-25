# AxiomCode.TwinCAT.CodeAnalyser

TwinCAT 3 PLC project parser and analyser. Built as a **dual-purpose** repo:

| Surface | Role | Built as |
| --- | --- | --- |
| `AxiomCode.TwinCAT.CodeAnalyser` | **Library** — embeddable in any .NET app (TwinStack, internal tools, future commissioning consoles). Publishes to GitHub Packages. | `Library`, `IsPackable=true`, multi-targets `net8.0;net10.0` |
| `AxiomCode.TwinCAT.CodeAnalyser.McpServer` | **MCP Server** — thin Exe shim hosting the [`ModelContextProtocol`](https://github.com/modelcontextprotocol/csharp-sdk) stdio loop and `[McpServerTool]` tool surface. Registered in Claude Code via `.mcp.json` by absolute path. | `Exe`, `net8.0`, not packaged |

The Library is the source of truth for parsing, compliance, PackML, alarm
extraction and IO mapping. The MCP Server is one consumer of that Library;
TwinStack is another. Internal applications get the same data shape from
both surfaces.

## Layout

```
AxiomCode.TwinCAT.CodeAnalyser/
├── .github/workflows/
│   ├── ci.yml                              # PR / main: restore → build
│   └── release.yml                         # on vMAJOR.MINOR.PATCH tag: pack Library + push to GitHub Packages
├── src/
│   ├── AxiomCode.TwinCAT.CodeAnalyser/                  # Library
│   │   ├── Models/                                      # public types: TcProject, TcPou, …
│   │   ├── Services/                                    # AnalyzerService, ComplianceChecker, …
│   │   ├── Templates/                                   # embedded HTML viewer assets
│   │   └── AxiomCode.TwinCAT.CodeAnalyser.csproj
│   └── AxiomCode.TwinCAT.CodeAnalyser.McpServer/        # MCP server Exe shim
│       ├── Tools/                                        # [McpServerTool] wrappers
│       ├── Program.cs                                   # stdio host bootstrap
│       ├── appsettings.json
│       └── AxiomCode.TwinCAT.CodeAnalyser.McpServer.csproj
├── AxiomCode.TwinCAT.CodeAnalyser.sln
├── Directory.Build.props                   # repo-wide compile / metadata defaults
├── global.json                             # SDK 10.0.x latestMinor
├── nuget.config                            # axiomatrix package source (private GitHub Packages)
├── CHANGELOG.md
├── LICENSE
└── README.md
```

## Library — what it provides

Public namespaces (consumed today by [TwinStack.AIPlatform](https://github.com/axiomatrix/AxiomCode.TwinStack.AIPlatform)):

| Namespace | Contents |
| --- | --- |
| `AxiomCode.TwinCAT.CodeAnalyser.Models` | `TcProject`, `TcPou`, `TcDut`, `TcGvl`, `TcMethod`, `TcVariable`, `StateMachine`, `AlarmInfo`, `IsaLayer`, `VarScope`, `PackMlComplianceResult`, `StandardCompliance`, `ModuleCompliance`, `ProjectCompliance`, `ComplianceLevel` |
| `AxiomCode.TwinCAT.CodeAnalyser.Services` | `AnalyzerService` (pipeline entry point), `ComplianceChecker` (10-standard compliance), `PackMlAnalyzer`, `AlarmDescriptionEnricher`, `HtmlGenerator` |

Minimal usage:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using AxiomCode.TwinCAT.CodeAnalyser.Services;

var analyzer = new AnalyzerService(NullLogger<AnalyzerService>.Instance);
var project  = analyzer.AnalyzeProject(@"D:\path\to\project.plcproj");

Console.WriteLine($"{project.Summary.PouCount} POUs, {project.Summary.TotalAlarms} alarms");
```

## MCP Server — what it provides

Tool catalogue surfaced over stdio to Claude Code / Claude Desktop:

| Tool | Description |
| --- | --- |
| `twincat_analyze` | Full project analysis — returns summary JSON |
| `twincat_generate_html` | Generate interactive HTML viewer |
| `twincat_alarm_list` | Extract flat alarm list |
| `twincat_state_machines` | State machines with states + transitions |
| `twincat_module_info` | Detailed info about a specific POU |
| `twincat_io_map` | All IO mappings |
| `twincat_libraries` | PLC library dependencies |
| `twincat_safety` | TwinSAFE project artefacts |
| `twincat_drives` | Drive Manager artefacts |
| `twincat_scopes` | Scope View artefacts |

## Building

Standard .NET CLI:

```powershell
dotnet restore
dotnet build -c Release
```

Both projects build by default. The McpServer Exe lands at:

```
src/AxiomCode.TwinCAT.CodeAnalyser.McpServer/bin/Release/net8.0/AxiomCode.TwinCAT.CodeAnalyser.McpServer.exe
```

The repo-wide `D:\AXIOM-DATA\GITHUB\.mcp.json` already references that
absolute path, so `claude` picks it up after every build.

## Releasing

Tag a commit on `main` with `vMAJOR.MINOR.PATCH` and push the tag. The
`release.yml` workflow packs the Library project (only) and publishes it
to the private `axiomatrix` GitHub Packages feed.

```powershell
git tag v1.0.0
git push origin v1.0.0
```

The McpServer Exe is **not** published — it stays a local-build artefact
referenced from `.mcp.json` by absolute path on each developer machine.

## Consuming the Library

Add a `nuget.config` to the consuming repo:

```xml
<configuration>
  <packageSources>
    <add key="axiomatrix" value="https://nuget.pkg.github.com/axiomatrix/index.json" />
  </packageSources>
</configuration>
```

Authenticate once per machine with a GitHub Personal Access Token that has
`read:packages` scope on the `axiomatrix` account:

```powershell
dotnet nuget add source `
    --username axiomatrix `
    --password <PAT-with-read:packages> `
    --store-password-in-clear-text `
    --name axiomatrix `
    "https://nuget.pkg.github.com/axiomatrix/index.json"
```

Then in the consuming `csproj`:

```xml
<ItemGroup>
  <PackageReference Include="AxiomCode.TwinCAT.CodeAnalyser" Version="1.0.0" />
</ItemGroup>
```

## License

Axiotech Automation Ltd — proprietary, internal use only. See `LICENSE`.
