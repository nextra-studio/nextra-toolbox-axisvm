# Install the Nextra AxisVM toolbox

Three parts: the seven AxisVM add-ons (script or manual copy), and the Rhino plug-in **Nextra-AxisRhinoImport**.

## Ready-built package

1. Install AxisVM X8 with the .NET Framework 4.8 runtime. Close every AxisVM instance.
2. Download the toolbox ZIP from this repository's **Releases** and extract it to a local folder.
3. Copy the seven DLLs in its `AddOns` folder into your AxisVM `AddOns` folder (normally `C:\AXISVM X8\AddOns`). Replace the older versions with the same names.
4. Start AxisVM. The seven `N.` tools appear in the add-ons menus/toolbars; a tool still in development is labelled (WIP) in its menu text.

The ZIP contains only toolbox DLLs. Keep AxisVM's own assemblies and other add-ons in place. NextraAxisVM (the separate AI add-on) is not part of this package.

### Install with automatic backup

From the extracted package, run the included script in PowerShell:

```powershell
.\Install-Addons.ps1
# Different AxisVM location:
.\Install-Addons.ps1 -AddOnsDir 'D:\AxisVM X8\AddOns'
```

It verifies all seven input files before copying, backs up existing toolbox DLLs to `%LOCALAPPDATA%\NextraToolbox\backups\<timestamp>`, and compares the installed SHA-256 hashes. It does not delete other files or install vendor assemblies. `-WhatIf` previews the destination without installing.

If the installation directory requires administrator access, use a PowerShell window with the necessary Windows permissions or copy the files with Explorer.

### Fallback: copy the DLLs yourself

If the script is blocked (execution policy, company PC) or you prefer a manual install:

1. Close every AxisVM window.
2. Open the extracted ZIP's `AddOns` folder and the AxisVM `AddOns` folder (normally `C:\AXISVM X8\AddOns`) side by side.
3. Copy the seven DLLs listed below into the AxisVM folder, replacing older versions. Optionally keep a copy of the old DLLs first.
4. If Windows blocks a downloaded DLL, right-click it, choose **Properties** and tick **Unblock**. If the copy is denied, use an Explorer window run as administrator.
5. Start AxisVM.

DLLs: `RhinoImportAddon.dll`, `OneWayLoadPanelAddon.dll`, `LoadPanelToolsAddon.dll`, `EndPinAssignAddon.dll`, `ReactionTransferAddon.dll`, `SectionModifierAddon.dll`, `ConnectionLoadExtractorAddon.dll`.

## Rhino plug-in: Nextra-AxisRhinoImport

Requires Rhino 8 (8.6 or newer) on Windows.

**Package Manager (recommended)**

1. In Rhino run `_PackageManager`.
2. Search for **Nextra-AxisRhinoImport** and click Install.
3. Restart Rhino. Type `Ax` in the command line to see `AxPoint`, `AxLine`, `AxDomain`, `AxLibrary`, `AxEdgeHinge`, `AxVisualize`, `AxClear`, `AxUpdateGuid`, `AxLayerTidyUp` and `AxExport`.

Command line alternative: `yak install Nextra-AxisRhinoImport` (`yak.exe` is in `C:\Program Files\Rhino 8\System`).

**Fallback: install the file yourself**

1. Download `AxisBridge.rhp` and `Newtonsoft.Json.dll` (from the Release ZIP's `Rhino` folder, or `Rhino\AxisBridgein\Release` after a source build) and keep them in the same folder.
2. Right-click both files, Properties, tick **Unblock**.
3. In Rhino choose Tools > Options > Plug-ins > Install..., select `AxisBridge.rhp`, and restart Rhino. Or drag `AxisBridge.rhp` onto a Rhino window.

**Rollback:** `_PackageManager` > Installed > Uninstall, or `yak uninstall Nextra-AxisRhinoImport`.

**For maintainers (publishing a new version):** raise `version` in `Rhino/yak/manifest.yml`, run `.\scripts\Build-YakPackage.ps1`, then `yak login` and `yak push Rhino\yak\out\<file>.yak`.

## Build from source

Install the .NET 10 SDK or a Visual Studio/MSBuild version supporting the `.slnx` solution, plus the .NET Framework 4.8 targeting pack. The projects read the two AxisVM assemblies from your installed AxisVM folder.

From the repository root:

```powershell
dotnet build 'AxisVM plugin\AxisVM plugin.slnx' -c Release -m:1
.\scripts\Install-Addons.ps1
```

For a different AxisVM installation:

```powershell
dotnet build 'AxisVM plugin\AxisVM plugin.slnx' -c Release -m:1 '/p:AxisVMDir=D:\AxisVM X8'
.\scripts\Install-Addons.ps1 -AddOnsDir 'D:\AxisVM X8\AddOns'
```

Output: `AxisVM plugin\AxisVM plugin\bin\Release\`. Install only:

- `RhinoImportAddon.dll`
- `OneWayLoadPanelAddon.dll`
- `LoadPanelToolsAddon.dll`
- `EndPinAssignAddon.dll`
- `ReactionTransferAddon.dll`
- `SectionModifierAddon.dll`
- `ConnectionLoadExtractorAddon.dll`

## Verify and roll back

Open a dialog and check the blue header and simple pictogram. Cancel closes settings without applying the operation. Select objects in AxisVM before opening a tool; see the [illustrated guide](user-guide/README.md).

To roll back, close AxisVM and copy the matching seven DLLs from the backup folder to `AddOns`, then restart AxisVM. DLLs already loaded by a running process cannot be replaced reliably.

## Troubleshooting

| Problem | Check |
|---|---|
| Build cannot find AxisVM references | Set `AxisVMDir` to the folder containing `Interop.AxisVM.FW4.dll` and `AxisVMDotNetAddonPluginInterface_v1.0.FW4.dll`. |
| Copy reports a locked DLL | Close every AxisVM process and retry. |
| Old dialog or icon remains | Confirm the DLL path, replace the corresponding DLL, then restart AxisVM. |
| A menu entry says (WIP) | Deliberate: the tool is newly added and not yet validated. |
| Rhino export not available | Install **Nextra-AxisRhinoImport** (see below), restart Rhino, then use `AxExport`. |
| Package Manager cannot find the Rhino plug-in | Use Rhino 8.6 or newer, or use the manual fallback below. |

[All tools](user-guide/README.md) ? [Development details](HOW-TO-BUILD.md)
