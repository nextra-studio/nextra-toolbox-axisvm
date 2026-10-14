# One Way Load Panel Addon

## Overview
The One Way Load Panel Addon automates the generation and configuration of one-way load distribution panels in AxisVM. It is designed to quickly assign one-way spanning logic over domains and automatically split panels to accurately distribute loads to edge beams versus main spanning beams based on user-defined geometries and tributary spacings.

## Workflow & Logic

### 0. Edge chain reconstruction (`Functions/EdgeChainBuilder.cs`)
- Real bays often have one physically-straight side split into several collinear beam segments
  (columns dropped in, secondary beams framing, meshing). Before anything else, each boundary
  group's segments are chained into **logical edges**: two segments are joined into one side only
  when the bend where they meet is within the **Angle Tolerance** (default 15°, so 90° corners stay
  as edge boundaries and a rectangular bay comes back as exactly four sides). This is the same
  "smaller bend angle vs tolerance" test as `EndPinAssignFunction.IsContinuousStrict`.
- Every downstream step (span direction, support/edge classification, edge-strip splitting) then
  works on these polyline sides. A side that is a single line behaves exactly as before; a side
  made of three beams has its offset points placed on whichever sub-segment straddles the spacing,
  and only that sub-segment is split. Strip and centre contours are built from segment *lists*.
- If the reconstruction does not yield exactly 2 support + 2 edge sides (L-shaped panel, genuine
  5+ sided bay, obtuse corner merged away), the with-spacing path is skipped and a single one-way
  panel is generated instead, with a note in the summary.

### 1. Selection & Input Gathering
- The user provides straight lines forming a horizontal closed boundary. Sloped panels and arcs
  are rejected because spanning directions are calculated in the global XY plane.
  Sides may be subdivided into multiple collinear segments — see section 0.
- **Multiple boundaries in one run**: the selection does not have to be a single bay. `GroupByConnectivity`
  splits the selected lines into independent boundaries — two lines belong to the same boundary when they
  share an end node, transitively — and each group is converted into its own panel set. Only disconnected closed loops are independent groups. Adjacent bays sharing nodes or beams
  are one connected graph and must be selected separately; branches are rejected before splitting.
- The whole run sits inside a single `BeginUpdate` / `EndUpdate` pair (`try/finally`), so AxisVM redraws once.
- Each group is remembered by **line UID** and re-resolved to indices immediately before it is built. This
  matters because the edge-spacing path splits the main beams, which renumbers every line index after the
  split point — indices captured before the first group runs are stale by the time the second one does.
  Node ids are unaffected: splitting only adds nodes, it never removes them.
- The UI prompts the user for configuration parameters:
  - **Span Direction**: Direction 1 or 2 (defining which way the main slab/deck spans).
  - **Spacing / Splitting Strategy**: User inputs a spacing value (e.g., 300mm for edge beam tributary width).
  - **No Spacing Checkbox**: If checked, the spacing logic is bypassed, and a single one-way panel is generated over the entire domain.

### 2. Basic Generation (No Spacing)
- If the user selects "No Spacing", the script retrieves the geometric domain defined by the 4 selected boundary lines.
- It calculates the shortest span between the opposing parallel (or near-parallel) boundary lines.
- It generates a single `IAxisVMLoadPanel` covering the domain and explicitly sets its load distribution attribute to "One-Way", assigning the span direction to match the calculated shortest span or user override.

### 3. Advanced Splitting (With Spacing)
- If a spacing is provided (e.g., 300mm for edge beam tributary), the addon performs a geometric split of the domain to ensure realistic load paths.
- **Edge Strips**: It offsets the boundary lines parallel to the edge beams inward by the spacing value (e.g., 300mm). This creates two narrow rectangular strips along the edges.
- **Center Strip**: The remaining area between the edge strips becomes the center panel.
- **Panel Assignment**: 
  - The script generates separate `IAxisVMLoadPanel` objects for each calculated strip.
  - The **edge strips** are configured to distribute load directly to the edge beams.
  - The **center strip** is configured as a one-way panel spanning across the main beams, ignoring the edge beams.

### 4. Application
- The model enters an `Update` state to ensure bulk generation is fast.
- The newly generated domains and load panels are committed to the AxisVM model.
- Load panel distribution parameters are strictly enforced via the AxisVM COM API.

### 5. Reporting
- One summary dialog for the whole run: panels created, how many of the selected boundary groups
  succeeded, and a note per boundary that had to be downgraded or that AxisVM rejected. A message box per
  boundary would be unusable once several bays are selected at once.
- When the edge-spacing path fails part-way through (after it has already split beams), each successful split replaces the deleted parent UID with both child UIDs in the logical edge.
  The fallback resolves these surviving child UIDs, retaining the full boundary. Temporary panels and
  inset lines are removed; valid beam splits remain in the model.

## File Structure
- `Addons/OneWayLoadPanelAddon.cs`: AxisVM COM registration + icon for "N. Lines -> One-Way Panel".
- `UI/OneWayLoadPanelForm.cs`: span direction / spacing / tolerance dialog.
- `Functions/OneWayLoadPanelLogic.cs`: selection + dialog + summary for the line-based workflow (`Execute`).
- `Functions/OneWayPanelBuilder.cs`: **shared** geometry engine — edge classification, panel
  creation, the edge-strip split (`GeneratePanels`, `OneWayPanelOptions`). Compiled into the line-based addon and Rhino import.
- `Functions/EdgeChainBuilder.cs`: **shared** — reconstructs subdivided boundary sides into logical
  polyline edges.

## Validation and failure handling (2026-10-14 refactor)
- Numeric inputs accept the current locale's decimal separator and an invariant decimal point,
  without thousands separators. This avoids interpreting the default `15.0` as `150` under Dutch
  regional settings. NaN and infinity are rejected.
- Angle tolerance must be at least 0 and below 45 degrees; spacing must exceed 0.001 mm.
- Duplicate lines, open or branched boundaries, zero-length segments, arcs and nonhorizontal
  boundaries are rejected before generation changes the model.
- A panel must have receiving lines. `SetLines` must report the expected count; configuration
  errors delete the unconfigured panel and are surfaced. SAFEARRAY contour reads accept either
  zero-based or one-based arrays; AxisVM edge indices remain one-based.
- Cleanup is centralized and runs on both normal failure returns and exceptions. Cleanup failures
  are reported rather than silently ignored. This is not a full model transaction: successful beam
  splits remain after an unsuccessful spaced attempt.

## Regression checks
`Checks/OneWayPanelChecks.csproj` compiles the production builder and edge-chain code against an
in-memory AxisVM API double. It simulates deletion of split parent UIDs, line-index renumbering,
closed-contour validation, and failures during splitting, panel creation and configuration.
These checks do not establish actual COM behavior or verify solved load distribution in AxisVM.
Build the add-on against the installed AxisVM assemblies, then check a disposable AxisVM model
before installing it for production use.
