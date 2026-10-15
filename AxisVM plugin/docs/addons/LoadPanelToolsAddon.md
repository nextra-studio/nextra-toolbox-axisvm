# Load Panel Tools Addon

[Illustrated user guide](../user-guide/panel-tools.md)

## Overview
Four load panel operations that all work on **everything currently selected**, not one object at a time.
The whole run — whichever operation — sits inside a single `BeginUpdate` / `EndUpdate` pair (`try/finally`,
followed by `Refresh`), so AxisVM redraws once and one summary dialog reports the outcome.

| Operation | Select | Does |
|---|---|---|
| **Load Panel → Domain** | load panels | creates a domain on each panel's contour |
| **Domain → Load Panel** | domains | `LoadPanels.AddFromDomain` per domain |
| **Split Load Panel by lines** | load panels **and** lines | cuts each panel into the pieces the lines divide it into |
| **Merge Load Panels** | load panels | joins coplanar panels that share edge lines into one panel per group |

A checkbox decides whether the source object is deleted once its counterpart exists. Splitting and merging
always replace the originals — the checkbox is disabled and forced on for those modes.

## The UID discipline
This is the thing to keep in mind when editing the operations. Three AxisVM operations renumber collections:

- deleting a load panel renumbers **load panels**; deleting a domain renumbers **domains**
- `IAxisVMLine.SplitByNode` renumbers **lines**, and both halves get **new UIDs** (the original UID is gone).
  It returns the halves' indices *after* renumbering.
- node ids are safe — splitting only *adds* nodes

So every id that has to survive one of those is held as a **UID** and re-resolved through
`get_IndexOfUID` immediately before use.

## 1. Load Panel → Domain
- The selection is read with a `get_Selected(i)` loop over `1..Count` — `IAxisVMLoadPanels` has no
  `GetSelectedItemIds`.
- Per panel: `GetContourLineIDs` → `Domains.Add(contourLines, ref attr)`.
- Panels whose contour is `lpctUserDefined` (a free polygon with no lines behind it) have no contour lines,
  and are reported and skipped.
- **Placeholder attributes**, because a load panel carries no thickness or material of its own:
  200 mm, `stShell`, and the first **concrete** material in the model (falling back to the first material
  of any type). If the model has no material at all, nothing is created and the summary says so.
- `Domains.Add` turns beams on the contour into ribs — that is AxisVM's own behaviour.
- With **delete the original**, the panel's uniform/linear area loads are moved onto the new domain
  (`AddDomainLinear`, same plane points and values). Other panel load kinds are counted and reported.

## 2. Domain → Load Panel
- `Domains.GetSelectedItemIds` → `LoadPanels.AddFromDomain(domainId)` per domain. Adding panels does not
  renumber domains, so the indices hold for the whole loop.
- `AddFromDomain` hands back the **existing** panel when one already covers the domain. That is recognised
  (the returned UID was already present), reported, and the domain is not deleted.
- With **delete the original**, the converted domains are removed with `Domains.Delete`, highest index
  first. `IAxisVMDomains` has no `SelectAll`. The new panel follows the contour lines and **survives** the
  domain deletion (verified live). Loads on the deleted domains go with them; their count is reported.

## 3. Split Load Panel by lines

### Contour ring
`BuildRing` walks `GetContourLineIDs` into an ordered ring where `Lines[i]` runs from `Nodes[i]` to
`Nodes[i+1]` and the last edge closes back onto `Nodes[0]`. The walk chains shared end nodes rather than
trusting the returned order, and fails cleanly if the contour is not a single closed loop.

### Validating the cut — before anything is changed
Each end of the cutting line is located on the ring: on a corner, or strictly part-way along an edge
(within 1 mm). The cut is rejected, with a reason in the summary, when:
- the line is itself one of the contour edges,
- an end does not lie on the contour,
- the two ends share an edge or are neighbouring/identical corners (the line runs along the contour),
- the line's midpoint is outside the panel (point-in-polygon in the contour's own plane, which guards concave
  panels).

Only after it passes are the edges split (`SplitByNode`), so a rejected line leaves the model untouched.

### Building the pieces
The ring is walked forward from corner A to corner B for one piece and from B back to A for the other; the
cutting line closes both. Each piece is created with `AddFromLineIDs` and inherits:
- the original's `Auto` flag (when `Auto` is true, AxisVM works the edges out itself and nothing more is set),
- the original's **load-carrying edges** (`GetLines`), matched by UID through an ancestry map so the halves
  of a split loaded edge still count,
- plus the **cutting line itself** if it is an `ltBeam` or `ltRib` — a real member splitting a panel picks up
  load. A plain geometry line does not.

Only `SetLines` drives AxisVM's load distribution. The per-edge `SelectedEdge` flags do not (verified by
analysis: contradicting flags give identical beam forces), and `SetLines` clears them, so they are not set.

### Loads
Deleting a panel deletes its loads. Before the first cut, the panel's uniform/linear area loads are read and
then re-added to every final piece with `AddLoadPanelLinear`. See **Panel load records** below for how they
are read. A **linear** load is re-expressed per piece: AxisVM rejects (−100027) a linear load whose three
reference points are not on the panel, so the points move to three corners of the piece and the values there
are taken from the original plane field. The load field itself is unchanged.

### Several lines through one panel
A worklist drives it: cut a panel, queue its two pieces, then try the remaining lines against those. A panel
crossed by two lines ends up as three or four pieces. Each cutting line is consumed once per original panel.

## 4. Merge Load Panels
- Selected panels that **share contour lines** form a group (union-find over line UIDs); each group becomes one
  panel. Panels touching only at a corner, or along edges made of *different* lines, are not connected.
- The outline is every edge used by exactly one panel of the group; shared edges fall inside. It is walked with
  the same ring builder as split and must be **one closed loop** — a group enclosing a hole is refused.
- A group is refused, with a reason, when its panels are not coplanar (1 mm), mix `Auto` on and off, carry a
  load kind that cannot be read back, or carry **different loads**. Loads are compared as fields: same case,
  component and kind, and each load evaluated at the other's reference points gives its values — so pieces cut
  from one sloped load still merge.
- The merged panel keeps the loaded outline edges (`SetLines`), the `Auto` flag, and gets the load set once.
  Shared edges that carried load (e.g. a beam that was used as a cut) stop receiving load; the summary says
  how many. The cut lines themselves stay in the model.
- The merged panel is created first, then the originals are deleted (`DeleteSelected`), then the merged panel
  is found again by UID because deletion renumbers panels.

## Panel load records
There is no typed getter for panel loads. They report `LoadType −1` with the panel index as `ElementId`, and
`GetLoad` returns 120 bytes: 5 × int32 (case, panel, distribution, load distribution, component), 4 bytes
padding, 3 load doubles, then the plane points x1..x3, y1..y3, z1..z3. The three load doubles are **not**
P1..P3 for a constant load: AxisVM stores it as a vector, the value in the X, Y or Z slot of its component.
Only an `ldtLinear` load stores P1, P2, P3 there. (Reading them as P1..P3 turns a constant −5 into 0, 0, −5,
and writing that back stores a zero load.) Records that do not match this shape are counted and reported.

## File Structure
- `Addons/LoadPanelToolsAddon.cs` — COM interface registration, toolbar icon, menu visibility.
- `UI/LoadPanelToolsForm.cs` — operation radio group, per-operation hints, delete-original checkbox.
- `Functions/LoadPanelToolsLogic.cs` — entry point: selection, dialog, `BeginUpdate` batch, summary.
- `Functions/LoadPanelOps.cs` — the four operations, UI-free.
- `LoadPanelToolsAddon.csproj` — compiles only its own sources plus `Properties/AssemblyInfo.cs`.

## Live checks
`Checks/LoadPanelToolsLiveChecks` runs `LoadPanelOps.cs` against a real AxisVM: 31 cases, 101 checks — split
(one/two lines, diagonal, corner to edge, pre-split edges, wall panel, beam cutter, Auto panel, linear load,
several panels at once, crossing lines, rejections), panel ↔ domain (keep/delete, load transfer, several at
once, user-defined polygon panel), and merge (round trip, three pieces, two groups, L shape, refusals for
non-adjacent / different loads / not coplanar / mixed Auto, loaded interior beam). Loads are checked by value,
not only by count. It builds its geometry at x ≥ 100 m and deletes everything it made.

It only runs against a **scratch** instance: start a separate AxisVM, save its empty model as
`LoadPanelTest*.axs` with at least one material and one cross-section (e.g. one beam far away) and one
user-defined polygon panel (`LoadPanels.AddFromPolygon`), and pass its process id:

```
MSBuild Checks\LoadPanelToolsLiveChecks.csproj /p:Configuration=Debug
set LP_PID=<pid of the scratch AxisVM>
Checks\bin\Debug\LoadPanelToolsLiveChecks.exe      (exit code 0 = all passed)
```

The harness attaches through the NextraAxisVM add-on's `!NextraAxisVM.Host.<pid>` entry in the Running Object
Table (AxisVM itself is not registered there), and refuses to run if the open file is not a
`LoadPanelTest*.axs` scratch file.

## Notes / limitations
- **No intersection maths.** A cutting line's ends must be *existing nodes* on the contour. A line that
  merely crosses the contour with no node there is skipped rather than intersected.
- Two cutting lines that cross each other inside a panel are not intersected either: the second one is skipped
  and the summary reports it as a selected line that did not cut anything.
- A cut must be **one** line from contour to contour. A T drawn in AxisVM splits the first line where the
  second meets it, and those two halves are refused as cuts (each has an end inside the panel).
- Interop signature gotcha: `GetContourLineIDs` is `ref`, `GetLines` is `out`. They are inconsistent in the
  interop assembly, so check before adding calls.
