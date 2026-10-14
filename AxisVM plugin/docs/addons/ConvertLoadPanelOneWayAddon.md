# Convert Load Panel → One-Way Addon

## Overview
Menu / toolbar entry **"N. Convert Load Panel -> One-Way"**. Takes the load panels the user has
selected and rebuilds each one as its one-way equivalent — a single one-way panel, or (with an edge
spacing) the two edge strips + centre panel set — and re-applies the uniform area load that was on
the original.

It is its own DLL with its own `ConvertLoadPanelLogic` and `ConvertLoadPanelForm`, but the panel
geometry comes from the shared `OneWayPanelBuilder` / `EdgeChainBuilder` (also compiled into the
line-based addon). Only the input gathering and the load preservation are specific to this addon.

## Workflow (`ConvertLoadPanelLogic.ExecuteConvert`)

1. **Selection** — load panels have no `GetSelectedItemIds`, so the selection is read panel by
   panel (`SelectedPanelUids`). Panels are remembered by UID.
2. The One-Way form is shown (same span direction / edge spacing / no-spacing / angle tolerance).
3. All work runs inside one `BeginUpdate` / `EndUpdate` (+ `Refresh`).
4. Per selected panel:
   - **Contour** — `GetContourLineIDs` first. If the panel is a **user-defined polygon** (no
     contour lines), straight boundary lines are created from its outline via `GetContourPolygon`
     (`TryBuildSegmentsFromContourPolygon`), with a note. Only a panel whose outline cannot be read
     at all is skipped.
   - **Chain reconstruction** — the contour lines are chained into logical edges exactly as in the
     line workflow, so a panel whose side was split into several collinear beams is handled.
   - **Capture loads** — every load in the model is scanned; those of type `ltDomainPolyArea` /
     `ltDomainPolyAssoc` whose target set (via `GetDomains_Surfaces_LoadPanels`) includes this
     panel are captured as plain values (load case, component, distribution type, `P1..P3`).
     Concentrated / linear / polyline / partial panel loads are only counted and reported as
     *"not carried over"*.
   - **Delete the original** — required, otherwise `AddFromLineIDs` on the same contour is rejected
     with `lopeSameLoadPanelExists`. The contour lines themselves are untouched.
   - **Regenerate** — `GeneratePanels` builds the one-way panel(s). If nothing could be built a
     plain load panel is restored so the user is never left with a hole.
   - **Re-apply loads** — for each regenerated panel, its own contour polygon
     (`GetContourPolygon`) is used as the load polygon and each captured load is re-added with
     `AddLoadPanelPolyArea`. The full intensity is applied to every regenerated panel (partial /
     tributary loads are intentionally not modelled).
5. One summary dialog: panels converted, new panels created, loads re-applied, and any skips.

## Notes / limitations
- Only **uniform full-panel area loads** are preserved. This matches the intent: after conversion
  the load simply sits on each new one-way panel at the same intensity and load case.
- `RLoadPanelPolyArea` for `ldtConst` distribution uses only `P1` (per the COM docs); `P2/P3` and
  the reference points are copied through but only matter for linear distributions.
- If the reconstructed contour is not a clean 2-support + 2-edge quad, a single one-way panel is
  produced (with a note), never the 3-panel split.
- Boundary lines created for a user-defined-polygon panel are plain lines and are left in the
  model; assign beam/rib attributes to the support edges if the load must transfer to members.

## File Structure
- `Addons/ConvertLoadPanelToOneWayAddon.cs` — registration shell (icon, menu text).
- `Functions/ConvertLoadPanelLogic.cs` — `ExecuteConvert`, the panel-outline-to-lines fallback,
  and the load capture / re-apply helpers.
- `UI/ConvertLoadPanelForm.cs` — its own dialog (same inputs as the One-Way dialog).
- `Functions/OneWayPanelBuilder.cs`, `Functions/EdgeChainBuilder.cs` — shared geometry, also
  compiled into the line-based addon.
- `ConvertLoadPanelOneWayAddon.csproj` — builds `ConvertLoadPanelOneWayAddon.dll` into
  `C:\AXISVM X8\AddOns`.
