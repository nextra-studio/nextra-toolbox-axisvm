# End pin assign

![End pin assign toolbar icon](../images/EndPinAssign-icon.png) Apply rotational hinges or semi-rigid releases at the free ends of selected chains.

![Workflow schematic](../images/end-pins-workflow.svg)

## Steps

1. Select the beam or rib chains, then open N. End Pin Assign (WIP).
2. Choose Complete Pin or Semi-rigid Pin. For semi-rigid pins, enter the rotational stiffness in kNm/rad.
3. Choose both chain ends, the chain start or the chain end. Enter the continuity angle tolerance.
4. Review Auto-orient, continuity against unselected lines and torsion release. Press OK and inspect the summary.

## Dialog

![End pin assign dialog with example data](../images/EndPinAssignForm.png)

The screenshot is an offline preview with synthetic inputs. It is not a model-analysis result.

## Existing behavior to know

Auto-orient is enabled by default and realigns selected beam/rib local x axes. Disabling it preserves local axes. Torsion is released at most once per chain. Existing translational releases are preserved. Branched and closed-loop selections cannot always define a unique start/end.

[Detailed implementation notes](../addons/EndPinAssignAddon.md) ? [All tools](README.md) ? [Installation](../INSTALLATION.md)
