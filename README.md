# AxisVM and Rhino tools

One Git repository contains the AxisVM add-ons and the companion Rhino AxisBridge plugin.

- `AxisVM plugin/`: add-ons, solution, checks and AxisVM documentation. See [the AxisVM README](AxisVM%20plugin/README.md).
- `Rhino/AxisBridge/`: Rhino plugin source and project.

Build the AxisVM solution with `dotnet build "AxisVM plugin/AxisVM plugin.slnx"`.
Build Rhino with `dotnet build "Rhino/AxisBridge/AxisBridge.csproj"`.
Both target .NET Framework 4.8 and require their local AxisVM or Rhino assemblies.
See [the build guide](AxisVM%20plugin/docs/HOW-TO-BUILD.md) for installation details.

## Cleanup

Archived ConcreteDesign/StudsWall projects and pre-refactor file backups were removed
from the working tree. Their source remains in earlier Git commits. The former Lines ->
One-Way Panel and Convert Load Panel -> One-Way add-ons are merged into one add-on,
N. One-Way Load Panel; it shares one-way geometry helpers with Rhino import.

Generated build files, IDE settings, Python environments and calibration runs are
ignored. The existing AxisVM Git history and `origin` remote are preserved. Run Git
commands from this folder to manage both projects together.
