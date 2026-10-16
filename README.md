# Nextra toolbox for AxisVM

Seven AxisVM X8 add-ons for model preparation and result extraction, with the companion Rhino 8 plug-in **Nextra-AxisRhinoImport** (AxisBridge).

[Installation](#installation) | [Illustrated user guide](AxisVM%20plugin/docs/user-guide/README.md) | [Build instructions](AxisVM%20plugin/docs/HOW-TO-BUILD.md)

![Simple, text-free toolbar icons at native size and enlarged](AxisVM%20plugin/docs/images/toolbar-icons.png)

| Tool | What it does |
|---|---|
| [Rhino import](AxisVM%20plugin/docs/user-guide/rhino-import.md) | Import AxisBridge JSON geometry and structural attributes. |
| [One-way load panels](AxisVM%20plugin/docs/user-guide/one-way-panels.md) | Convert panels or create them from lines; choose span and edge strips. |
| [Load panel tools](AxisVM%20plugin/docs/user-guide/panel-tools.md) | Convert panel/domain, split, and merge panels. |
| [End pin assign](AxisVM%20plugin/docs/user-guide/end-pins.md) | Apply hinges and semi-rigid end releases. |
| [Reaction transfer](AxisVM%20plugin/docs/user-guide/reaction-transfer.md) | Turn support reactions into point loads in a target model. |
| [Section modifier](AxisVM%20plugin/docs/user-guide/section-modifier.md) | Set rectangular section width and depth. |
| [Connection loads](AxisVM%20plugin/docs/user-guide/connection-loads.md) | Extract concurrent forces and moments for governing combinations. |

## Installation

### 1. AxisVM add-ons (quick install)

1. Close every AxisVM window.
2. Download the toolbox ZIP from this repository's **Releases** and extract it.
3. In PowerShell, inside the extracted folder, run:
   ```powershell
   .\Install-Addons.ps1
   # AxisVM installed elsewhere:
   .\Install-Addons.ps1 -AddOnsDir 'D:\AxisVM X8\AddOns'
   ```
4. Start AxisVM. The seven `N.` tools appear in the add-ons menu and toolbar.

The script backs up existing toolbox DLLs to `%LOCALAPPDATA%\NextraToolbox\backups\<timestamp>`, copies the seven DLLs and checks their SHA-256 hashes. It touches nothing else.

### 2. Fallback: copy the files yourself

Use this if the script is blocked (execution policy, company PC) or you prefer to do it by hand.

1. Close every AxisVM window.
2. Copy these seven DLLs into your AxisVM `AddOns` folder (normally `C:\AXISVM X8\AddOns`) and replace older copies:
   - `RhinoImportAddon.dll`
   - `OneWayLoadPanelAddon.dll`
   - `LoadPanelToolsAddon.dll`
   - `EndPinAssignAddon.dll`
   - `ReactionTransferAddon.dll`
   - `SectionModifierAddon.dll`
   - `ConnectionLoadExtractorAddon.dll`

   They are in the ZIP's `AddOns` folder, or in `AxisVM plugin\AxisVM plugin\bin\Release` after a source build.
3. Start AxisVM.

If Windows blocks a downloaded DLL, right-click it, choose **Properties** and tick **Unblock**. If the copy is denied, copy in an Explorer window run as administrator.

### 3. Rhino plug-in: Nextra-AxisRhinoImport

1. In Rhino 8 run the command `_PackageManager`.
2. Search for **Nextra-AxisRhinoImport**, install it, then restart Rhino.
3. The commands `AxPoint`, `AxLine`, `AxDomain`, `AxLibrary`, `AxExport` and the others are now available.

Command line alternative: `yak install Nextra-AxisRhinoImport`. Manual fallback and rollback: [INSTALLATION.md](AxisVM%20plugin/docs/INSTALLATION.md#rhino-plug-in-nextra-axisrhinoimport).

## Tool status

A tool still under development is labelled **(WIP)** in its AxisVM menu text and in the tool table above. All seven tools are currently released without the label. When a new tool is added it starts as WIP and stays listed here until it is validated.

## UI refresh

The dialogs share blue headers, an orange accent, clear fields and separate action rows. Settings scroll when the display cannot show the full content; result-window actions wrap on narrow windows. Each toolbar pictogram uses engineering geometry without lettering.

This update preserves the existing structural algorithms, input defaults, validation, result calculations and COM registration. See the [validation record](AxisVM%20plugin/docs/UI-REFRESH-VALIDATION.md).

## Source layout

- `AxisVM plugin/AxisVM plugin/`: C# add-on entry points, WinForms dialogs, functions and shared helpers.
- `AxisVM plugin/Checks/`: existing algorithm checks and the offline UI rendering fixture.
- `AxisVM plugin/docs/`: installation, illustrated walkthroughs and technical notes.
- `Rhino/AxisBridge/`: companion Rhino plug-in source; `Rhino/yak/` holds its package manifest.
- `scripts/`: installation with backups, Yak package build, and repeatable preview rendering.

```powershell
dotnet build 'AxisVM plugin\AxisVM plugin.slnx' -c Release -m:1
.\scripts\Install-Addons.ps1
```

Requires Windows, AxisVM X8, and the .NET Framework 4.8 targeting pack for source builds. Vendor assemblies are resolved from the local AxisVM installation. The existing source history and original remote are preserved.
