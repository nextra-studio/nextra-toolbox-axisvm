# Concrete Design Addon

## Overview
The Concrete Design Addon automates the manual and iterative process of checking and designing reinforced concrete elements within AxisVM. Instead of manually reviewing forces, guessing reinforcement ratios, and checking against code requirements, this addon performs automated iterations to find the optimal reinforcement layout, considers structural zoning, checks lap lengths, and can generate preliminary reinforcement drawings.

## Workflow & Logic

### 1. Selection & Input Gathering
- The user selects specific concrete members (lines or domains) in the AxisVM model.
- The UI presents design parameters:
  - **Target Reinforcement Percentage (%)**: The initial goal or maximum limit for reinforcement density.
  - **Code Requirements**: Selection of design code (e.g., Eurocode 2) parameters for minimum/maximum spacing and cover.
  - **Zoning Settings**: Allows differentiation between support zones (where shear or negative moment is high) and midspan zones.
  - **Lap & Anchorage**: Configurations for calculating required lap splice lengths and anchorage requirements based on bar diameters.

### 2. Result Extraction
- The addon interfaces with the AxisVM result database to extract the critical internal forces (N, Vy, Vz, Mx, My, Mz) for the selected members under the critical load combinations.
- It maps these forces along the length of the member to identify peak demand locations (e.g., supports vs. midspan).

### 3. Automated Design Iteration
- **Zoning Generation**: The script divides the member into logical zones (e.g., Support A, Midspan, Support B) based on the force envelope and user settings.
- **Reinforcement Calculation**:
  - For each zone, it begins with an initial guessed reinforcement layout based on the user's target %.
  - It pushes this layout back into the AxisVM design module (or uses internal analytical formulas) to check the utilization ratio.
  - If the utilization is > 1.0 (unsafe) or << 1.0 (uneconomical), it iterates by adjusting bar diameters or spacing.
  - It ensures code compliance (min/max reinforcement area, spacing limits).
- **Detailing Checks**: Once longitudinal and shear reinforcement are optimized, the script calculates required lap lengths at splice locations and anchorage lengths at supports based on the selected bar diameters and concrete grade.

### 4. Output & Reporting
- The final optimized reinforcement layouts are applied to the members in the AxisVM model.
- (Planned Feature) **Drawing Generation**: The script generates 2D CAD-like drawing outputs or DXF exports detailing the reinforcement layout, bar marks, zones, and lap lengths for drafting.

## File Structure
- `Addons/ConcreteDesignAddon.cs`: AxisVM COM interface registration and Addon icon definition.
- `UI/ConcreteDesignForm.cs`: The user interface for defining design parameters and targets.
- `Functions/ConcreteDesignLogic.cs`: The core engine for result extraction, iterative design loops, and detailing calculations.
