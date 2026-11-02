# Rhino ⇄ AxisVM link — how it works, what is supported, limits

The Rhino plug-in (AxisBridge: AxPoint, AxLine, AxDomain, AxLink, AxExport) and the AxisVM add-on (Rhino Import) exchange one
JSON file. This page explains how elements are matched between the two programs, what each element looks like on both sides,
what an update rebuilds, and what the link cannot do.

## 1. One file, two directions

| | Rhino (AxLink window) | AxisVM (Rhino Import add-on) |
|---|---|---|
| Rhino → AxisVM | **Send to AxisVM** writes the Rhino model into the file | **From Rhino › Load from Rhino** builds or updates the model |
| AxisVM → Rhino | **Load from AxisVM** applies AxisVM's changes (one Undo) | **To Rhino › Send to Rhino** writes what changed in AxisVM |

The file (`*.axlink.json`) has one section per side: `rhino` (the Rhino model) and `axisvm` (what changed in AxisVM, plus
what the last load built). Each side rewrites only its own section. A plain AxExport `.json` also loads, but it is replaced as
a whole on every update (it keeps no record of the last load); use AxLink for updates of only what changed.

## 2. How elements are matched

- **Rhino side:** every object has its Rhino object id (a GUID). The export writes it as `guid` (and `Ax_Guid`).
- **AxisVM side:** AxisVM has no field for an outside id, so the add-on writes small text notes (attachments, saved in the
  `.axs`) on each node, line and domain:

| Attachment | Meaning |
|---|---|
| `RhinoGuid` | The Rhino object(s) the entity stands for. Every piece of a split curve carries its curve's id; a line shared by overlapping curves carries all their ids. |
| `RhinoLink` | Name of the link file whose load created it. |
| `RhinoAdopted` | Drawn in AxisVM and sent to Rhino: the id Rhino gave the new object. |
| `RhinoState` | Geometry, type, section and material as last loaded — what Send compares with. |
| `RhinoExtras` | Supports, releases, springs, links and hinges as last loaded — what Send compares with. |

- **Load panels** cannot carry attachments: the link file remembers which panel (by AxisVM UID) belongs to which Rhino
  surface (`panelUids`), and panels drawn in AxisVM by their outline (`panelIds`).
- **Elements drawn in AxisVM** get a new id when you Send to Rhino. Rhino creates the object **with that same id**, so from
  then on both programs know the element by one id — whichever side created it.
- **Signatures:** at every load, the link file records a fingerprint of every Rhino object (`sigs`): its geometry in metres
  (0.1 mm) and all its `Ax_*` attributes. Send records the fingerprint of every element it creates (exactly what Rhino will
  echo back). The next load compares fingerprints to see what changed.

## 3. Load from Rhino — "Only apply what changed" (default)

An object is **left exactly as it is in AxisVM** when its fingerprint is unchanged and its AxisVM entities still exist —
including everything added to it in AxisVM (edge hinges, supports, meshes, member assemblies, loads drawn in AxisVM).

Rebuilt are:

1. Objects that **changed, are new, or were deleted** in Rhino.
2. Curves that **share an AxisVM line or an AxisVM member** with one of those (overlapping curves; lines joined into one member in AxisVM).
3. Objects whose geometry **touches** the old or new geometry of an object that **moved, appeared or disappeared** (one ring:
   a kept line is never left with an unconnected node on it). An attribute-only change (new section, new load) has no ring.
4. **Groups made in one AxisVM call:** a part (`Ax_Parts`), a load panel with its `Ax_LoadTargets`, an edge hinge with its
   `Ax_HingeDomains`, a line-to-line link with its master.
5. A domain or load panel that **loses an edge line** — as the surface only; its other edges stay and are reused.

Rigid diaphragms are re-made over the kept and the rebuilt lines; kept members get their type back from their member records.

A rebuilt domain whose outline runs over kept lines (a meshed wall, for instance) is matched node to node. Where a kept
line stops a few millimetres short of the next one, because the line between them went with a rebuilt neighbour, the
add-on draws that short line again so the outline closes (the report says how many).

Things AxisVM made on rebuilt elements (edge hinges, edge/line supports, elastic foundations, global nodal supports) are
**put back where the same geometry is rebuilt**. Where the geometry itself changed they cannot be placed, and the report
says how many were lost. Rhino's own supports and hinges are rebuilt as Rhino now says — a support removed in Rhino stays removed.

The report shows `N unchanged, N changed, N new, N deleted in Rhino, N neighbours rebuilt` and how the set grew.

**Full replace** (option off, a plain AxExport file, the first load, or changed units / merge tolerance): everything the last
load built is deleted and rebuilt. Elements drawn in AxisVM that Rhino has not loaded yet are never deleted.

## 4. Send to Rhino

For linked elements, only what changed in AxisVM since the last load is sent (property by property). Elements drawn in AxisVM
are sent complete, as new Rhino objects. Elements deleted in AxisVM are listed; Rhino asks before deleting them.

Edits made in AxisVM to an element Rhino did not change are **kept** by the next load. An element changed on both sides is
rebuilt from Rhino's version — Send first, then load in Rhino, to keep both.

## 5. Elements

Units in Rhino attributes: lengths in the Rhino model unit; forces, stiffnesses and loads in kN-based units
(the AxisBridge dialogs always write those). `1E+10` = rigid.

| Element | Rhino | Attribute(s) and format | AxisVM | → AxisVM | → Rhino |
|---|---|---|---|---|---|
| Beam / Rib / Truss | curve | `Ax_Type`, `Ax_Section` (`IPE300`, `300x500`, `HEA200; HEA300`), `Ax_Material`, `Ax_LocalAxis`, `Ax_Eccentricity` | beam / rib / truss, one member per straight run | ✓ | ✓ |
| End release | curve | `Ax_Release`: `fix`, `pin`, `semi-pin`, or `start=kx,ky,kz,kxx,kyy,kzz; end=…` | releases at the curve's two far ends | ✓ | ✓ |
| Simple point | point, `Ax_Type=Simple` | — | plain node (no support or load needed) | ✓ | — |
| Point support | point | `Ax_Support`: `fixed`, `pinned`, `roller`, or 6 stiffnesses (kN/m, kNm/rad) | global nodal support | ✓ | ✓ (new supports become points) |
| Line support on a slab / wall edge | Simple curve on the edge | `Ax_Support` (6 values per metre) | edge support (global) | ✓ | ✓ |
| Elastic foundation along a beam / rib | Beam / Rib curve | `Ax_Support` (kx,ky,kz per metre; rotations ignored) | beam / rib elastic foundation | ✓ | ✓ |
| Surface support | Shell / Plate / Membrane surface | `Ax_SurfaceSupport`: `kz` or `kx,ky,kz` (kN/m/m²) | domain elastic foundation (a plate keeps kz only) | ✓ | ✓ |
| Spring | curve, `Ax_Type=Spring` | `Ax_Stiffness` (6 values) | spring element | ✓ | ✓ |
| Node-node link | curve, `Ax_Type=NodeLink` | `Ax_CoupledDOF`: 6 flags, 1 = coupled, 0 = free (or a stiffness) | node-to-node link | ✓ | ✓ |
| Rigid link | curve, `Ax_Type=RigidLink` | — | rigid body | ✓ | ✓ |
| Line-to-line link | the slave curve | `Ax_LineLink` = id of the master curve; `Ax_Stiffness` optional (rigid if absent) | line-to-line link + two link lines | ✓ | ✓ |
| Edge hinge | curve, `Ax_Type=EdgeHinge` | `Ax_Stiffness` (per metre), `Ax_HingeDomains` (blank = every domain along it) | edge connection | ✓ | ✓ (one curve per straight run, per domain and stiffness) |
| Shell / Plate / Membrane | surface | `Ax_FEType`, `Ax_Thickness`, `Ax_Material`, `Ax_LocalAxis`, `Ax_Eccentricity` | domain (holes included) | ✓ | ✓ |
| Load panel (two-way / one-way) | surface | `Ax_FEType=LoadPanel` / `OneWayLoadPanel`, `Ax_LoadTargets`, `Ax_SpanDirection`, `Ax_EdgeSpacing` | load panel | ✓ | ✓ (two-way) |
| Rigid diaphragm | curves / surfaces | `Ax_Diaphragm` = name; equal names = one diaphragm | diaphragm (XY, XZ or YZ plane) | ✓ | ✓ |
| Loads | any | `Ax_NodalLoad`, `Ax_PointLoad`, `Ax_DistLoad` (`.1`, `.2` … for more) | nodal / member / line / surface / panel loads | ✓ | loads of elements drawn in AxisVM |
| Parts | any | `Ax_Parts` (`;`-separated names) | custom parts | ✓ | — |
| Library | document (AxLibrary) | materials, sections, load cases, combinations | catalog entries, load cases, combinations | ✓ | ✓ |
| Custom material / outline | library JSON: a material with `custom` (type, ex, ey, nu, rho, g, strengths in kN/m², kg/m³), a section with `shape: Polygon`, `outer`, `holes` (mm) | created in AxisVM, updated on the next load; a custom material wins over a catalog name | ✓ | — |

## 6. Limits

- **A line-to-line link** needs both curves to stay one AxisVM line each (not split by crossing elements). AxisVM makes
  both linked members ribs; the add-on builds them as ribs straight away, Rhino keeps its `Beam`, and nothing is reported
  as changed.
- **Supports other than global ones** (beam-relative, edge-relative, reference supports) and isolators are not sent to Rhino,
  and are not carried over when their element is rebuilt.
- **A beam on a slab edge is built as a rib** (AxisVM needs that). An end release on it may then be refused by AxisVM
  (reported per curve) — this only happens when such a member is rebuilt, kept members keep what they have.
- **Overlapping curves of different types** share one AxisVM line; the first typed one decides its type (the report says so).
  A curve lying wholly on another curve's line does not take that curve's rigid diaphragm on Send.
- **Units:** the add-on reads the Rhino unit from the file (`source.unitSystem`); millimetre and metre models both work.
- **Meshes** are not part of the link: a rebuilt domain comes without a mesh (tick "Mesh the rebuilt domains again", or mesh in AxisVM).
- **Edge hinges / supports made in AxisVM** on a rebuilt element come back only where the same geometry is rebuilt.
- **A rigid diaphragm is re-made** when one of its members is rebuilt (AxisVM cannot add lines to one); its other members are
  not rebuilt, they get their member type back.
- **Splitting or joining lines in AxisVM:** pieces that keep their `RhinoGuid` are found again; pieces AxisVM created without
  it (a re-joined mesh line, for instance) look like elements drawn in AxisVM.
- **Domains AxisVM refuses** (`-100002`: outline not planar or not closed) are reported and not retried until they change in Rhino.
- **Units / tolerance changed** in the dialog: the next load replaces everything.
- **Undo:** one Ctrl+Z in AxisVM reverts a whole load; one Undo in Rhino reverts a whole AxLink load.

## 7. AxisVM add-on traps the link works around

These are AxisVM API behaviours, recorded so they are not rediscovered:

- `Members.Add` inside `BeginUpdate` corrupts AxisVM's memory on large models (a crash at the end of the load). Members are
  therefore joined between two update blocks.
- Adding lines to a rigid diaphragm turns beams and ribs into plain lines; re-typing them line by line duplicates members and
  their loads. The add-on restores the member records instead.
- Deleting a domain deletes the edge supports and edge hinges on its edges.
