# Rhino Import Addon

[Illustrated user guide](../user-guide/rhino-import.md)

The add-on reads a JSON file written by the Rhino AxisBridge `AxExport` command. The dialog previews source units, counts and document-library metadata, and exposes length/load units, merge tolerance, crossing-line connection and a dry-run option.

The unchanged `RhinoImportLogic` parses the model and passes an `ImportOptions` instance to `AxisModelBuilder`. A normal run requests an undo record and wraps the builder in BeginUpdate/EndUpdate; a dry run writes no model data. The report includes substitutions, attribute issues, export warnings and import statistics.

## Sources

- `UI/RhinoImportForm.cs`, `UI/RhinoImportReportForm.cs`: settings and full report.
- `Functions/RhinoImportLogic.cs`: import orchestration.
- `Core/AxisModelBuilder*.cs`, `Core/RhinoModel.cs`, `Core/AxUnits.cs`: model and units implementation.
- `Rhino/AxisBridge/`: companion export plugin in the repository root.

One-way panel generation shares the existing builder with the One-Way Load Panel add-on. Read [its implementation notes](OneWayLoadPanelAddon.md) for receiver and strip behavior. The import dialog has no overwrite/replace switch.
