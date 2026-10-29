# Nextra toolbox for AxisVM

AxisVM X8 add-ons for model preparation and result extraction, plus the companion Rhino 8 plug-in **Nextra-AxisRhinoImport**. Each tool has its own icon, dialog and illustrated guide. The toolbox keeps growing: new tools are added to one list ([tools.json](tools.json)) and then appear in the installer, the download ZIP and these docs.

**[Install (about 3 minutes)](#install)** | **[Download ZIP](https://github.com/nextra-studio/nextra-toolbox-axisvm/releases/latest/download/nextra-axisvm-toolbox.zip)** | [Illustrated user guide](AxisVM%20plugin/docs/user-guide/README.md) | [Rhino commands](AxisVM%20plugin/docs/user-guide/rhino-commands.md) | [Add a tool](AxisVM%20plugin/docs/ADDING-A-TOOL.md) | [Build from source](AxisVM%20plugin/docs/HOW-TO-BUILD.md)

## Tools

<!-- gen:tools-root -->
| | Tool | What it does | Status |
|---|---|---|---|
| ![Rhino import](AxisVM%20plugin/docs/images/icons/RhinoImport@4x.png) | [Rhino import](AxisVM%20plugin/docs/user-guide/rhino-import.md) | Import AxisBridge JSON geometry and structural attributes. | Released |
| ![One-way load panels](AxisVM%20plugin/docs/images/icons/OneWayLoadPanel@4x.png) | [One-way load panels](AxisVM%20plugin/docs/user-guide/one-way-panels.md) | Convert panels or create them from lines; choose span and edge strips. | Released |
| ![Load panel tools](AxisVM%20plugin/docs/images/icons/LoadPanelTools@4x.png) | [Load panel tools](AxisVM%20plugin/docs/user-guide/panel-tools.md) | Convert panel/domain, split, and merge panels. | Released |
| ![End pin assign](AxisVM%20plugin/docs/images/icons/EndPinAssign@4x.png) | [End pin assign](AxisVM%20plugin/docs/user-guide/end-pins.md) | Apply hinges and semi-rigid end releases. | Released |
| ![Reaction transfer](AxisVM%20plugin/docs/images/icons/ReactionTransfer@4x.png) | [Reaction transfer](AxisVM%20plugin/docs/user-guide/reaction-transfer.md) | Turn support reactions into point loads in a target model. | Released |
| ![Section modifier](AxisVM%20plugin/docs/images/icons/SectionModifier@4x.png) | [Section modifier](AxisVM%20plugin/docs/user-guide/section-modifier.md) | Set rectangular section width and depth. | Released |
| ![Connection loads](AxisVM%20plugin/docs/images/icons/ConnectionLoadExtractor@4x.png) | [Connection loads](AxisVM%20plugin/docs/user-guide/connection-loads.md) | Extract concurrent forces and moments for governing combinations. | Released |
<!-- /gen:tools-root -->

A tool that is still being developed carries **(WIP)** in its AxisVM menu text and in this table. New tools start as WIP until they are validated; all current tools are released.

## Install

**About 3 minutes, no hassle:** download one ZIP, double-click `Install-Addons.bat`, install the Rhino plug-in from PackageManager.

**Windows | AxisVM X8 | Rhino 8.6 or newer for the Rhino plug-in.**

![Three steps: download, install add-ons, install in Rhino](AxisVM%20plugin/docs/images/install/overview.svg)

### 1. Download

**[Download nextra-axisvm-toolbox.zip](https://github.com/nextra-studio/nextra-toolbox-axisvm/releases/latest/download/nextra-axisvm-toolbox.zip)** and extract it anywhere (for example your Downloads folder). It contains the add-on DLLs, the Rhino plug-in files, `Install-Addons.bat`, `Install-Addons.ps1`, `tools.json` and an offline copy of the install guide.

![On the GitHub Releases page, download nextra-axisvm-toolbox.zip](AxisVM%20plugin/docs/images/install/download-release.svg)

### 2. AxisVM add-ons

Close every AxisVM window, then choose **one** of three ways, easiest first. All three end with the same files in the same folder.

<details open>
<summary><strong>A. Double-click (easiest)</strong>: no PowerShell needed</summary>

Open the extracted folder, **double-click `Install-Addons.bat`**, and wait for the list of installed DLLs. No PowerShell window or settings change needed.

![Double-click Install-Addons.bat](AxisVM%20plugin/docs/images/install/double-click.svg)

- A blue **Windows protected your PC** box may appear for a downloaded file: click **More info**, then **Run anyway**.
- If AxisVM is not in `C:\AXISVM X8`, the script asks for your AddOns folder. You can also drag that folder onto `Install-Addons.bat`.
- If Windows needs administrator rights for the AxisVM folder, the window offers **Try again as administrator**.

</details>

<details open>
<summary><strong>B. PowerShell</strong>: the same installer, started from a terminal</summary>

Open PowerShell in the extracted folder (Shift + right-click in the folder, then **Open in Terminal**) and run:

```powershell
.\Install-Addons.ps1
# AxisVM installed elsewhere:
.\Install-Addons.ps1 -AddOnsDir 'D:\AxisVM X8\AddOns'
```

![Install-Addons.ps1 installs each DLL](AxisVM%20plugin/docs/images/install/run-script.svg)

The script backs up existing toolbox DLLs to `%LOCALAPPDATA%\NextraToolbox\backups\<timestamp>`, copies every DLL listed in `tools.json`, and checks the SHA-256 hash of each copy. It never deletes other files. Add `-WhatIf` to preview.

If PowerShell refuses to run it, use `powershell -ExecutionPolicy Bypass -File .\Install-Addons.ps1`, or option B.

</details>

<details>
<summary><strong>C. Fallback: copy the files yourself</strong> (no script at all)</summary>

1. Open the ZIP's `AddOns` folder and your AxisVM `AddOns` folder (normally `C:\AXISVM X8\AddOns`).
2. Select these DLLs and copy them into the AxisVM folder, replacing older versions:

<!-- gen:dll-list -->
- `RhinoImportAddon.dll`
- `OneWayLoadPanelAddon.dll`
- `LoadPanelToolsAddon.dll`
- `EndPinAssignAddon.dll`
- `ReactionTransferAddon.dll`
- `SectionModifierAddon.dll`
- `ConnectionLoadExtractorAddon.dll`
<!-- /gen:dll-list -->

![Copy the DLLs from the ZIP into the AxisVM AddOns folder](AxisVM%20plugin/docs/images/install/manual-copy.svg)

3. If the copy is denied, use an Explorer window run as administrator.
4. If AxisVM does not show a tool, right-click the DLL, choose **Properties**, tick **Unblock**, press OK, and restart AxisVM.

![Unblock a downloaded file](AxisVM%20plugin/docs/images/install/unblock.svg)

</details>

Start AxisVM. The tools appear in the add-ons menu, each with its own icon:

![The tools in the AxisVM add-ons menu](AxisVM%20plugin/docs/images/install/axisvm-tools.svg)

### 3. Rhino plug-in: Nextra-AxisRhinoImport

<details open>
<summary><strong>A. Package Manager (recommended)</strong></summary>

In Rhino run **PackageManager**, search **Nextra-AxisRhinoImport**, click **Install**, then restart Rhino.

![Install Nextra-AxisRhinoImport in Rhino Package Manager](AxisVM%20plugin/docs/images/install/rhino-package-manager.svg)

Command line: `yak install Nextra-AxisRhinoImport` (`yak.exe` is in `C:\Program Files\Rhino 8\System`).

</details>

<details>
<summary><strong>B. Fallback: install the file yourself</strong></summary>

Use the `Rhino` folder from the ZIP (`AxisBridge.rhp` and `Newtonsoft.Json.dll` together). In Rhino choose **Tools > Options > Plug-ins > Install...**, select `AxisBridge.rhp`, and restart Rhino. If Rhino refuses the file, Unblock it as shown above.

![Install AxisBridge.rhp from Rhino Options](AxisVM%20plugin/docs/images/install/rhino-manual.svg)

</details>

Type `Ax` in Rhino's command line to list `AxPoint`, `AxLine`, `AxDomain`, `AxLibrary`, `AxExport` and the other commands. **[Rhino command guide with screenshots](AxisVM%20plugin/docs/user-guide/rhino-commands.md)**.

### Try it

1. In Rhino: classify geometry with `AxPoint`, `AxLine` and `AxDomain`, then run `AxExport` and save the JSON.
2. In AxisVM: open **N. Rhino Import** and pick that file. See the [Rhino import guide](AxisVM%20plugin/docs/user-guide/rhino-import.md).

<details>
<summary>Update, roll back and troubleshooting</summary>

- **Update:** download the new ZIP and repeat steps 2 and 3. The script backs up the old DLLs first.
- **Roll back:** close AxisVM and copy the DLLs from `%LOCALAPPDATA%\NextraToolbox\backups\<timestamp>` back into `AddOns`. Rhino: `_PackageManager` > Installed > Uninstall.

| Problem | Fix |
|---|---|
| Tool missing in AxisVM | Check the DLL is in the AxisVM `AddOns` folder, Unblock it, restart AxisVM. |
| Copy reports a locked DLL | Close every AxisVM process and retry. |
| Old dialog or icon remains | Replace the DLL and restart AxisVM; a loaded DLL cannot be replaced reliably. |
| No `Ax` commands in Rhino | Restart Rhino and confirm the plug-in under Options > Plug-ins. Package Manager needs Rhino 8.6 or newer. |
| Build cannot find AxisVM references | Set `AxisVMDir` (see the [build notes](AxisVM%20plugin/docs/HOW-TO-BUILD.md)). |

Full text: [INSTALLATION.md](AxisVM%20plugin/docs/INSTALLATION.md).

</details>

## Adding and releasing tools

1. Write the add-on (copy a small existing one), then add one entry to [tools.json](tools.json).
2. Run `python scripts/Build-Docs.py`. The README and guide tables, the install pictures and a stub guide page update.
3. Run `.\scripts\New-Release.ps1` to build `dist\nextra-axisvm-toolbox.zip`, then publish it as a GitHub release (keep the asset name).

Details: [ADDING-A-TOOL.md](AxisVM%20plugin/docs/ADDING-A-TOOL.md).

## Source layout

- `tools.json`: the tool registry that drives the installer, the release ZIP and the docs.
- `AxisVM plugin/AxisVM plugin/`: add-on entry points, WinForms dialogs, functions and shared helpers.
- `AxisVM plugin/Checks/`: algorithm checks and the offline UI rendering fixture.
- `AxisVM plugin/docs/`: installation, illustrated guides (`images/icons/` holds one icon per tool) and technical notes.
- `Rhino/AxisBridge/`: Rhino plug-in source; `Rhino/yak/` holds its Package Manager manifest.
- `scripts/`: `Install-Addons.ps1`, `New-Release.ps1`, `Build-Docs.py`, `Build-YakPackage.ps1`, `Preview-UI.ps1`.

A source build requires Windows, AxisVM X8 and the .NET Framework 4.8 targeting pack; vendor assemblies are resolved from the local AxisVM installation:

```powershell
dotnet build 'AxisVM plugin\AxisVM plugin.slnx' -c Release -m:1
.\scripts\Install-Addons.ps1
```
