# Load Panel Tools Addon

## Overview
Three load panel operations that all work on **everything currently selected**, not one object at a time.
The whole run — whichever operation — sits inside a single `BeginUpdate` / `EndUpdate` pair (`try/finally`,
followed by `Refresh`), so AxisVM redraws once and one summary dialog reports the outcome.

| Operation | Select | Does |
|---|---|---|
| **Load Panel → Domain** | load panels | creates a domain on each panel's contour |
| **Domain → Load Panel** | domains | `LoadPanels.AddFromDomain` per domain |
| **Split Load Panel by lines** | load panels **and** lines | cuts each panel into the pieces the lines divide it into |

A checkbox decides whether the source object is deleted once its counterpart exists. Splitting always
replaces the original — the checkbox is disabled and forced on for that mode.

## The UID discipline
This is the thing to keep in mind when editing this file. Three AxisVM operations renumber collections:

- deleting a load panel renumbers **load panels**
- `IAxisVMLine.SplitByNode` renumbers **lines**
- node ids are safe — splitting only *adds* nodes

So every id that has to survive one of those is held as a **UID** and re-resolved through
`get_IndexOfUID` immediately before use. In particular, `TrySplitPanel` re-resolves the cutting line's
index *after* `EnsureContourCorner`, because that call may have split a contour edge underneath it.

## 1. Load Panel → Domain
- The selection is read with a `get_Selected(i)` loop over `1..Count` — `IAxisVMLoadPanels` has no
  `GetSelectedItemIds`.
- Per panel: `GetContourLineIDs` → `Domains.Add(contourLines, ref attr)`.
- Panels whose contour is `lpctUserDefined` (a free polygon with no lines behind it) make
  `GetContourLineIDs` fail, and are reported and skipped — there is nothing to hand to `Domains.Add`.
- **Placeholder attributes**, because a load panel carries no thickness or material of its own:
  200 mm, `stShell`, and the first **concrete** material in the model (falling back to the first material
  of any type). The dialog states exactly what will be used; adjust the domains in AxisVM afterwards.
  If the model has no material at all, nothing is created and the summary says so.
- Creating domains does not renumber load panels, so the loop runs on stable indices; the deletions all
  happen afterwards in one `DeleteSelected`.

## 2. Domain → Load Panel
- `Domains.GetSelectedItemIds` → `LoadPanels.AddFromDomain(domainId)` per domain. Adding panels does not
  renumber domains, so the indices hold for the whole loop.
- **Deleting the source domain is riskier than it looks**: a panel made by `AddFromDomain` has an
  *associated* contour (`lpctAssociated`), so removing the domain can remove the panel with it. Rather than
  guess, the code records the new panels' UIDs, deletes the domains, then counts how many of those UIDs
  still resolve, and reports the difference. The dialog warns about this before the run.

## 3. Split Load Panel by lines

### Contour ring
`BuildContourRing` walks `GetContourLineIDs` into an ordered ring where `ringLines[i]` runs from
`ringNodes[i]` to `ringNodes[i+1]` and the last edge closes back onto `ringNodes[0]`. The walk is done by
chaining shared end nodes rather than trusting the returned order, and it fails cleanly if the contour is
not a single closed loop.

### Placing the cut
For a line to cut a panel, both of its end nodes must be **corners of the contour ring**.
`EnsureContourCorner` handles the common case where an end sits part-way along an edge: it finds the edge
the node lies on (perpendicular distance ≤ 0.1 mm, strictly between the ends), splits it there with
`SplitByNode`, and rebuilds the ring.

A cut is rejected — with a reason in the summary — when:
- the line is itself one of the contour edges,
- an end does not lie on the contour at all,
- both ends land on the same corner,
- the two corners are neighbours (the line runs alongside an edge and encloses no area).

### Building the pieces
The ring is walked forward from corner A to corner B for one piece and from B back to A for the other; the
cutting line closes both. Each piece is created with `AddFromLineIDs` and inherits:
- the original's `Auto` flag (when `Auto` is true, AxisVM works the edges out itself and nothing more is set),
- the original's **load-carrying edges** (`GetLines`), matched by UID so a contour edge that got split still
  counts — `uidAncestor` maps each half back to the edge it came from,
- plus the **cutting line itself** if it is an `ltBeam` or `ltRib`, on the reasoning that a real member
  splitting a panel should pick up load. A plain geometry line does not.

Both `SetLines` and the per-edge `SelectedEdge` flags are written, the same pairing the One-Way Load Panel
addon uses.

### Several lines through one panel
A worklist drives it: cut a panel, push its two pieces, then try the remaining lines against those. A panel
crossed by two lines therefore ends up as three or four pieces. Each cutting line is consumed once per
original panel. A piece nothing cuts any further is a final result.

## File Structure
- `Addons/LoadPanelToolsAddon.cs` — COM interface registration, toolbar icon, menu visibility.
- `UI/LoadPanelToolsForm.cs` — operation radio group, per-operation hints, delete-original checkbox.
- `Functions/LoadPanelToolsLogic.cs` — all three operations.
- `LoadPanelToolsAddon.csproj` — compiles only its own three sources plus `Properties/AssemblyInfo.cs`.

## Notes / limitations
- **No intersection maths.** A cutting line's ends must be *existing nodes* on the contour. A line that
  merely crosses the contour with no node there is skipped rather than intersected — no new nodes are
  invented.
- **A rejected cut can still leave a split edge behind.** `EnsureContourCorner` has to split a contour edge
  before the ring positions (and therefore the adjacency test) are knowable, so a line that turns out not to
  cut the panel may leave that edge divided. The division is geometrically harmless — the node was already
  there — but it is a model change.
- **Concave contours are not tested for containment.** A chord between two non-adjacent corners of a concave
  contour can fall outside the panel; the code relies on `AddFromLineIDs` rejecting the result.
- Interop signature gotcha: `GetContourLineIDs` is `ref`, `GetLines` is `out`. They are inconsistent in the
  interop assembly, so check before adding calls.
