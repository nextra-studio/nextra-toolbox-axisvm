# UI refresh validation ? 2026-10-15

## Scope preservation

Baseline revision: `be6a79103528f5f58adb4bc888d570d8c9414fd4`.

- All 25 pre-existing C# files in Functions and Core match the initial working-tree SHA-256 snapshots byte for byte. The new Core/AddonIcons.cs contains drawing code only.
- The repeatable source guard also checks all 63 original function/core/Rhino C# files against the baseline revision, normalizing line endings only.
- UI methods outside layout initialization and the two one-way preview-painting methods are unchanged. Setting defaults, validation, option mapping, numerical results and export handlers are preserved.
- Add-on entry-point classes are unchanged outside Addon_IconBitmap. COM identifiers, execution, menu placement and existing WIP labels are preserved.
- Build changes include the shared visual helpers and a configurable AxisVMDir. The companion Rhino source is unchanged.

Run from the repository root:

```powershell
python 'AxisVM plugin\Checks\verify_ui_scope.py'
```

## Build and existing checks

Release solution rebuild: **0 warnings, 0 errors**.

| Existing suite | Passed |
|---|---:|
| EndPinAssign | 36 |
| OneWayPanel | 47 |
| ConnectionLoadMath | 29 |
| Total | 112 |

These are the existing offline suites, not new live AxisVM calculations. The UI refresh does not change the production engines they cover.

## Visual review

The offline fixture renders all ten dialog classes, including both create/convert one-way variants, using synthetic inputs. The guide screenshots come from these forms. No AxisVM COM model is opened by the fixture.

Reviewed: rounded single-line inputs, native control and focus states, rounded gradient button painting, header gradients, disabled controls, hidden stiffness rows, all four panel operations, existing-file/envelope transfer, the 150% layout/font stress fixture, and the connection-results window at its minimum width. Measured label heights did not report clipped text in these cases. Input forms can scroll; result actions wrap; the one-way preview and its legend scale together.

Keyboard accept/cancel button references are retained when controls are reparented. Inputs and buttons remain native WinForms controls; visual wrappers do not replace their handlers. Gradient/highlight tint is decorative; no host-window opacity or model UI transparency is changed.

```powershell
.\scripts\Preview-UI.ps1
.\scripts\Preview-UI.ps1 -OutputDir "$env:TEMP\nextra-ui-stress" -Stress
```

The scale fixture is an offline stress check, not a claim that every host DPI/theme combination was tested inside AxisVM.

## Installer

The installer was exercised against a temporary AddOns folder: all seven installed hashes matched, an existing DLL was backed up intact, and WhatIf made no changes. The package includes only the seven toolbox DLLs, not vendor or third-party add-ons.

## Limits

This update has not rerun the live AxisVM analysis/calibration suites. Runtime structural behavior remains the existing implementation; its detailed notes and WIP status still apply. The Section Modifier documentation was corrected to describe its actual rectangular WxD naming convention and fallback, rather than unavailable steel-profile mapping features.
