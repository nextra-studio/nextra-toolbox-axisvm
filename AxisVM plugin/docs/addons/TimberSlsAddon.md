# Timber SLS Check Addon

[Illustrated user guide](../user-guide/timber-sls.md) | [EC5 SLS specification](../reference/ec5-sls-timber.md)

Checks timber deflection (EN 1995-1-1:2004 + NL NA, Harff) and floor vibration (EN 1995-1-1:2004 §7.3 or EN 1995-1-1:2025 §9.3) for Floor and Roof members. The add-on reads results and never changes the model, apart from a temporary drawing layer.

## Flow

1. **Members**: line ids per group. *Whole beam* mode chains collinear selected lines and splits them at nodal supports, columns (IsColumn or vertical) and, as an option, non-parallel beams or ribs. *Each line* mode keeps every line on its own. A single span with both ends free is skipped.
2. **Loads**: load cases with their load group: type → role, ψ0/ψ1/ψ2, and exclusive groups. Overrides are stored per model by load-case name. Combinations come from one of three sources:
   - *Auto*: `TimberSlsMath.Generate`, each variable group leading in turn, the others at ψ0.
   - Existing AxisVM combinations: `GetCombination` factors.
   - Manual.
3. **Results**: `GetLineDisplacementsByLoadCaseId` per line and load case in the global system. Each result is projected on the checked direction (perpendicular in the vertical plane / global Z / lateral), resampled onto one set of x positions and converted to mm. For timber lines the results are multiplied by 1/(1+kdef of the line), because AxisVM analyses timber at E_mean/(1+kdef).
4. **Reference** (doc §5.1): *Span* = minus the chord between both ends; *Cantilever* = minus the root displacement (and, as an option, the root tangent). *Auto* tries span, root at the start and root at the end, and keeps the one with the highest utilisation. A manual *Cantilever* takes the end that moves least as the root.
5. **Check**: every enabled limit row for every combination, after the group's creep model (`ApplyCreepModel`). Then vibration for the Floor spans (not cantilevers): two-span chains are double span floors, longer chains use their largest span.
6. **Output**: the results table, the plan view and deflection diagram (`UI/TimberSlsViews.cs`), CSV / clipboard export, and the layer `Timber SLS check (temporary)` with segments coloured by local utilisation.

## Sources

- `Addons/TimberSlsAddon.cs`: registration, toolbar icon.
- `Functions/TimberSlsFunction.cs`: COM access (`TimberSlsBackend`), spans, results, run, overlay.
- `Core/TimberSlsMath.cs`: all formulas (no COM), checked by `Checks/TimberSlsMathChecks`.
- `Core/TimberSlsSettings.cs`: settings, presets, editable dropdowns, JSON in `%AppData%\NextraAxisVM\TimberSls` (override with `NEXTRA_TIMBERSLS_DIR`).
- `UI/TimberSlsForm.cs`: the modeless window.

## Checks

- `Checks/TimberSlsMathChecks`: the worked examples of the specification (Bastion IX, the vibration sheet for 2004 and 2025, Table 9.4, the combination generator).
- `Checks/TimberSlsCaseChecks <folder>`: nine cases in a scratch AxisVM, each compared with independent hand calculations (46 checks, largest difference 0.04 %), writing `report.csv` / `report.md` and screenshots of every case:
  - simple: a simply supported beam with UDL, a cantilever, a point load
  - complicated: a span with overhang (both cantilever references), two equal spans, five load groups with exclusive wind
  - slanted: an inclined rafter, a plan-diagonal unequal two-span, a 3D-sloped rafter with an eave across three lines
- `Checks/TimberSlsLiveChecks <folder>`: starts its own scratch AxisVM and builds a simply supported beam, a cantilever and a two-span beam. It compares the results with 5qL⁴/384EI, qL⁴/8EI and qL⁴/185EI, checks the Auto references, span splitting, the overlay and the type overrides, and saves window screenshots.
