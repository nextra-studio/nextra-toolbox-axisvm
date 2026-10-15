# How to create, build and install an AxisVM add-on

[Installation quick start](INSTALLATION.md) ? [Illustrated tool guides](user-guide/README.md)

## 1. How an add-on works

An AxisVM add-on is a .NET Framework 4.8 **class library (DLL)** placed in `C:\AXISVM X8\AddOns`.
AxisVM loads it at start-up. The DLL contains one class implementing
`IAxisVMDotNetAddonPluginInterface_v1_0`, which provides:

- `Plugin_MenuItemText` – text in the menu
- `Plugin_Execute(iAxisVMApp)` – called when the user clicks; `iAxisVMApp` is the COM `IAxisVMApplication`
- `Addon_*` members – toolbar hint text and the 16x16 icon (`Addon_IconBitmap`)

Required references (found in `C:\AXISVM X8`):

- `AxisVMDotNetAddonPluginInterface_v1.0.FW4.dll`
- `Interop.AxisVM.FW4.dll` (set *Embed Interop Types = True*)

## 2. Code structure used in this repo

| Layer | Folder | Role |
|---|---|---|
| Add-on entry | `Addons/` | Implements the interface, calls a `Functions` class |
| Function | `Functions/` | `Execute(app)` – checks the model, opens the form, runs the logic |
| UI | `UI/` | WinForms dialog |
| Core | `Core/` | Shared helpers used by several add-ons |

## 3. Create a new add-on

1. Copy an existing small add-on as a template (e.g. `SectionModifierAddon.csproj` + its `Addons/`,
   `UI/`, `Functions/` files).
2. In the new `.csproj` change `<ProjectGuid>` (new GUID), `<AssemblyName>`, and the `<Compile Include>` list.
3. Add the project to `AxisVM plugin.slnx`.
4. Change `Plugin_MenuItemText` / `Addon_HintText` in the new `Addons/*.cs`.
5. Write a `docs/addons/<Name>.md` describing the workflow.
6. Comment the code generously (every function, most lines) so others can follow it.

## 4. Build

Visual Studio: open `AxisVM plugin.slnx`, choose Debug, *Build Solution*.

Command line (from the repository root):

```powershell
dotnet build 'AxisVM plugin\AxisVM plugin.slnx' -c Release -m:1
# Override the default C:\AXISVM X8 installation if needed:
dotnet build 'AxisVM plugin\AxisVM plugin.slnx' -c Release -m:1 '/p:AxisVMDir=D:\AxisVM X8'
```

Output: `AxisVM plugin/AxisVM plugin/bin/Release/<AssemblyName>.dll`.

## 5. Install

Close AxisVM, then copy the DLL:

```powershell
.\scripts\Install-Addons.ps1
```

(`C:\AXISVM X8\Plugins` is for plugins, not used for now.) Start AxisVM; the add-on is in the menu / toolbar.

## 6. Troubleshooting

- **Add-on not shown** – DLL not in `AddOns`, wrong target framework (must be 4.8), or the old DLL is locked
  because AxisVM is still open.
- **Reference not found** - set `AxisVMDir` to the installed AxisVM folder. The default is set in `Directory.Build.props`; the repository depth does not matter.
- **COM API questions** – see `docs/reference/axisvm_com_17100.pdf` (searchable text in `axisvm_com.txt`).

## 7. Rhino side (AxisBridge)

`../Rhino/AxisBridge` is a RhinoCommon plugin (`.rhp`) with `Ax*` commands to classify geometry, set
attributes and export a model; the *Rhino Import* add-on reads that export into AxisVM.
