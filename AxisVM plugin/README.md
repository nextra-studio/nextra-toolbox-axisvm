# AxisVM Plugin / Add-ons

[Installation](docs/INSTALLATION.md) ? [Illustrated guide](docs/user-guide/README.md) ? [UI validation](docs/UI-REFRESH-VALIDATION.md)

A set of C# (.NET Framework 4.8) add-ons for **AxisVM X8** (finite element software), plus a companion
Rhino plugin (`AxisBridge`, in `../Rhino/`) that prepares geometry for import.

## Folder layout

```
AxisVM plugin/                      <- AxisVM sources (repository root is one level above)
├─ AxisVM plugin.slnx               Solution (one project per add-on)
├─ README.md                        This file
├─ AxisVM plugin/                   Source code of the add-ons
│  ├─ *.csproj                      One project per add-on (each builds one DLL)
│  ├─ Addons/                       Entry points (AxisVM add-on interface, menu + toolbar)
│  ├─ UI/                           WinForms dialogs
│  ├─ Functions/                    Command logic called by the add-ons
│  ├─ Core/                         Shared helpers (model builder, JSON, DTOs, ...)
│  └─ Properties/AssemblyInfo.cs
├─ docs/
│  ├─ HOW-TO-BUILD.md               How to create / build / install an AxisVM add-on
│  ├─ addons/                       One .md per add-on describing its workflow and logic
│  └─ reference/                    AxisVM COM reference (axisvm_com_17100.pdf, axisvm_com.txt)
├─ lib/                             Interop.AxisVM.FW4.dll (local copy, for reference)
├─ .agents/rules/reference.md       Rules/links for the AI assistant
└─ .claude/                         Claude Code local settings
```

## Add-ons

| Menu text in AxisVM | Project / DLL | Docs |
|---|---|---|
| N. Rhino Import | RhinoImportAddon | [Rhino Import guide](docs/user-guide/rhino-import.md) |
| N. One-Way Load Panel (selected panels -> one-way, or lines -> new panels) | OneWayLoadPanelAddon | [docs/addons/OneWayLoadPanelAddon.md](docs/addons/OneWayLoadPanelAddon.md) |
| N. Load Panel Tools | LoadPanelToolsAddon | [docs/addons/LoadPanelToolsAddon.md](docs/addons/LoadPanelToolsAddon.md) |
| N. End Pin Assign (WIP) | EndPinAssignAddon | [docs/addons/EndPinAssignAddon.md](docs/addons/EndPinAssignAddon.md) |
| N. Reaction Transfer (WIP) | ReactionTransferAddon | [docs/addons/ReactionTransferAddon.md](docs/addons/ReactionTransferAddon.md) |
| N. Section Modifier (WIP) | SectionModifierAddon | [docs/addons/SectionModifierAddon.md](docs/addons/SectionModifierAddon.md) |
| N. Connection Load Extractor (WIP) | ConnectionLoadExtractorAddon | [docs/addons/ConnectionLoadExtractorAddon.md](docs/addons/ConnectionLoadExtractorAddon.md) |

## Quick start

1. Install AxisVM X8 at `C:\AXISVM X8` (the projects reference the DLLs found there).
2. Open `AxisVM plugin.slnx` in Visual Studio and build, **or** see [docs/HOW-TO-BUILD.md](docs/HOW-TO-BUILD.md)
   for the command line.
3. Copy the built DLL from `AxisVM plugin/bin/Debug/` to `C:\AXISVM X8\AddOns\`.
4. Restart AxisVM — the add-ons appear in the Add-ons menu / toolbar.

## References

- AxisVM docs: <https://axisvm.eu/docs/how-to-create-a-simple-plugin-for-axisvm/>
- COM server example: <https://axisvm.eu/docs/creation-of-a-simple-model-using-axisvm-com-server/>
- Local COM reference: [docs/reference/axisvm_com_17100.pdf](docs/reference/axisvm_com_17100.pdf)
