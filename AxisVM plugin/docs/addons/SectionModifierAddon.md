# Section Modifier Addon

[Illustrated user guide](../user-guide/section-modifier.md)

The existing engine modifies rectangular sections for selected beam, rib and truss lines, including lines belonging to selected members. The dialog exposes two optional dimensions in millimetres: width and depth.

## Existing engine behavior

1. Collect selected line IDs and selected members' lines.
2. Read beam/rib/truss material and section data.
3. Read the current rectangular dimensions from a section name containing exactly two numeric parts separated by `x`, interpreted in millimetres. The fallback is 100 mm for each unparsed dimension.
4. Replace the ticked dimensions with the dialog values; retain unticked dimensions using the rule above.
5. Create or reuse a rectangular section named `WxD`. Assign it to the selected line, preserving the material and the data passed back to DefineAsBeam/DefineAsRib/DefineAsTruss. Beam/rib start and end receive the same new section.
6. Redraw and report the number modified. Per-line read or definition failures can be skipped by the existing implementation.

There is no steel-profile mapping table, next-profile lookup, or generic section-shape editor. Restrict use to the intended rectangular naming convention and inspect the resulting assignments. This presentation update does not change that behavior.

## Sources

- `Addons/SectionModifierAddon.cs`: registration and toolbar pictogram.
- `UI/SectionModifierForm.cs`: dimension fields and validation.
- `Functions/SectionModifierFunction.cs`: unchanged section engine.
