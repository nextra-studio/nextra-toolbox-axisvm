# Reaction Transfer Addon

## Overview
The Reaction Transfer Addon is designed to automate the transfer of support reactions from one model (e.g., a superstructure model) to another model (e.g., a substructure or foundation model). It replaces the manual process of reading reaction forces and applying them as nodal loads.

## Workflow & Logic

### 1. Source Data Extraction
- The addon requires the user to select specific nodes in the "Source Model" where support reactions exist.
- **Result Querying**: It accesses the AxisVM results database for the selected load case or load combination.
- **Reaction Retrieval**: For each selected node, it queries the 6 degrees of freedom (Fx, Fy, Fz, Mx, My, Mz) for the support reactions.
- **Coordinate Mapping**: It records the exact 3D coordinates (X, Y, Z) of the node where the reaction occurred.

### 2. Export / Data Transfer
- The extracted reactions (Force vectors and Moment vectors) along with their corresponding 3D coordinates are temporarily stored in memory or serialized to a file (depending on the implementation approach: direct COM transfer vs. file-based transfer).

### 3. Target Model Application
- The user opens or switches to the "Target Model".
- **Spatial Matching**: The script searches the target model for nodes that match the exact 3D coordinates (X, Y, Z) of the source reactions. A small spatial tolerance (e.g., 1mm) is often used to account for floating-point inaccuracies.
- **Load Generation**: For every matched node, the script generates a new Nodal Load.
- **Force Reversal (Optional but typical)**: A support reaction pointing UP in the superstructure becomes an applied load pointing DOWN on the substructure. The script automatically reverses the sign of the vectors if configured to do so.
- **Load Case Assignment**: The generated loads are placed into a specified Load Case in the target model.

## File Structure
- `Addons/ReactionTransferAddon.cs`: AxisVM COM interface registration and Addon icon definition.
- `UI/ReactionTransferForm.cs`: The user interface for selecting source load cases, target load cases, and spatial tolerances.
- `Functions/ReactionTransferFunction.cs`: The core logic that queries the result database, performs coordinate matching, and generates nodal loads.
