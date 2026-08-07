# One Way Load Panel Addon

## Overview
The One Way Load Panel Addon automates the generation and configuration of one-way load distribution panels in AxisVM. It is designed to quickly assign one-way spanning logic over domains and automatically split panels to accurately distribute loads to edge beams versus main spanning beams based on user-defined geometries and tributary spacings.

## Workflow & Logic

### 1. Selection & Input Gathering
- The user provides 4 edge planar closed lines that define the boundary of the loaded area.
- The UI prompts the user for configuration parameters:
  - **Span Direction**: Direction 1 or 2 (defining which way the main slab/deck spans).
  - **Spacing / Splitting Strategy**: User inputs a spacing value (e.g., 300mm for edge beam tributary width).
  - **No Spacing Checkbox**: If checked, the spacing logic is bypassed, and a single one-way panel is generated over the entire domain.

### 2. Basic Generation (No Spacing)
- If the user selects "No Spacing", the script retrieves the geometric domain defined by the 4 selected boundary lines.
- It calculates the shortest span between the opposing parallel (or near-parallel) boundary lines.
- It generates a single `IAxisVMLoadPanel` covering the domain and explicitly sets its load distribution attribute to "One-Way", assigning the span direction to match the calculated shortest span or user override.

### 3. Advanced Splitting (With Spacing)
- If a spacing is provided (e.g., 300mm for edge beam tributary), the addon performs a geometric split of the domain to ensure realistic load paths.
- **Edge Strips**: It offsets the boundary lines parallel to the edge beams inward by the spacing value (e.g., 300mm). This creates two narrow rectangular strips along the edges.
- **Center Strip**: The remaining area between the edge strips becomes the center panel.
- **Panel Assignment**: 
  - The script generates separate `IAxisVMLoadPanel` objects for each calculated strip.
  - The **edge strips** are configured to distribute load directly to the edge beams.
  - The **center strip** is configured as a one-way panel spanning across the main beams, ignoring the edge beams.

### 4. Application
- The model enters an `Update` state to ensure bulk generation is fast.
- The newly generated domains and load panels are committed to the AxisVM model.
- Load panel distribution parameters are strictly enforced via the AxisVM COM API.

## File Structure
- `Addons/OneWayLoadPanelAddon.cs`: AxisVM COM interface registration and Addon icon definition.
- `UI/OneWayLoadPanelForm.cs`: The user interface for defining span directions and spacing parameters.
- `Functions/OneWayLoadPanelLogic.cs`: The core logic that calculates geometric offsets, creates domains, and assigns load panel attributes.
