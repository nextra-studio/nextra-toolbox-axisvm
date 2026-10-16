# Connection Load Extractor Addon

[Illustrated user guide](../user-guide/connection-loads.md)

## Overview
The Connection Load Extractor reads the end forces of selected beams and ribs at selected nodes, for selected load
combinations, and reports them as the **force each member exerts on the node**. The loads are given in the member's
local axes (N, Vy, Vz, Tx, My, Mz) or in global axes (Fx, Fy, Fz, Mx, My, Mz). For **each of the six components** in
those axes it also finds the **critical** load combinations: the most positive (Max +) and the most negative (Min −)
value, per member end and over all reported ends. Both the full table and the critical table can be exported for
connection design.

Menu: **N. Connection Load Extractor**. The addon does not edit the model. The only thing it changes is the
selection, and only when you press "Select in model".

## Workflow & Logic

### 1. Selection (GatherSelectedLineIds, BuildTargetEnds)
- Select the beams / ribs in AxisVM, as lines or members (a member counts as all of its lines). Optionally also
  select the nodes you are interested in.
- Only `ltBeam` / `ltRib` lines are used. Other selected lines are listed in Checks.
- **Nodes decide which member ends are reported.** A beam has two ends, and only the ends sitting on a selected node
  are reported. If no node is selected, both ends of every selected line are reported. Selected nodes that are not an
  end of any selected beam / rib are listed in Checks and ignored.
- For each line, `StartNode` / `EndNode` are read; these follow local x (see EndPinAssignAddon.md).

### 2. Dialog
- Load combinations (checklist, all ticked by default), and Linear or Non-linear static results.
- **Coordinate system**: Local or Global. It is used both for the output and for the critical search; no component
  is chosen, because all six are searched.

### 3. Local axes (ResolveFrames)
- Lines have no transformation matrix of their own, so the axes come from `IAxisVMMember.GetTrMatrix` of the member
  the line belongs to (`IAxisVMLine.MemberID`).
- The COM docs do not say whether the local axes are the **rows** or the **columns** of `RMatrix3x3`. The addon decides
  from the data: for a line not parallel to a global axis, only one reading has its x axis along the line. The majority
  over all selected lines wins, and the Checks tab says which reading was used and how many lines decided it. If every
  line is parallel to a global axis, nothing can decide it and rows are assumed (flagged ℹ).
- Every frame is checked: unit axes, mutually perpendicular, right-handed, and x parallel to start → end. A line that
  fails is excluded, never silently mis-rotated.
- A line running against its member's x gets the frame turned 180° about z (x and y reversed).

### 4. Forces (ReadAllRows, ReadLineEnds)
- `IAxisVMForces.GetLineForcesByLoadCombinationId(line, combination, loadLevel 0, analysisType)` is called once per
  line and combination. Non-linear results retry with load level 1 if level 0 returns nothing.
- The section with the smallest PosX is the start node and the one with the largest PosX is the end node. If those
  sections are not at x = 0 and x = L, it is flagged.
- A combination that returns no results (not calculated) is listed in Checks.

### 5. Sign: internal force → force on node (ConnectionLoadMath.RawToNodeLocal)
AxisVM gives the internal force S on the cut face whose outward normal is +x. So:
- at the **start** node (x = 0), the member pushes the node with **+S**;
- at the **end** node (x = L), the member pushes the node with **−S**.

The factors are stored per component (`StartFactor`, `EndFactor` in `Core/ConnectionLoadMath.cs`). If calibration
(below) shows that AxisVM's sign convention for a shear or moment component differs, flip that one entry in both
arrays. Nothing else needs to change.

### 6. Transform (ConnectionLoadMath.LocalToGlobal)
Forces and moments are both true vectors, so each triple is rotated with the same frame:
`F_global = N·ex + Vy·ey + Vz·ez`, `M_global = Tx·ex + My·ey + Mz·ez`.

### 7. Critical (FindCriticalRows)
- For each of the six components (in the chosen axes) and each member end, every combination is evaluated:
  **Max +** is the combination with the largest value and **Min −** the one with the smallest.
- The same search is then run over all reported ends ("All ends").
- Each critical row carries the **full concurrent vector** of that combination, not just the critical component.
- If the largest value is not positive (or the smallest not negative), the row is still shown, greyed and noted
  "no positive / negative value". On a tie, the first combination in model order wins.

### 8. Result window
- **Summary** tab: Max + / Min − over all reported ends for each of the six components (12 rows), with node, line,
  end, combination name, the critical value and the full concurrent vector (the governing component in bold).
- **One tab per component** (Fx … Mz, or N … Mz): Max + / Min − per member end, plus the "All ends" rows (orange).
- **All** tab: every member end × combination. Rows that are critical (any component) for their end are light yellow;
  the overall governing rows are orange.
- **Export critical (CSV)** writes the critical rows of all six components, with a Component column.
- **Checks** tab: all checks below. The tab title shows the number of ⚠.
- **Export all (CSV)** / **Export critical (CSV)**: `;`-separated, `.` decimal, 3 decimals, a `sep=;` first line and
  a UTF-8 BOM, so Excel opens the file correctly in a Dutch locale.
- **Copy tab (TSV)**: copies the visible tab to the clipboard for pasting into Excel.
- **Select in model**: replaces the AxisVM selection with the node and line of the highlighted grid rows (or every
  target node and line if no row is highlighted), so the result can be checked in the model.

## Checks
### Automated (no AxisVM needed)
`Checks/ConnectionLoadMathChecks.csproj` compiles `Core/ConnectionLoadMath.cs` by link and tests it:
- transform round trip;
- hand-computed rotations (beam along X / Y / 45°, column);
- |F| and |M| preserved;
- matrix rows vs columns, left-handed and skewed frames detected;
- sign table;
- Max + / Min − search (mixed values, all one sign, ties, single value, empty list, NaN);
- CSV / TSV text and culture-independent decimals.

```
MSBuild Checks\ConnectionLoadMathChecks.csproj /p:Configuration=Debug
Checks\bin\Debug\ConnectionLoadMathChecks.exe      (exit code 0 = all passed)
```

### At run time (Checks tab)
1. Matrix layout (rows / columns) and how many lines decided it.
2. Frames orthonormal, right-handed and along the line, plus any line that was flipped or excluded.
3. First / last result section at x = 0 / x = L.
4. **Joint equilibrium**: at every target node that has no support and where every connected line is a reported end,
   Σ(global force on node) must be 0 for every combination. This tests reading, sign and transform together. A
   non-zero residual is expected only where a nodal load or a surface acts on the node.
5. **Cross-check with AxisVM's node reader** (`GetLineForcesByLoadCombinationIdConnectedToNode`, first combination):
   each component is reported as `same` / `negated` / `DIFFERENT`, separately for start ends and end ends. `DIFFERENT`
   means the wrong section was picked. The same/negated pattern shows the convention AxisVM's own node table uses.

## Verification in AxisVM (do this before removing "")
1. **Calibration (sign table)**: a cantilever along global X, fixed at node 1, with one combination per tip load:
   +Fx, +Fy, +Fz, +Mx, +My, +Mz. Select the beam and node 1 and run with output Global. The force on node 1 must equal
   **−(support reaction)** in AxisVM's reaction table, for all six components. Repeat with the beam drawn in the
   reverse direction (now node 1 is the end node). If a component disagrees, flip that entry in `StartFactor` /
   `EndFactor`.
2. **Rotated members**: the same cantilever at 30° in plan, and as a vertical column, plus a rib. Global output must
   match the support reactions, and the Checks tab must decide the matrix layout (the 30° beam is what decides it).
3. **Frame test**: a 2-storey portal frame with several combinations. Select all members and no node. Joint equilibrium
   must be ✔ at the internal joints. Pick two critical rows, switch AxisVM to that combination, and compare the beam
   end forces table with the Local output.
4. **Visual**: Select in model highlights the expected nodes / lines. Exported CSVs open in Excel with correct columns
   and decimals.

## File Structure
- `Addons/ConnectionLoadExtractorAddon.cs`: AxisVM interface registration and icon.
- `UI/ConnectionLoadExtractorForm.cs`: input dialog (combinations, analysis type, output system, critical direction).
- `UI/ConnectionLoadResultForm.cs`: Critical / All / Checks tabs, CSV / clipboard export, Select in model.
- `Functions/ConnectionLoadExtractorFunction.cs`: selection, local axes, force reading, critical search, run-time checks.
- `Core/ConnectionLoadMath.cs`: pure maths (frames, sign table, transforms, critical search, CSV). No COM.
- `../Checks/ConnectionLoadMathChecks.*`: console checks for the maths.

## Notes / limitations
- Load combinations only. Load cases, envelopes and AxisVM's automatic critical combinations are not included.
- Rib forces are at the rib axis; eccentricity is not moved to the node.
- Seismic components use `scstUsual` in the cross-check; the main reader uses AxisVM's default.
- AxisVM has no COM call to switch the displayed result combination, so switch to the reported combination in
  AxisVM by hand to compare.
- The results window is modeless (owned by the AxisVM main form), so AxisVM stays usable while it is open. "Select in
  model" first clears the node, line and member selection, then selects only the chosen items. A new run closes the
  previous result window. Close / Esc call `Close()` directly, because a button's `DialogResult` only closes modal forms.
- The toolbar button is on the **Static** tab (`Addon_ButtonPlaceToolbarId` = 4; 0 geometry, 1 elements, 2 loads,
  3 mesh, 4 static).
