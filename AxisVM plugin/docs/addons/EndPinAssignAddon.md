# End Pin Assign Addon

[Illustrated user guide](../user-guide/end-pins.md)

## Overview
The End Pin Assign Addon is designed to automatically assign structural pins (hinges or semi-rigid connections) to the true "free ends" of continuous structural lines. This automates the tedious process of manually identifying which nodes in a structural model should have moment releases to avoid unwanted rigid moment transfers in truss-like or hinged structural systems.

## Workflow & Logic

### 1. Selection & Data Gathering
- The script retrieves all currently selected lines in the active AxisVM model.
- Chains are built only from selected beams and ribs. A single selected line remains a one-line chain; the continuity option only filters its ends. Other selected line types are excluded with a warning.
- Connectivity uses AxisVM node IDs, not rounded coordinates: coincident but distinct nodes remain disconnected. Coordinates retain full precision for angle calculations.
- Selection array lower bounds are respected, and sorted line IDs make direction ties independent of selection order.
- For each line it reads `IAxisVMLine.StartNode` / `EndNode`, documented as "the node at the start / end of
  the local x axis". Those are the authority on a line's direction, and they are what
  `SetStartReleases` / `SetEndReleases` act on. The earlier `BulkGetLineData` path was dropped: its
  `NodeId1` / `NodeId2` are not guaranteed to follow local x (and the same call has been observed handing
  back zeroed node ids elsewhere in this plugin).

### 2. Continuity Checking (IsContinuousStrict)
- The core logic relies on determining if two lines intersecting at a node are "continuous" (acting as a single continuous beam) or if they represent a sharp break/corner.
- **Angle Calculation**: It computes the 3D directional vectors of the two lines pointing outwards from the shared node.
- **Smaller Angle**: It calculates the interior angle between the two lines, then finds the "smaller angle" of the bend: `Math.Min(angle, 180 - angle)`.
- **Tolerance Checking**: The user provides an "Angle Tolerance" (e.g., 90°). If the calculated smaller angle is less than or equal to this tolerance, the lines are considered continuous. For instance, a tolerance of 90° will group straight lines (0°), right angles (90°), and sharp corners (60°) as continuous.
- Lines that satisfy this continuity check are grouped together into "Chains" using a Breadth-First Search (BFS) algorithm.

### 3. Identifying Free Ends
- Once chains are built, the script counts how many times each node appears within a specific chain.
- A node that appears exactly **once** in the chain's connectivity map is identified as a "free end" of that chain.

### 4. Direction Normalisation (NormaliseChainDirections)
This runs **before** any release is assigned.

- The UI includes a checkbox: **Auto-orient chain (align local x)**, ticked by default. It controls only
  the model edit - the chain is walked either way, so a chain's Start and End are well defined even with
  the box cleared. Unticked, the addon reports how many lines still run against their chain direction and
  touches no line attribute.
- Each chain is walked end-to-end from one free end to the other (`TraceChain`). Every line is then
  re-oriented so its local x axis points the same way along that walk.
- The walk direction is decided by majority vote: whichever of the two possible directions already matches
  most of the chain's lines wins, so the smallest possible number of lines is touched.
- Re-orientation goes through `IAxisVMLines.BulkGetAttr_V161` / `BulkSetAttr_V161`, writing
  `RLineAttr_V161.LocalXOrientation` (`mlxo_ij` / `mlxo_ji` — local x from the lower node index to the
  higher one, or the reverse). The attribute records are read and written back untouched apart from that
  single field. This flips only the local axis: no node is renumbered, no line is split or moved, and the
  releases stay on the same physical node (in that record they are stored against the lower/higher node
  index, not against local x).
- **Why it matters**: a chain whose lines were drawn in mixed directions has no well-defined Start and End,
  so `Chain: Start Node Only` landed on an arbitrary end and the per-line start/end release assignment
  could not be reasoned about. After normalisation the chain's Start is the free end the walk begins at and
  the End is where it finishes.
- Chains that are not simple open runs — loops, branches, or anything the walk cannot cover completely —
  keep their local directions and are reported as a warning. Start/End-only targets are skipped because no unique order exists; Both Ends can still pin branch leaves. Closed loops have no free ends. A loop with two tails is also rejected as a simple chain. Lines that
  are not `ltBeam` / `ltRib` are also skipped, since `BulkSetAttr` rejects edges and links.
- If an orientation write fails or partially succeeds, all affected lines are re-read before release assignment. The summary counts confirmed reversals and reports failures. If a line cannot be refreshed, release assignment stops to avoid using stale local directions.
- Which physical node receives a release does not depend on this step: `isStartFree` / `isEndFree` are
  decided from the line's own start/end coordinates, which always follow its current local x. Auto-orient
  makes the chain readable and keeps `Chain: Start / End Node Only` pointing at the intuitive end.
- **Interop gotcha**: the modified records must be copied into a *fresh* array created with
  `Array.CreateInstance(typeof(RLineAttr_V161), ...)`; they cannot be written back into the array
  `BulkGetAttr_V161` returns. The interop types are embedded (`EmbedInteropTypes`), so that array's
  element type is only *equivalent* to this assembly's `RLineAttr_V161`, not identical. A cast honours the
  equivalence but `Array.SetValue` does an exact element-type check on value-type arrays and throws
  "Object cannot be stored in an array of this type."

### 5. Model Continuity Check (Unselected Filter)
- The UI includes a checkbox: **Check continuity against unselected lines**.
- If checked, the free ends are tested against the lines that actually meet at that node
  (`Nodes.GetConnectedLines`), skipping the ones in the selection. The earlier implementation scanned every
  structural member in the model, which was both slow and blind to lines belonging to no member.
- If a free end connects to an unselected line, and they are continuous (based on the angle logic), then this node is NOT a true free end—it is just a segment boundary in a larger continuous beam. The script removes this node from the list of valid free ends.
- Only unselected beams/ribs count as structural continuations. If connectivity or a connected line cannot be read, the affected end is skipped with a warning.
- This filter only decides **which** ends get a pin. The Start/End ordering still comes from the normalised
  trace in step 4, so a filtered-out end does not reshuffle the chain's direction.

### 6. Applying Pins (Releases)
- The chain's "Start" is the first free end of the normalised trace and its "End" is the last.
- Based on the user's UI choice (`Chain: Both Ends`, `Chain: Start Node Only`, `Chain: End Node Only`), it determines which ends receive the pin.
- It applies a complete hinge (`yy`, `zz` free, and optionally `xx` torsion free) or a semi-rigid spring with user-defined stiffness to those specific ends.
- Importantly, it modifies **only** the rotational releases, leaving existing translational releases (e.g., supports) untouched.
- Existing torsion (`xx`) is preserved at ends that do not explicitly receive a new torsion release. With Release Torsion checked, an existing non-rigid torsion release anywhere in the chain prevents adding another. Otherwise, one new torsion release is added per chain — releasing it at both ends would leave the chain free
  to spin about its own axis.
- Release read/write return codes are checked. Failed writes are reported and do not count as assigned ends or consume the chain's torsion release.
- Angle tolerance must be finite and between 0 and 90 degrees. Stiffness must be finite and non-negative (zero is allowed). Local decimal separators and dot-decimal defaults are supported.
- One summary dialog at the end: releases assigned, chains found, lines re-oriented, and any warnings.

## File Structure
- `Addons/EndPinAssignAddon.cs`: AxisVM COM interface registration and Addon icon definition.
- `UI/EndPinAssignForm.cs`: The user interface for selecting Pin Type, Target, Angle Tolerance, and Continuity checking.
- `Functions/EndPinAssignFunction.cs`: The core logic and mathematical processing described above.

## Focused validation
Run `dotnet build Checks/EndPinAssignChecks.csproj --no-restore` and then
`Checks/bin/EndPinAssignChecks/EndPinAssignChecks.exe`. These checks compile the production
logic and dialog and cover node identity, angles, short members, mixed directions, majority
ordering, branches, loops, release preservation and locale/input handling. They do not exercise
live AxisVM COM calls or modify an open model.
