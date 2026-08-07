# Studs Wall Generator Addon

## Overview
The Studs Wall Generator Addon automatically generates a 3D structural model of a timber stud wall along a user-selected path (a single reference line). It automatically spaces vertical studs, adds top and bottom plates, handles end studs, and generates load panels for sheathing.

## Workflow & Logic

### 1. Selection & Input Gathering
- The user selects a single line in the AxisVM model to act as the "base path" of the wall.
- The UI prompts the user for wall generation parameters:
  - **Wall Height**: Total height of the stud wall.
  - **Stud Spacing**: The maximum distance between vertical studs.
  - **Section Type**: The AxisVM section ID to use for the timber members (e.g., 50x100mm timber).
  - **Material ID**: The AxisVM material ID to assign to the members.
  - Checkboxes for generating load panels and support conditions.

### 2. Geometry Calculation
- **Base Vector**: The script calculates the length and direction (unit vector) of the selected base line.
- **Stud Placement**: 
  - It places a stud at the start node (0.0).
  - It calculates how many intermediate studs are required based on the `Stud Spacing` input. The spacing is calculated to evenly distribute the studs along the wall length (or placed exactly at the spacing distance, depending on implementation).
  - It places a stud at the exact end node (L).
- **Node Generation**: For each stud position, it calculates the 3D coordinates for the bottom node (on the base line) and the top node (offset by the `Wall Height` in the Z direction). It creates these nodes in the AxisVM model.

### 3. Member Generation
- **Vertical Studs**: For each calculated bottom and top node pair, a vertical line member is created.
- **Plates**: 
  - A bottom plate line is drawn connecting all the bottom nodes sequentially.
  - A top plate line is drawn connecting all the top nodes sequentially.
- **Property Assignment**: All generated lines are assigned the user-specified Section ID and Material ID. 
- **Local Axes**: The script automatically rotates the local axes of the studs (usually the major axis aligns with the wall plane) by manipulating the reference vector of the members.

### 4. Auxiliary Elements
- **Supports**: If selected, nodal or line supports are applied along the bottom plate.
- **Load Panels**: If selected, the script generates an `IAxisVMLoadPanel` covering the entire rectangular domain of the wall (Start-Bottom -> End-Bottom -> End-Top -> Start-Top). This is used to transfer wind or area loads into the timber frame.

## File Structure
- `Addons/StudsWallAddon.cs`: AxisVM COM interface registration and Addon icon definition.
- `UI/StudsWallForm.cs`: The user interface for configuring wall parameters.
- `Generators/WallGenerator.cs`: The core logic that calculates node coordinates, generates members, and assigns properties.
