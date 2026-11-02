# Rhino Import Addon

[Illustrated user guide](../user-guide/rhino-import.md) · [How the link works, elements and limits](../reference/rhino-axisvm-link.md)

The add-on reads a file shared with the Rhino AxisBridge plug-in: an `AxExport` JSON or an `AxLink` link file. The dialog has
one file field and two tabs. **From Rhino** builds or updates the model: units, merge tolerance, crossing-line connection,
"Update what an earlier load built", "Only apply what changed" and a dry run. **To Rhino** sends what changed in AxisVM back
into the link file, and exports the material / section catalog for Rhino.

`RhinoImportLogic` records one undo step and calls `LinkLoader`, which reads the file, lets `AxisModelBuilder.PlanDiff`
decide what to rebuild (with "only what changed"), runs `AxisModelBuilder` on that part, and records in the link file what
AxisVM is now in line with (signatures, load panel ids, extras baselines). The builder holds BeginUpdate / EndUpdate itself and
joins members between two update blocks. "Send to Rhino" is `RhinoLinkSender` with `LinkExtras` for supports, releases,
springs, links and hinges.

## Sources

- `UI/RhinoImportForm.cs`, `UI/RhinoImportReportForm.cs`: dialog and report.
- `Functions/RhinoImportLogic.cs`: orchestration.
- `Core/LinkLoader.cs`: one load from Rhino.
- `Core/AxisModelBuilder*.cs`: the build; `AxisModelBuilder.Diff.cs` decides what "only what changed" rebuilds and carries
  AxisVM-made hinges and supports across a rebuild; `AxisModelBuilder.Link.cs` deletes what is replaced.
- `Core/LinkSignature.cs`, `Core/LinkState.cs`, `Core/LinkFile.cs`, `Core/LinkExtras.cs`, `Core/RhinoLinkSender.cs`: link record and Send.
- `Rhino/AxisBridge/`: companion Rhino plug-in in the repository root.

## Checks

`AxisVM plugin/Checks/RhinoUpdateLiveChecks.cs` (run through `scripts/Check-RhinoUpdate.ps1`) starts its own AxisVM. Modes:
the default update check, `link` (round trip), `diff <link>` (only-changes against full rebuilds), `elements` (supports,
foundations, links, hinges both ways), `roundtrip <axs>` (AxisVM-first on a real model), `load <link>`.

One-way panel generation shares the builder with the One-Way Load Panel add-on. Read [its implementation notes](OneWayLoadPanelAddon.md).
