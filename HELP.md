# AxiomCode.TwinCAT.CodeAnaliser — Help Guide

## What This Tool Does

This MCP server performs **static analysis** of TwinCAT 3 Structured Text (IEC 61131-3) projects. It parses the XML-wrapped source files (.TcPOU, .TcDUT, .TcGVL), resolves the complete object hierarchy with inheritance, and extracts:

- The full ISA-88 module tree (UM / EM / CM / DM layers)
- Every alarm (`DM_TriggeredLatch`) with severity classification
- Every state machine (`DM_StateMachine`) with states, transitions, and code bodies
- All IO mappings (AT bindings to physical addresses)

It can output results as JSON (via MCP tools) or as a self-contained interactive HTML viewer.

---

## Getting Started

### Prerequisites

- .NET 8.0 SDK or Runtime installed
- A TwinCAT 3 project with Structured Text source files

### Building

```powershell
cd D:\AXIOM-DATA\GITHUB\AxiomCode.TwinCAT.CodeAnaliser
.\build.ps1              # Debug build
.\build.ps1 -Release     # Release build
.\build.ps1 -Publish     # Self-contained executable
```

### Quick Test (Command Line)

```powershell
# Analyse a project and print summary
AxiomCode.TwinCAT.CodeAnaliser.exe --test "D:\path\to\ABF PLC"

# Analyse and generate HTML viewer
AxiomCode.TwinCAT.CodeAnaliser.exe --test "D:\path\to\ABF PLC" "D:\output\analysis.html"
```

### Running as MCP Server

The server runs on **stdio** transport — it reads JSON-RPC from stdin and writes to stdout. This is handled automatically by Claude Code or Claude Desktop.

**Claude Desktop** (`claude_desktop_config.json`):
```json
{
  "mcpServers": {
    "AxiomCode.TwinCAT.CodeAnaliser": {
      "command": "D:\\path\\to\\AxiomCode.TwinCAT.CodeAnaliser.exe",
      "args": []
    }
  }
}
```

**Claude Code** (`.mcp.json` in workspace root):
```json
{
  "mcpServers": {
    "AxiomCode.TwinCAT.CodeAnaliser": {
      "command": "D:\\path\\to\\AxiomCode.TwinCAT.CodeAnaliser.exe",
      "args": []
    }
  }
}
```

---

## MCP Tools Reference

### 1. `twincat_analyze`

**Full project analysis.** Returns a JSON summary of the entire project.

| Parameter | Required | Description |
|-----------|----------|-------------|
| `project_path` | Yes | Path to the TwinCAT PLC project directory containing .TcPOU, .TcDUT, .TcGVL files |

**Returns:** JSON with project name, summary statistics (POU/DUT/GVL counts, alarm totals by severity, state machine count, IO point count), list of unresolved types, and root object overview.

**Example prompt:** *"Analyse the ABF PLC project at D:\path\to\ABF PLC"*

---

### 2. `twincat_generate_html`

**Generate an interactive HTML viewer.** Creates a self-contained .html file you can open in any browser.

| Parameter | Required | Description |
|-----------|----------|-------------|
| `project_path` | Yes | Path to the TwinCAT PLC project directory |
| `output_path` | Yes | Where to save the HTML file (e.g. `C:\output\analysis.html`) |

**Returns:** Confirmation message with output path and summary counts.

**Example prompt:** *"Generate an HTML analysis of the ABF PLC project and save it to my desktop"*

**The HTML viewer includes:**
- Collapsible object hierarchy tree with ISA-88 layer colour coding
- Alarm nodes with severity colour bars and unresolved reason tags
- Search and filter by name, type, severity, unresolved reason
- "Hide non-matching" toggle to collapse filtered results
- Module detail panel with Variables, Methods, Alarms, State Machine, and IO tabs
- State machine SVG diagrams with clickable states showing code bodies
- Documentation sidebar explaining the alarm system and architecture
- Stats bar with totals

---

### 3. `twincat_alarm_list`

**Extract all alarms as a flat JSON list.** Useful for generating alarm spreadsheets or comparing against existing alarm lists.

| Parameter | Required | Description |
|-----------|----------|-------------|
| `project_path` | Yes | Path to the TwinCAT PLC project directory |

**Returns:** JSON array where each alarm includes:
- `instanceName` — e.g. "ALM_CommandAborted"
- `severity` — Critical, Process, Advisory, Information, or Unresolved
- `unresolvedReason` — None, BaseClass, NoMethod, Missing, or Dead
- `unresolvedReasonText` — human-readable explanation
- `modulePath` — full S0_Objects path to the parent module
- `moduleType` — the POU/FB type name
- `variablePath` — full path including `._Latched` suffix (for Zenon import)
- `triggerCondition` — the boolean expression from `.Trigger :=`
- `triggerDelayMs` — debounce delay from `.TriggerDelay_ms :=`
- `condition` — human-readable condition name (CamelCase split)

**Example prompt:** *"List all Critical alarms in the ABF project"*

---

### 4. `twincat_state_machines`

**Extract state machines with full detail.** Returns states, transitions, timeouts, and the actual Structured Text code inside each CASE branch.

| Parameter | Required | Description |
|-----------|----------|-------------|
| `project_path` | Yes | Path to the TwinCAT PLC project directory |
| `module_name` | No | Filter by POU name (e.g. "CM_Sealer") |

**Returns:** JSON array where each state machine includes:
- `instanceName` — the DM_StateMachine variable name
- `displayName` — from constructor NameString argument
- `enumTypeName` — the E_xxx_States enum
- `initialState`, `transitionState` — from constructor args
- `ownerPou` — which FB owns this state machine
- `states[]` — each with name, enum value, code body, method name, and flags (isInitial, isError, isTimeout, isTransition)
- `transitions[]` — each with fromState, toState, timeoutValue, methodName

**Example prompt:** *"Show me the state machine in CM_Filler"*

---

### 5. `twincat_module_info`

**Detailed information about a single module/POU.** Shows everything about one FB: its variables, methods, properties, inheritance, alarms, state machines, and IO.

| Parameter | Required | Description |
|-----------|----------|-------------|
| `project_path` | Yes | Path to the TwinCAT PLC project directory |
| `module_name` | Yes | The POU name (e.g. "CM_Sealer", "EM_Fill", "UM_Machine") |

**Returns:** JSON with full module detail. If the module is not found, returns a list of available POU names.

**Example prompt:** *"What variables and methods does CM_Filler have?"*

---

### 6. `twincat_io_map`

**Extract all IO mappings.** Lists every variable with an `AT` binding to physical hardware addresses.

| Parameter | Required | Description |
|-----------|----------|-------------|
| `project_path` | Yes | Path to the TwinCAT PLC project directory |

**Returns:** JSON array where each mapping includes:
- `variableName` — the ST variable name
- `dataType` — BOOL, WORD, DWORD, etc.
- `atBinding` — the AT address (e.g. `%I*`, `%QB10`, `%IX0.0`)
- `direction` — Input, Output, or Memory
- `sourceGvl` — which GVL it was declared in
- `sourcePou` — which POU it was declared in (if inside an FB)

**Example prompt:** *"Show me all the IO mappings in the project"*

---

## How the Analysis Works

### Source File Parsing

TwinCAT 3 stores source code in XML files with Structured Text embedded in CDATA sections:

| File Type | Contains |
|-----------|----------|
| `.TcPOU` | Function Blocks, Programs, Functions — with nested Methods and Properties |
| `.TcDUT` | ENUM, STRUCT, UNION type definitions |
| `.TcGVL` | Global Variable Lists (including Objects.TcGVL which defines the instance tree) |

The parser uses `System.Xml.Linq` for the XML layer, then regex-based parsing for the Structured Text declarations (VAR blocks, type headers, AT bindings, constructor arguments).

### Object Tree Building

1. Finds the `Objects` GVL which declares root instances (e.g. `Machine : UM_Machine`)
2. For each root instance, looks up the FB type in the parsed POUs
3. Resolves the full inheritance chain (EXTENDS) and merges inherited variables
4. Recursively expands child FB instances into tree nodes
5. Marks `REFERENCE TO` children as links (not recursed into)
6. Tags each node with its ISA-88 layer (UM/EM/CM/DM) from the type name prefix

### Alarm Analysis

1. Walks the object tree, finding all `DM_TriggeredLatch` variables prefixed with `ALM_`
2. For each alarm, examines the owning POU's `_Alarms()` method
3. Parses the severity assignment blocks:
   ```
   _AlarmsPresentCritical := ALM_CommandAborted.Latched OR ...;
   _AlarmsPresentProcess  := ALM_PowderLevelLowLow.Latched OR ...;
   ```
4. If an alarm's `.Latched` appears in a severity block, that's its category
5. Also extracts `.Trigger :=` conditions and `.TriggerDelay_ms :=` values

**Unresolved alarms** are classified by reason:

| Reason | Meaning |
|--------|---------|
| **BaseClass** | Module extends `CM_BASECLASS` (not `CM_ControlModule`) — only has `_AlarmsPresent` with no severity split. The parent module determines severity at runtime. |
| **NoMethod** | The POU declares alarms but has no `_Alarms()` method override — inherits base behaviour without categorising its own alarms. |
| **Missing** | The alarm is triggered and reset in code but was never added to any `_AlarmsPresentXxx` assignment. Implementation gap. |
| **Dead** | The alarm variable is declared in the VAR block but never triggered, updated, or referenced in any executable code. Placeholder or leftover. |

### State Machine Extraction

1. Finds `DM_StateMachine` instances from variable declarations
2. Parses constructor arguments for NameString, InitialState, TransitionState
3. Searches all methods for `CASE xxx.State OF` blocks matching the SM variable
4. Extracts each state label and its code body (the ST code between state labels)
5. Extracts `GotoState(StateNext := ..., TimeoutNext := ...)` transitions
6. Cross-references with the ENUM DUT to get the full state list with values
7. Marks special states: initial (green), error (red), timeout (red), transition

### IO Mapping

Scans all GVL and POU variables for `AT` bindings:
- `%I*` / `%IB` / `%IW` / `%ID` / `%IX` → Input
- `%Q*` / `%QB` / `%QW` / `%QD` / `%QX` → Output
- `%M*` / `%MB` / `%MW` / `%MD` / `%MX` → Memory

---

## Caching

The analyzer caches results per project path within each session. Subsequent tool calls against the same project return instantly. The cache is cleared when the MCP server restarts.

---

## Limitations

- **Compiled libraries** — Types defined in `.compiled-library` files (e.g. `DM_TriggeredLatch`, `TExecution`, `TClock`) cannot be parsed. They appear in the `unresolvedTypes` list. The analyzer knows about `DM_TriggeredLatch` and `DM_StateMachine` by name for alarm and state machine detection.

- **REFERENCE TO recursion** — Variables declared as `REFERENCE TO` are shown as link nodes but not recursed into, since they point to instances owned elsewhere. This means the tree may not include alarm paths through reference chains.

- **Runtime-resolved severity** — Some alarms (particularly in `CM_BASECLASS`-derived modules like `CM_Axis_V5`, `CM_AI`) have their severity determined by the parent module at runtime, not by the declaring module. These show as Unresolved/BaseClass.

- **Multi-instance type reuse** — The same FB type instantiated multiple times (e.g. `CM_TouchProbe` x12) produces separate tree branches with independent alarm paths. This is correct behaviour but increases the total alarm count.

---

## File Locations

| Item | Path |
|------|------|
| Executable (Debug) | `src\AxiomCode.TwinCAT.CodeAnaliser\bin\Debug\net8.0\AxiomCode.TwinCAT.CodeAnaliser.exe` |
| Executable (Release) | `src\AxiomCode.TwinCAT.CodeAnaliser\bin\Release\net8.0\AxiomCode.TwinCAT.CodeAnaliser.exe` |
| HTML Template | `src\AxiomCode.TwinCAT.CodeAnaliser\Templates\viewer.html` |
| Claude Desktop Config | `%APPDATA%\Claude\claude_desktop_config.json` |
| Claude Code Config | `.mcp.json` in workspace root |

---

## Troubleshooting

| Problem | Solution |
|---------|----------|
| "0 POUs parsed" | Check the `project_path` points to the directory containing .TcPOU files, not the .sln or .tsproj |
| "0 alarms" with POUs found | Ensure the project has an `Objects.TcGVL` with root FB instances — the tree builder starts from there |
| "Unresolved types" count high | Normal — these are Beckhoff library types (TON, MC_MoveAbsolute, etc.) not in project source |
| HTML file very large | Expected for large projects — the JSON data is embedded. A 100+ POU project typically produces 2-3MB HTML |
| MCP server not appearing | Restart Claude Code/Desktop after adding to config. Check the exe path exists and .NET 8 runtime is installed |

---

## Version

- **Server:** AxiomCode.TwinCAT.CodeAnaliser v1.0.0
- **Framework:** .NET 8.0
- **MCP Protocol:** ModelContextProtocol v0.7.0-preview.1
- **Author:** Axiotech Automation Ltd — systems@axiotech.co.uk
