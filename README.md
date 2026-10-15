# Nextra toolbox for AxisVM

Seven AxisVM X8 add-ons for model preparation and result extraction, with a companion Rhino AxisBridge exporter.

[Install the toolbox](AxisVM%20plugin/docs/INSTALLATION.md) ? [Illustrated user guide](AxisVM%20plugin/docs/user-guide/README.md) ? [Build instructions](AxisVM%20plugin/docs/HOW-TO-BUILD.md)

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

## UI refresh

The dialogs share blue headers, an orange accent, clear fields and separate action rows. Settings scroll when the display cannot show the full content; result-window actions wrap on narrow windows. Each toolbar pictogram uses engineering geometry without lettering.

This update preserves the existing structural algorithms, input defaults, validation, result calculations and COM registration. Existing WIP labels remain. See the [validation record](AxisVM%20plugin/docs/UI-REFRESH-VALIDATION.md).

## Source layout

- `AxisVM plugin/AxisVM plugin/`: C# add-on entry points, WinForms dialogs, functions and shared helpers.
- `AxisVM plugin/Checks/`: existing algorithm checks and the offline UI rendering fixture.
- `AxisVM plugin/docs/`: installation, illustrated walkthroughs and technical notes.
- `Rhino/AxisBridge/`: companion Rhino plugin, unchanged by the UI refresh.
- `scripts/`: installation with backups, and repeatable preview rendering.

```powershell
dotnet build 'AxisVM plugin\AxisVM plugin.slnx' -c Release -m:1
.\scripts\Install-Addons.ps1
```

Requires Windows, AxisVM X8, and the .NET Framework 4.8 targeting pack for source builds. Vendor assemblies are resolved from the local AxisVM installation. The existing source history and original remote are preserved.
