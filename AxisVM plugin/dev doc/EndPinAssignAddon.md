# End Pin Assign Addon

## Overview
The End Pin Assign Addon is designed to automatically assign structural pins (hinges or semi-rigid connections) to the true "free ends" of continuous structural lines. This automates the tedious process of manually identifying which nodes in a structural model should have moment releases to avoid unwanted rigid moment transfers in truss-like or hinged structural systems.

## Workflow & Logic

### 1. Selection & Data Gathering
- The script retrieves all currently selected lines in the active AxisVM model.
- If only **one line** is selected, the script will automatically traverse the model to find all connecting lines to form a chain (unless bypassed).
- For each relevant line, it retrieves the start and end node coordinates (`Node1`, `Node2`) and maps them.

### 2. Continuity Checking (IsContinuousStrict)
- The core logic relies on determining if two lines intersecting at a node are "continuous" (acting as a single continuous beam) or if they represent a sharp break/corner.
- **Angle Calculation**: It computes the 3D directional vectors of the two lines pointing outwards from the shared node.
- **Smaller Angle**: It calculates the interior angle between the two lines, then finds the "smaller angle" of the bend: `Math.Min(angle, 180 - angle)`.
- **Tolerance Checking**: The user provides an "Angle Tolerance" (e.g., 90°). If the calculated smaller angle is less than or equal to this tolerance, the lines are considered continuous. For instance, a tolerance of 90° will group straight lines (0°), right angles (90°), and sharp corners (60°) as continuous.
- Lines that satisfy this continuity check are grouped together into "Chains" using a Breadth-First Search (BFS) algorithm.

### 3. Identifying Free Ends
- Once chains are built, the script counts how many times each node appears within a specific chain.
- A node that appears exactly **once** in the chain's connectivity map is identified as a "free end" of that chain.

### 4. Model Continuity Check (Unselected Filter)
- The UI includes a checkbox: **Check continuity against unselected lines**.
- If checked, the script iterates through the entire structural model to see if any of the identified "free ends" actually connect to an UNSELECTED line in the model.
- If a free end connects to an unselected line, and they are continuous (based on the angle logic), then this node is NOT a true free end—it is just a segment boundary in a larger continuous beam. The script removes this node from the list of valid free ends.

### 5. Applying Pins (Releases)
- The remaining valid free ends are sorted spatially (by Z, then X, then Y) to designate a "Start" and "End" node for the chain.
- Based on the user's UI choice (`Chain: Both Ends`, `Chain: Start Node Only`, `Chain: End Node Only`), it determines which ends receive the pin.
- It applies a complete hinge (`yy`, `zz` free, and optionally `xx` torsion free) or a semi-rigid spring with user-defined stiffness to those specific ends.
- Importantly, it modifies **only** the rotational releases, leaving existing translational releases (e.g., supports) untouched.

## File Structure
- `Addons/EndPinAssignAddon.cs`: AxisVM COM interface registration and Addon icon definition.
- `UI/EndPinAssignForm.cs`: The user interface for selecting Pin Type, Target, Angle Tolerance, and Continuity checking.
- `Functions/EndPinAssignFunction.cs`: The core logic and mathematical processing described above.
