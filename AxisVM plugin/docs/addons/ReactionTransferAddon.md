# Reaction Transfer Addon

[Illustrated user guide](../user-guide/reaction-transfer.md)

## Overview
Transfers support reactions from a source model into a target model as **nodal point loads** — typically to
hand superstructure reactions down to a substructure/foundation model without re-typing them.

There are two target modes, chosen in the form:

| | **Create New File** | **Modify Existing File** |
|---|---|---|
| What the target starts as | a clone of the source | the user's own existing model |
| Trimming | everything unselected is deleted from the clone | nothing is ever deleted |
| Node matching | by node **UID** (the target *is* the source) | by **coordinate**, within a user tolerance |
| Load case / group matching | not needed — they carried over 1:1 | by **name**; missing ones are created |
| Backup | none needed (the target is new) | a timestamped `.axs` copy next to the target |

## Workflow

### 1. Guards and snapshot
- Refuses to run if the source model has never been saved (no filename).
- Refuses to run if the target path is the same file as the source.
- In existing-file mode, refuses to run if the target file does not exist, and asks the user to confirm
  before writing into a model they already own.
- Saves the source model to its own path first, as a safety snapshot. This matters even more in
  existing-file mode, where `LoadFromFile` replaces the in-memory model outright.

### 2. Extraction (read-only)
- `ExtractSelection` records the selected nodes, lines, members, surfaces, domains, load panels, nodal
  supports and link elements via `GetSelectedItemIds`.
- `ExtractLoadCaseCatalogue` records the source's load cases (name, type, group) and load groups (name,
  type, `SimultExc`, combination type, γ/ψ factors). The clone path never needs this; the existing-file
  path uses it to decide, per case, whether the target already has one by that name.
- `ExtractSupportsAndReactions` walks nodal supports `1..Count` (indices are dense) and keeps the ones
  sitting on a selected node or that are themselves selected. For each it stores **both** the node UID
  (indices shift once trimming starts, UIDs do not) **and** the node's coordinates (UIDs mean nothing
  across two independent models).
- Reactions are read in bulk, one COM call per load case:
  - Linear / non-linear: `Results.Forces.GetAllNodalSupportForcesByLoadCaseId(lc, 0, analysisType, out arr)`
  - Envelope: `Results.Forces.GetAllEnvelopeNodalSupportForces(minMax, analysisType, component, out arr)`

  Each returns one record per nodal support, ordered by support index. A per-support fallback
  (`GetNodalSupportForceByLoadCaseId` / `GetEnvelopeNodalSupportForce`) runs if the bulk call yields nothing.

### 3a. New-file mode — clone, trim, apply
`sourceModel.SaveToFile(TargetFilePath, lbFalse)`. AxisVM keeps a single model instance, so from this point
the in-memory model *is* the target file and the same COM object is used for both.

Trimming is wrapped in `BeginUpdate` / `EndUpdate` (exception-safe, via `try/finally`).

- First, sub-elements of selected owners are protected: each selected member, domain, surface and load
  panel contributes its lines, and every kept line contributes its two end nodes. Without this, deleting an
  "unselected" node would cascade and break a selected member.
- Design members and virtual beams are cleared outright — they lock geometry.
- Everything else is removed with **one `DeleteSelected()` call per collection**: mark the doomed items,
  delete once. This avoids both the per-item `Delete` cost and the index-shift bugs that come from comparing
  pre-deletion indices against a collection that renumbers as you delete.
  Nodes take the faster `BulkDelete` path.
- Order: link elements → domains → surfaces → members → load panels → lines → nodes → nodal supports.
- Nodal supports have neither a UID nor `SelectAll`, so they are matched through the UID of the node they
  sit on. Deleting a node already removes its support, so only the "Keep Existing Supports" filter is left to apply.

Then `ApplyReactionsByUID`: node UIDs are resolved to new indices through a single `Nodes.BulkGetUIDs` pass,
not one `IndexOfUID` call per reaction.

### 3b. Existing-file mode — back up, open, add
1. **Backup.** The target is copied next to itself as `<name>_backup_<yyyyMMdd_HHmmss>.axs`. If the copy
   fails, nothing else happens. The path is repeated in the summary and in any error dialog.
2. **Open.** `model.LoadFromFile(TargetFilePath)`. Everything needed from the source is already in memory,
   and the source file was saved in step 1, so replacing the in-memory model is safe. AxisVM is left showing
   the target model when the run finishes — the confirmation dialog says so up front.
3. **Apply** (`ApplyReactionsByCoordinate`), inside `BeginUpdate` / `EndUpdate`. Everything is matched by
   identity rather than by index:
   - **Nodes** — `Nodes.IndexOf(x, y, z, tolerance, 1)`. A hit reuses the existing node; a miss calls
     `Nodes.Add`. Results are cached per coordinate, so the second load case's reaction at the same support
     reuses whatever the first one resolved or created. An empty target skips `IndexOf` entirely
     (its `StartIndex` must lie inside `1..Count`).
   - **Load cases** — matched by name, case-insensitively. A hit is reused; a miss is created with the
     source case's type via `LoadCases.AddWithGroup`. Types that only their own generator can create
     (snow, wind, seismic) fall back to a plain `lctStandard` case under the same name, which is reported as
     a warning — the reactions still land under the right name.
   - **Load groups** — same rule. Matched by name, otherwise created with the source group's type,
     `SimultExc` and combination type; the γ/ψ factors are copied afterwards because `Add` does not take them.

### 4. Apply rules (both modes)
- Reactions whose six components are all below 1e-9 are skipped.
- Load cases of type `lctSelfWeight` are skipped — AxisVM generates those loads itself and rejects additions.
  In existing-file mode this is decided from the *source* case type as well, before any target lookup.
- Loads are batched and submitted with a single `Loads.AddNodalForces` call, falling back to per-load
  `AddNodalForce` if the bulk call fails.
- **Sign**: reactions are applied with the same sign they were read with (`Fx = Rx`). There is no reversal.
- **Envelope mode**: results are written to a dedicated load case named `Transferred Reactions (Env)`,
  reused if it already exists, created on demand otherwise, so no existing load case is contaminated.

### 5. Report
One summary dialog at the end: mode, target path, backup path, loads applied, reactions read, nodes and
load cases/groups reused vs created, everything skipped and why, plus a per-stage timing breakdown
(extract / clone-or-open / trim / apply / save).

## File Structure
- `Addons/ReactionTransferAddon.cs` — COM interface registration, toolbar icon, menu visibility.
- `UI/ReactionTransferForm.cs` — target mode + path, result type, envelope component + Min/Max,
  "Keep Existing Supports", node match tolerance.
- `Core/ReactionTransferLogic.cs` — extraction, trimming and load application. All the real work.
- `Functions/ReactionTransferFunction.cs` — thin wrapper: validates the active model, shows the form,
  confirms the existing-file mode, maps controls onto `ReactionTransferSettings`.
- `backup/ReactionTransfer_pre-existing-file-mode/` — the previous version of the four files above, kept as
  a reference copy from before the existing-file mode was added.

## Notes / limitations
- **Beam point loads were removed.** Reactions are always applied as nodal point loads now; the
  "Apply as Beam Point Loads" radio pair, its `FindBeamTarget` host-line search and the disabled, never
  implemented "Tidy Up (merge divided foundation beams)" checkbox are all gone.
- "Keep Existing Supports" only steers the trimming pass, so it is disabled in existing-file mode. The node
  match tolerance is the mirror image: it is disabled in new-file mode, where nodes match by UID.
- Coordinate matching assumes the two models share an origin and orientation. There is no transform.
- `ReactionTransferAddon.csproj` compiles only its own four sources — `Core/AxisVMHelper.cs` is not part of
  this project, so any shared helper has to be added to the csproj first.
- The source model must have been analysed; without a result database the extraction returns zero reactions
  and the run aborts before anything is written.
