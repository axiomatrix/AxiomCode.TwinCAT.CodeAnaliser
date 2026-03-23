# AxiomCode.TwinCAT.CodeAnaliser

MCP (Model Context Protocol) server for analyzing TwinCAT 3 Structured Text codebases. Generates interactive HTML documentation with alarm analysis, state machine diagrams, and IO mapping.

## Features

- **Full object tree** — walks GVL instances recursively, resolves inheritance, maps ISA-88 hierarchy (UM/EM/CM/DM)
- **Alarm analysis** — extracts all DM_TriggeredLatch instances, determines severity from `_Alarms()` methods, identifies unresolved reasons
- **State machine extraction** — parses CASE blocks on DM_StateMachine instances, extracts states, transitions, timeouts, and code bodies
- **IO mapping** — extracts AT bindings from GVLs and FB declarations
- **Interactive HTML viewer** — self-contained file with collapsible tree, search, filtering, state machine SVG diagrams, code drill-down

## MCP Tools

| Tool | Description |
|------|-------------|
| `twincat_analyze` | Full project analysis — returns summary JSON |
| `twincat_generate_html` | Generate interactive HTML viewer |
| `twincat_alarm_list` | Extract flat alarm list as JSON |
| `twincat_state_machines` | Extract state machines with states and transitions |
| `twincat_module_info` | Detailed info about a specific POU/module |
| `twincat_io_map` | Extract all IO mappings |

## Usage

### As MCP Server (Claude Code)

Add to your Claude Code settings or `.mcp.json`:

```json
{
  "mcpServers": {
    "twincat-analyzer": {
      "command": "D:\\path\\to\\AxiomCode.TwinCAT.CodeAnaliser.exe"
    }
  }
}
```

### Command Line (Test Mode)

```
AxiomCode.TwinCAT.CodeAnaliser.exe --test <project_path> [output.html]
```

### Build

```powershell
.\build.ps1              # Debug build
.\build.ps1 -Release     # Release build
.\build.ps1 -Publish     # Self-contained publish
```

## Requirements

- .NET 8.0 SDK
- TwinCAT 3 project with .TcPOU, .TcDUT, .TcGVL source files

## Architecture

Built with the `ModelContextProtocol` NuGet package for MCP server capabilities. Parses TwinCAT XML source files using `System.Xml.Linq` and Structured Text declarations using regex-based parsing.

## Author

Axiotech Automation Ltd — systems@axiotech.co.uk
