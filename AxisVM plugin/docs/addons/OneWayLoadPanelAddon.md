# One-Way Load Panel Addon

## Overview
Menu / toolbar entry **"N. One-Way Load Panel"** (`OneWayLoadPanelAddon.dll`). One command for both
ways of making one-way load panels; what it does depends on the selection:

| Selection | Result |
|---|---|
| Load panels (lines may be selected too) | Each panel is rebuilt as one-way panel(s) on the members under its outline. Its loads are moved onto the new panels. |
| Lines only | Each closed boundary in the selection gets new one-way panel(s), optionally with an area load. |

A window selection picks a panel together with its edge lines, so selected panels take priority.
This add-on replaces the former "N. Lines -> One-Way Panel" and "N. Convert Load Panel -> One-Way"
add-ons. Remove `ConvertLoadPanelOneWayAddon.dll` from `C:\AXISVM X8\AddOns` when updating.

## Dialog
- The header says what the run will do with the current selection.
- **Span direction**: Auto - shorter span, Auto - longer span, Global X, Global Y, or an angle
  from global X. Both Auto options work per panel from its own edges (not global X/Y), so they
  suit rotated bays; "longer" is "shorter" turned 90° (`SpanningDirection` 0 / 3). The supports
  (receiving lines) are the sides across the span.
- **Edge strips**: split off a strip of the given width (the joist spacing) along each edge beam.
  The strip loads the edge beam, and the centre spans between the supports.
- **Load**: when creating from lines, an optional area load (load case, kN/m², positive downward).
  When converting, the existing loads are carried over (see below).
- The preview redraws with every change. Settings are remembered for the AxisVM session.
- After the run, a report lists every note in a scrollable, copyable box.

## Converting a load panel (`OneWayLoadPanelLogic.ConvertPanel`)
1. **Outline onto members** (`PanelBoundaryResolver`). The outline comes from the panel's contour
   lines, or from its contour polygon when it was drawn as a free polygon (such a panel has no
   contour lines). Each outline edge is covered by the existing lines lying on it, ranked
   beam/rib/truss, then domain edge, then plain line. A plain line is only created to bridge a
   stretch no line covers, and this is reported.
   *Earlier versions drew a new line along every polygon edge. That put a plain line on top of each
   edge beam, the panel sent its load to those plain lines, and the load never reached the structure.*
2. **Validate** before touching the model. On failure the panel is left unchanged and any bridging
   lines are removed.
3. **Capture and delete its loads** (`PanelLoadCarrier`): whole-panel area loads, polygon area loads
   and point loads. Line and polyline loads are counted and reported as not carried over.
4. **Delete the panel**, required because `AddFromLineIDs` on the same contour is otherwise
   rejected with `lopeSameLoadPanelExists`.
5. **Generate** the one-way panel(s) with `OneWayPanelBuilder`. If nothing can be built, a plain
   (two-way) panel is restored so the user is never left with a hole.
6. **Re-apply the loads**. A whole-panel load goes onto every new panel at the same intensity. A
   polygon load is re-added once with its own outline, and AxisVM attaches it to every panel it
   covers. A point load goes to the panel containing it.
7. **Check receivers**: any receiving line without a beam/rib/truss element is reported, because
   AxisVM drops load sent to it.

### Load records
AxisVM has no typed getter for load panel loads, so `IAxisVMLoads.GetLoad` returns raw bytes. The
layouts were measured on AxisVM X8 (type library 18.100):

| Load | LoadType | Bytes | Layout |
|---|---|---|---|
| Whole-panel area (`AddLoadPanelLinear`) | -1 | 120 | int case@0, panel@4, dist@8, ldist@12, component@16; double P1@24, P2@32, P3@40; x1..z3@48 |
| Polygon area (`AddLoadPanelPolyArea`) | 0x21 | 120 | int case@0, dist@4, ldist@8, component@12; double P1@16, P2@24, P3@32; x1..z3@40; int window@112. Its panels come from `GetDomains_Surfaces_LoadPanels` |
| Point (`AddLoadPanelConcentrated`) | -1 | 88 | int case@0, panel@4; double Fx..Mz@8; x,y,z@56; int system@80, reference@84 |

For a constant load AxisVM stores the intensity in the slot of its axis (P3 for a Z load).
`GetDomains_Surfaces_LoadPanels` returns an error for whole-panel and point loads; they are
matched by `ElementId` and the panel index inside the record.

## Creating from lines (`OneWayLoadPanelLogic.CreateFromLines`)
- Straight, horizontal, closed boundaries. `GroupByConnectivity` splits the selection into
  independent boundaries (lines sharing end nodes, transitively). Adjacent bays share beams and
  must be selected separately; branches are rejected before splitting.
- If a plain line and a member lie on the same two nodes (a duplicate, often left by an earlier
  run), only the member is used.
- Groups are remembered by line UID, because the edge-strip split renumbers line indexes.

## Geometry (`OneWayPanelBuilder`, `EdgeChainBuilder`, shared with Rhino import)
- **Edge chains**: collinear segments are chained into one logical side when the bend between them
  is within the straight-side tolerance (default 15°), so a side split into several beams is one side.
- **Classification**: sides across the span are supports; sides along it are edge beams.
- **Receivers and the tolerance**: a panel's load direction is the span (single and centre
  panels) or the direction toward the edge beam (edge strips). A boundary side or an interior
  beam/rib/truss receives load when it runs at 90° ± tolerance to that direction (tolerance 10 =
  80° to 100°). Interior members must lie in the panel plane and pass through its inside; plain
  lines never receive. AxisVM shares the load between all receivers by tributary width and loads
  only the part of a member inside the panel, so interior beams are not split (measured: 6 x 4 m
  bay, mid-span beam, 6 kN/m² gives 36 / 72 / 36 kN; with 300 mm strips the mid beam takes
  2 x 5.4 x q). The same tolerance also decides how far a side may bend and still be one side.
- **Rhino import**: uses this builder, so interior beams receive load there too. The tolerance is
  the panel's `Ax_SpanTolerance` (degrees, AxDomain > Load panel > Angle tolerance); blank = 5.
- **Single panel**: one panel over the boundary, receiving lines = the supports (`SetLines`, plus
  `SelectedEdge` flags in contour order).
- **Edge strips**: each support is split at the strip width from both corners (only the segment
  straddling the point), plain inset lines join the split points, and three panels are built: two
  strips feeding the edge beams and a centre panel feeding the supports. This needs exactly 2
  supports + 2 edge beams; otherwise a single panel is made, with a note.
- Failure part-way through removes temporary panels and inset lines; valid beam splits remain.

## Verification (2026-10-15)
Run against a live AxisVM X8 model (6 x 4 m bays on 300x300 beams and columns), with the
production source compiled into an out-of-process harness:
- Line-based panel converted with 300 mm strips: area load -5 kN/m² and point load -10 kN carried,
  receiving lines all beams.
- Panel drawn as a polygon over beams, with a -2 kN/m² polygon load: no duplicate lines created,
  polygon load re-attached to all three new panels.
- Lines selection including a plain duplicate over a beam, plus 1.5 kN/m²: duplicate ignored.
- Linear analysis: support reactions 214 kN = 5·24 + 10 + 2·24 + 1.5·24.

## Validation
- Numeric inputs accept the current locale's decimal separator and an invariant decimal point,
  without thousands separators. NaN and infinity are rejected.
- Tolerance must be at least 0 and below 45°; strip width must exceed 0.001 mm.
- Open or branched boundaries, zero-length segments, arcs and non-horizontal boundaries are rejected
  before the model changes.
- A panel must have receiving lines. `SetLines` must report the expected count; otherwise the
  unconfigured panel is deleted and the error is reported.

## File Structure
- `Addons/OneWayLoadPanelAddon.cs`: registration shell (menu text, icon).
- `Functions/OneWayLoadPanelLogic.cs`: selection, both workflows, receiver check, report.
- `Functions/PanelBoundaryResolver.cs`: panel outline onto existing members.
- `Functions/PanelLoadCarrier.cs`: capture / delete / re-apply panel loads.
- `Functions/OneWayPanelBuilder.cs`, `Functions/EdgeChainBuilder.cs`: shared geometry engine.
- `UI/OneWayLoadPanelForm.cs`: dialog with preview. `UI/OneWayPanelReportForm.cs`: run report.

## Regression checks
`Checks/OneWayPanelChecks.csproj` compiles the builder and edge-chain code against an in-memory
AxisVM API double (split UID deletion, index renumbering, contour validation, failures during
splitting, panel creation and configuration). It does not cover COM behaviour or load transfer;
check those on a disposable AxisVM model, as in the verification above.
