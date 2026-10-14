# Reaction Transfer Addon

## Overview
Transfers support reactions from a source model into a new, trimmed copy of that model — typically to
hand superstructure reactions down to a substructure/foundation model without re-typing them.

The implementation is **save-and-trim**, not coordinate matching between two independent files. The
source model is cloned to the target path with `SaveToFile`, then everything the user did not select is
deleted from the clone, and the reactions read from the source are re-applied as point loads. Because the
target starts as a byte-for-byte clone, load cases, materials, sections and load groups carry over 1:1 and
never need to be mapped.

## Workflow

### 1. Guards and snapshot
- Refuses to run if the source model has never been saved (no filename).
- Refuses to run if the target path is the same file as the source — that would destroy the model being read.
- Saves the source model to its own path first, as a safety snapshot.

### 2. Extraction (read-only)
- `ExtractSelection` records the selected nodes, lines, members, surfaces, domains, load panels, nodal
  supports and link elements via `GetSelectedItemIds`.
- `ExtractSupportsAndReactions` walks nodal supports `1..Count` (indices are dense) and keeps the ones
  sitting on a selected node or that are themselves selected. For each it stores the **node UID**, not the
  node index — indices shift once trimming starts, UIDs do not.
- Reactions are read in bulk, one COM call per load case:
  - Linear / non-linear: `Results.Forces.GetAllNodalSupportForcesByLoadCaseId(lc, 0, analysisType, out arr)`
  - Envelope: `Results.Forces.GetAllEnvelopeNodalSupportForces(minMax, analysisType, component, out arr)`

  Each returns one record per nodal support, ordered by support index. A per-support fallback
  (`GetNodalSupportForceByLoadCaseId` / `GetEnvelopeNodalSupportForce`) runs if the bulk call yields nothing.

### 3. Clone
`sourceModel.SaveToFile(TargetFilePath, lbFalse)`. AxisVM keeps a single model instance, so from this point
the in-memory model *is* the target file and the same COM object is used for both.

### 4. Trim
Wrapped in `BeginUpdate` / `EndUpdate` (exception-safe, via `try/finally`).

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

### 5. Apply
- Node UIDs are resolved to new indices through a single `Nodes.BulkGetUIDs` pass, not one `IndexOfUID`
  call per reaction.
- Reactions whose six components are all below 1e-9 are skipped.
- Load cases of type `lctSelfWeight` are skipped — AxisVM generates those loads itself and rejects additions.
- **Node point loads**: batched and submitted with a single `Loads.AddNodalForces` call.
- **Beam point loads**: the host line comes from `Nodes.GetConnectedLines`, cached per node. Beams
  (`ELineType.ltBeam`) win over columns so a column-base reaction lands on the foundation beam rather than
  the column above it; ties break on the lowest line id for determinism. `Position` is 0 at the line's start
  node and `Line.Length` at its end node. If no usable line exists, it falls back to a nodal load and the
  count is reported.
- **Sign**: reactions are applied with the same sign they were read with (`Fx = Rx`). There is no reversal.
- **Envelope mode**: results are written to a dedicated load case named `Transferred Reactions (Env)`,
  created on demand, so no existing load case is contaminated.

### 6. Report
One summary dialog at the end: loads applied, reactions read, everything skipped and why, plus a
per-stage timing breakdown (extract / clone / trim / apply / save).

## File Structure
- `Addons/ReactionTransferAddon.cs` — COM interface registration, toolbar icon, menu visibility.
- `UI/ReactionTransferForm.cs` — target path, result type, envelope component + Min/Max, application
  method, "Keep Existing Supports".
- `Core/ReactionTransferLogic.cs` — extraction, trimming and load application. All the real work.
- `Functions/ReactionTransferFunction.cs` — thin wrapper: validates the active model, shows the form,
  maps controls onto `ReactionTransferSettings`.

## Notes / limitations
- The "Tidy Up (merge divided horizontal foundation beams)" checkbox is present but **not implemented**;
  it is disabled in the form.
- `ReactionTransferAddon.csproj` compiles only its own four sources — `Core/AxisVMHelper.cs` is not part of
  this project, so any shared helper has to be added to the csproj first.
- The source model must have been analysed; without a result database the extraction returns zero reactions
  and the run aborts before the clone.
