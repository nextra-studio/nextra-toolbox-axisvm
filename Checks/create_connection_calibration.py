"""Create a separate AxisVM cantilever calibration suite using installed COM API.

Requires comtypes. Never attaches to or changes the user's existing model.
Outputs an AXS model, specimen map, and numerical reference results beside it.
"""
from array import array
from datetime import datetime
from pathlib import Path
import csv
import json
import math
import time
import sys
import comtypes.client
import comtypes.gen.AxisVM as ax


def code(result):
    return result[-1] if isinstance(result, (tuple, list)) else result


def require(result, context):
    value = code(result)
    if value <= 0:
        raise RuntimeError(f"{context}: AxisVM returned {value}")
    return value


def cross(a, b):
    return [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]]


def main():
    if len(sys.argv) > 1:
        verify_existing(Path(sys.argv[1]).resolve())
        return
    output = Path(__file__).resolve().parent / "calibration" / datetime.now().strftime("%Y%m%d-%H%M%S")
    output.mkdir(parents=True, exist_ok=False)
    app = comtypes.client.CreateObject("AxisVM.AxisVMApplication")
    deadline = time.monotonic() + 45
    while not app.Loaded:
        if time.monotonic() > deadline:
            raise TimeoutError("AxisVM did not finish starting within 45 seconds")
        time.sleep(.25)
    app.CloseOnLastReleased = True
    app.AskCloseOnLastReleased = False
    app.AskSaveOnLastReleased = False
    app.Visible = True
    model_id = require(app.Models.New(), "new model")
    model = app.Models.Item[model_id]
    model.ProjectName = "Connection extractor calibration"
    model.Comment = "Five independent 3 m cantilevers; 12 unit-direction combinations; no self weight."
    names_result = app.Catalog.GetMaterialNamesByType(ax.ndcEuroCode, ax.mtSteel)
    steel_names = names_result[0]
    material_name = next((name for name in steel_names if name.replace(' ', '') == 'S235'), None)
    if material_name is None:
        raise RuntimeError(f"S235 not found in catalog: {steel_names}")
    material = require(model.Materials.AddFromCatalog(ax.ndcEuroCode, material_name), "material")
    section = require(model.CrossSections.AddRectangular("Calibration 200x400", .2, .4, ax.cspOther), "section")
    specimens = []
    definitions = [
        ("X forward", (3., 0., 0.), False, False),
        ("X reversed", (3., 0., 0.), True, False),
        ("30 degree beam", (3*math.cos(math.pi/6), 1.5, 0.), False, False),
        ("Vertical column", (0., 0., 3.), False, False),
        ("30 degree rib", (3*math.cos(math.pi/6), 1.5, 0.), False, True),
    ]
    for i, (name, r, reverse, rib) in enumerate(definitions):
        origin = (i*6., 0., 0.)
        base = require(model.Nodes.Add(*origin), "base node")
        tip = require(model.Nodes.Add(*(origin[j]+r[j] for j in range(3))), "tip node")
        start, end = (tip, base) if reverse else (base, tip)
        line = require(model.Lines.Add(start, end, ax.lgtStraightLine, ax.RLineGeomData()), "line")
        define = model.Lines.Item[line].DefineAsRib if rib else model.Lines.Item[line].DefineAsBeam
        require(define(material, section, section, ax.RPoint3d(), ax.RPoint3d()), "beam/rib")
        if model.Lines.Item[line].MemberID <= 0:
            require(model.Members.Add(array('i', [line])), "member")
        stiff = ax.RStiffnesses()
        for component, _ in stiff._fields_:
            setattr(stiff, component, 1.e12)
        support = require(model.NodalSupports.AddNodalGlobal(stiff, ax.RNonLinearity(), ax.RResistances(), base), "support")
        specimens.append(dict(name=name, line=line, base=base, tip=tip, support=support,
                              fixed_end="end" if reverse else "start", lever_arm=r))
    combinations = []
    for component, field in enumerate(("Fx", "Fy", "Fz", "Mx", "My", "Mz")):
        for sign in (1, -1):
            name = ("+" if sign > 0 else "-") + field
            case = require(model.LoadCases.Add(name, ax.lctStandard), "load case")
            vector = [0.] * 6
            vector[component] = sign * 10.
            for specimen in specimens:
                load = ax.RLoadNodalForce()
                load.LoadCaseId, load.NodeId = case, specimen['tip']
                for field_name, value in zip(("Fx", "Fy", "Fz", "Mx", "My", "Mz"), vector):
                    setattr(load, field_name, value)
                require(model.Loads.AddNodalForce(load), "nodal load")
            combo = require(model.LoadCombinations.Add(name, ax.ctOther, array('d', [1.]), array('i', [case])), "combination")
            combinations.append(dict(id=combo, name=name, tip_load=vector))
    model.FitInView()
    model.Refresh()
    path = output / "connection-calibration.axs"
    require(model.SaveToFile(str(path), False), "save geometry")
    print(f"Created: {path}", flush=True)
    analysis = model.Calculation.LinearAnalysis2(ax.cuiNoUserInteractionWithoutAutoCorrectNoShow, False)
    print(f"Linear analysis: {analysis}", flush=True)
    (output / "specimens.json").write_text(json.dumps(dict(specimens=specimens, combinations=combinations,
        analysis_status=str(analysis)), indent=2), encoding='utf-8')
    if code(analysis) < 0:
        raise RuntimeError(f"Linear analysis failed: {analysis}")
    rows = []
    for specimen in specimens:
        for combo in combinations:
            load = combo['tip_load']
            moment = cross(specimen['lever_arm'], load[:3])
            expected = load[:3] + [load[3+j]+moment[j] for j in range(3)]
            reaction_result = model.Results.Forces.GetNodalSupportForceByLoadCombinationId(
                specimen['support'], combo['id'], 0, ax.atLinearStatic, ax.RNodalSupportForceValues())
            reaction = reaction_result[0]
            actual = [getattr(reaction, field) for field in ('Rx','Ry','Rz','Rxx','Ryy','Rzz')]
            residual = max(abs(a-b) for a,b in zip(actual, expected))
            rows.append([specimen['name'], specimen['line'], specimen['base'], combo['id'], combo['name'],
                         *expected, *actual, residual, str(code(reaction_result))])
    with (output / 'reference-forces.csv').open('w', encoding='utf-8-sig', newline='') as f:
        writer = csv.writer(f, delimiter=';')
        writer.writerow(['specimen','line','fixed_node','combination_id','combination',
            *('expected_'+c for c in ('Fx','Fy','Fz','Mx','My','Mz')),
            *('api_support_force_'+c for c in ('Fx','Fy','Fz','Mx','My','Mz')), 'max_error', 'read_status'])
        writer.writerows(rows)
    require(model.SaveToFile(str(path), True), "save results")
    for specimen in specimens:
        model.Lines.Item[specimen['line']].Selected = True
        model.Nodes.Selected[specimen['base']] = True
    model.Refresh()
    app.BringToFront()
    app.CloseOnLastReleased = False
    print(f"Reference checks: {len(rows)}; maximum error {max(row[-2] for row in rows):.6g}", flush=True)


def verify_existing(path):
    """Open the generated model in a COM session and record both end forces."""
    app = comtypes.client.CreateObject('AxisVM.AxisVMApplication')
    deadline = time.monotonic() + 60
    while not app.Loaded:
        if time.monotonic() > deadline:
            raise TimeoutError('AxisVM startup timed out')
        time.sleep(.25)
    app.CloseOnLastReleased = True
    app.AskCloseOnLastReleased = False
    app.AskSaveOnLastReleased = False
    app.Visible = True
    model = app.Models.Item[require(app.Models.New(), 'new model')]
    require(model.LoadFromFile(str(path)), 'load calibration model')
    data = json.loads((path.parent / 'specimens.json').read_text(encoding='utf-8'))
    analysis = model.Calculation.LinearAnalysis2(ax.cuiNoUserInteractionWithoutAutoCorrectNoShow, False)
    if code(analysis) < 0:
        raise RuntimeError(str(analysis))
    rows, raw_rows = [], []
    for s in data['specimens']:
        member = model.Lines.Item[s['line']].MemberID
        matrix = model.Members.Item[member].GetTrMatrix(ax.RMatrix3x3())[0]
        matrix_rows = [[getattr(matrix, f'e{i}{j}') for j in range(1,4)] for i in range(1,4)]
        for c in data['combinations']:
            load = c['tip_load']
            moment = cross(s['lever_arm'], load[:3])
            expected = load[:3] + [load[3+j]+moment[j] for j in range(3)]
            result = model.Results.Forces.GetNodalSupportForceByLoadCombinationId(
                s['support'], c['id'], 0, ax.atLinearStatic, ax.RNodalSupportForceValues())
            if code(result) <= 0:
                raise RuntimeError(f'Support force unavailable: {result}')
            actual = [getattr(result[0], f) for f in ('Rx','Ry','Rz','Rxx','Ryy','Rzz')]
            error = max(abs(a-b) for a,b in zip(actual, expected))
            rows.append([s['name'],s['line'],s['base'],c['id'],c['name'],*expected,*actual,error,code(result)])
            forces, positions, status = model.Results.Forces.GetLineForcesByLoadCombinationId(
                s['line'], c['id'], 0, ax.atLinearStatic)
            if status <= 0:
                raise RuntimeError(f'Line forces unavailable: {status}')
            endpoints = sorted(zip(positions,forces), key=lambda p:p[0])
            raw_rows.append(dict(specimen=s['name'],line=s['line'],fixed_end=s['fixed_end'],
                combination=c['name'],matrix_rows=matrix_rows,expected_on_fixed_node=expected,
                api_support_force=actual,ends=[dict(x=p,raw=[getattr(f,n) for n in
                ('lfvNx','lfvVy','lfvVz','lfvTx','lfvMy','lfvMz')]) for p,f in (endpoints[0],endpoints[-1])]))
        model.Lines.Item[s['line']].Selected = True
        model.Nodes.Selected[s['base']] = True
    with (path.parent / 'reference-forces.csv').open('w',encoding='utf-8-sig',newline='') as f:
        writer=csv.writer(f,delimiter=';')
        writer.writerow(['specimen','line','fixed_node','combination_id','combination',
            *('expected_'+c for c in ('Fx','Fy','Fz','Mx','My','Mz')),
            *('api_support_force_'+c for c in ('Fx','Fy','Fz','Mx','My','Mz')),'max_error','read_status'])
        writer.writerows(rows)
    (path.parent / 'raw-end-forces.json').write_text(json.dumps(raw_rows,indent=2),encoding='utf-8')
    verified_path = path.with_name('connection-calibration-verified.axs')
    require(model.SaveToFile(str(verified_path), True), 'save calculated model')
    model.FitInView()
    model.Refresh()
    app.BringToFront()
    app.CloseOnLastReleased = False
    print(f'Verified {len(rows)} combinations/specimens. Maximum support-force error: {max(r[-2] for r in rows):.8g}',flush=True)
    print(f'Open model: {verified_path}',flush=True)


if __name__ == '__main__':
    main()
