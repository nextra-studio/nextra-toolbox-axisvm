# Section Modifier Addon

## Overview
The Section Modifier Addon provides a rapid way to batch-modify the cross-section properties of selected line members in the AxisVM model. It allows structural engineers to easily bump up or reduce section sizes during the iterative design process without navigating through multiple dialog boxes.

## Workflow & Logic

### 1. Selection & Identification
- The user selects one or more line members in the model.
- The script retrieves the `RLineData` for each selected member to identify its current Section ID and Material ID.

### 2. Section Modification Mapping
- The UI presents a mapping table or dropdowns to the user, allowing them to define transformation rules (e.g., "Change all IPE 200 to IPE 240", or "Increase section depth by 50mm").
- **Database Lookup**: If the modification involves standard steel profiles, the script interfaces with the AxisVM profile database to find the next larger or smaller section in a specific series (e.g., HEA, IPE).
- **Parametric Modification**: If the section is a parametric concrete or timber shape (e.g., Rectangle), the script mathematically calculates the new dimensions (Width, Height).

### 3. Application
- The model enters an `Update` state to ensure bulk operations are fast and do not trigger constant graphical refreshes.
- For each selected member:
  - If a completely new section from the database is required, the script creates a new Section definition (if it doesn't already exist in the model) and updates the member's Section ID pointer.
  - If it's a parametric shape, it modifies the parameters of the existing section definition or creates a new one.
- The changes are committed to the model, and the graphical view is refreshed.

## File Structure
- `Addons/SectionModifierAddon.cs`: AxisVM COM interface registration and Addon icon definition.
- `UI/SectionModifierForm.cs`: The user interface for defining modification rules.
- `Functions/SectionModifierFunction.cs`: The core logic that interacts with the profile database and modifies member data.
