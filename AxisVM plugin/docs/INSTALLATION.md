# Install the Nextra AxisVM toolbox

## Ready-built package

1. Install AxisVM X8 with the .NET Framework 4.8 runtime. Close every AxisVM instance.
2. Download the toolbox ZIP from this repository's **Releases** and extract it to a local folder.
3. Copy the seven DLLs in its `AddOns` folder into your AxisVM `AddOns` folder (normally `C:\AXISVM X8\AddOns`). Replace the older versions with the same names.
4. Start AxisVM. The seven `N.` tools appear in the add-ons menus/toolbars; their existing menu placement and WIP labels are preserved.

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
| A menu entry still says WIP | Deliberate: this UI update retains the existing feature status. |
| Rhino export not available | Build/install the separate companion in `Rhino/AxisBridge`, then use `AxExport`. |

[All tools](user-guide/README.md) ? [Development details](HOW-TO-BUILD.md)
