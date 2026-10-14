"""Live PyAxisVM calibration against independent cantilever statics.

Run with .venv-axisvm/Scripts/python.exe. Creates its own COM session.
Global load units: kN and kNm; geometry: m. Local frames are derived from
member azimuth/elevation independently of AxisVM's transformation matrix.
"""
from array import array
from datetime import datetime
from importlib.metadata import version
from pathlib import Path
import csv
import json
import math
import time

import comtypes.client
from axisvm.com.axapp import IAxisVMApplication
import axisvm.com.tlb as ax

FIELDS = ('Fx', 'Fy', 'Fz', 'Mx', 'My', 'Mz')
RAW_FIELDS = ('lfvNx', 'lfvVy', 'lfvVz', 'lfvTx', 'lfvMy', 'lfvMz')
TOL = 0.001


def code(result):
    return result[-1] if isinstance(result, (tuple, list)) else result


def require(result, context):
    if code(result) <= 0:
        raise RuntimeError(f'{context}: {result}')
    return code(result)


def cross(a, b):
    return [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]]


def angular_vector(magnitude, azimuth, elevation):
    a, e = map(math.radians, (azimuth, elevation))
    return [magnitude*math.cos(e)*math.cos(a), magnitude*math.cos(e)*math.sin(a), magnitude*math.sin(e)]


def analytic_axes(azimuth, elevation, reverse=False):
    a, e = map(math.radians, (azimuth, elevation))
    if elevation == 90:
        axes = [[0., 0., 1.], [0., -1., 0.], [1., 0., 0.]]
    else:
        axes = [[math.cos(e)*math.cos(a), math.cos(e)*math.sin(a), math.sin(e)],
                [-math.sin(a), math.cos(a), 0.],
                [-math.sin(e)*math.cos(a), -math.sin(e)*math.sin(a), math.cos(e)]]
    if reverse:
        axes[0] = [-v for v in axes[0]]
        axes[1] = [-v for v in axes[1]]
    return axes


def decompose(vector, axes):
    return [sum(vector[3*k+j]*axis[j] for j in range(3)) for k in range(2) for axis in axes]


def compose(local, axes):
    return [sum(local[3*k+i]*axes[i][j] for i in range(3)) for k in range(2) for j in range(3)]


def maximum_error(a, b):
    return max(abs(x-y) for x, y in zip(a, b))


def main():
    output = Path(__file__).resolve().parent / 'calibration' / ('pyaxisvm-' + datetime.now().strftime('%Y%m%d-%H%M%S'))
    output.mkdir(parents=True, exist_ok=False)
    print(f'PyAxisVM {version("axisvm")}; output: {output}', flush=True)
    # CreateObject guarantees a separate session; wrapping is the PyAxisVM API.
    # start_AxisVM(join=False) can quit an existing registered session, so avoid it.
    raw_app = comtypes.client.CreateObject('AxisVM.AxisVMApplication')
    deadline = time.monotonic()+90
    while not raw_app.Loaded:
        if time.monotonic() > deadline:
            raise TimeoutError('AxisVM did not complete startup')
        time.sleep(.25)
    raw_app.CloseOnLastReleased = True
    raw_app.AskCloseOnLastReleased = False
    raw_app.AskSaveOnLastReleased = False
    raw_app.Visible = True
    app = IAxisVMApplication(wrap=raw_app)
    model = app.new_model()
    model.ProjectName = 'PyAxisVM oblique force and moment calibration'
    model.Comment = 'Independent 3 m cantilevers; global nodal loads; no self-weight. Springs 1e8.'
    material = require(model.Materials.AddFromCatalog(ax.ndcEuroCode, 'S 235'), 'material')
    section = require(model.CrossSections.AddRectangular('Calibration 200x400', .2, .4, ax.cspOther), 'section')
    definitions = [
        ('X forward', 0, 0, False, False), ('X reversed', 0, 0, True, False),
        ('Plan 30 beam', 30, 0, False, False), ('Plan 75 beam', 75, 0, False, False),
        ('Plan -40 beam', -40, 0, False, False), ('Vertical column', 0, 90, False, False),
        ('Plan 30 rib', 30, 0, False, True), ('3D az30 el20', 30, 20, False, False),
        ('3D az30 el20 reversed', 30, 20, True, False), ('3D az-40 el35', -40, 35, False, False),
    ]
    specimens = []
    for index, (name, azimuth, elevation, reverse, rib) in enumerate(definitions):
        r = angular_vector(3., azimuth, elevation)
        base = require(model.Nodes.Add(index*6., 0., 0.), 'base node')
        tip = require(model.Nodes.Add(index*6.+r[0], r[1], r[2]), 'tip node')
        start, end = (tip, base) if reverse else (base, tip)
        line = require(model.Lines.Add(start, end, ax.lgtStraightLine, ax.RLineGeomData()), 'line')
        member_line = model.Lines[line]  # PyAxisVM collection syntax
        define = member_line.DefineAsRib if rib else member_line.DefineAsBeam
        require(define(material, section, section, ax.RPoint3d(), ax.RPoint3d()), 'beam/rib')
        if member_line.MemberID <= 0:
            require(model.Members.Add(array('i', [line])), 'member')
        stiffness = ax.RStiffnesses()
        for field, _ in stiffness._fields_:
            setattr(stiffness, field, 1.e8)
        support = require(model.NodalSupports.AddNodalGlobal(stiffness, ax.RNonLinearity(), ax.RResistances(), base), 'support')
        matrix, status = model.Members[member_line.MemberID].GetTrMatrix(ax.RMatrix3x3())
        if status < 0:
            raise RuntimeError(f'Matrix unavailable: {status}')
        axes = [[getattr(matrix, f'e{i}{j}') for j in range(1,4)] for i in range(1,4)]
        theoretical_axes = analytic_axes(azimuth, elevation, reverse)
        frame_error = maximum_error(sum(axes, []), sum(theoretical_axes, []))
        if frame_error > 1.e-8:
            raise AssertionError(f'{name}: AxisVM frame differs from analytical default frame: {axes}; expected {theoretical_axes}')
        specimens.append(dict(name=name,line=line,base=base,tip=tip,support=support,
            reversed=reverse,lever_arm=r,matrix_rows=axes,theoretical_axes=theoretical_axes,frame_error=frame_error))
    loads = []
    for i, component in enumerate(FIELDS):
        for sign in (1, -1):
            v = [0.]*6
            v[i] = sign*10.
            loads.append((('+' if sign > 0 else '-')+component, v))
    for a,e in ((15,25),(-55,-20),(120,40)):
        loads.append((f'Force az{a} el{e}',angular_vector(10.,a,e)+[0.]*3))
    for a,e in ((-35,20),(65,-30),(140,45)):
        loads.append((f'Moment az{a} el{e}',[0.]*3+angular_vector(7.,a,e)))
    loads += [('Mixed A',[3.,-4.,5.,2.,-3.,4.]), ('Mixed B',[-6.,2.,-3.,-4.,5.,-2.])]
    combinations = []
    for name, vector in loads:
        case = require(model.LoadCases.Add(name, ax.lctStandard), 'load case')
        for s in specimens:
            record = ax.RLoadNodalForce()
            record.LoadCaseId, record.NodeId = case, s['tip']
            for field,value in zip(FIELDS,vector):
                setattr(record,field,value)
            require(model.Loads.AddNodalForce(record), 'nodal load')
        combo = require(model.LoadCombinations.Add(name, ax.ctOther, array('d',[1.]), array('i',[case])), 'combination')
        combinations.append(dict(id=combo,name=name,tip_load=vector))
    print(f'Built {len(specimens)} specimens and {len(combinations)} combinations via PyAxisVM.',flush=True)
    path = output / 'pyaxisvm-oblique-calibration.axs'
    require(model.SaveToFile(str(path), False), 'save model')
    status = model.Calculation.LinearAnalysis(interact=False,show=False,autocorrect=False)
    if status <= 0:
        raise RuntimeError(f'Analysis failed: {status}')
    print(f'PyAxisVM LinearAnalysis passed: {status}',flush=True)
    rows, raw_rows, supports = [], [], []
    for s in specimens:
        for c in combinations:
            force, moment = c['tip_load'][:3], c['tip_load'][3:]
            arm_moment = cross(s['lever_arm'],force)
            fixed_vector = force + [moment[j]+arm_moment[j] for j in range(3)]
            tip_vector = [-v for v in c['tip_load']]
            result = model.Results.Forces.GetNodalSupportForceByLoadCombinationId(s['support'],c['id'],0,ax.atLinearStatic,ax.RNodalSupportForceValues())
            if code(result) <= 0:
                raise RuntimeError(f'Support read failed: {result}')
            support_vector = [getattr(result[0], f) for f in ('Rx','Ry','Rz','Rxx','Ryy','Rzz')]
            supports.append(dict(specimen=s['name'],combination=c['name'],expected=fixed_vector,
                api_support_force=support_vector,max_error=maximum_error(support_vector,fixed_vector)))
            forces, positions, rc = model.Results.Forces.GetLineForcesByLoadCombinationId(s['line'],c['id'],0,ax.atLinearStatic)
            if rc <= 0:
                raise RuntimeError(f'Line force read failed: {rc}')
            sorted_ends = sorted(zip(positions,forces),key=lambda pair:pair[0])
            endpoints = [dict(x=p,raw=[getattr(f,n) for n in RAW_FIELDS]) for p,f in (sorted_ends[0],sorted_ends[-1])]
            for end_index, end_name in enumerate(('start','end')):
                is_fixed = (end_index == 1) if s['reversed'] else (end_index == 0)
                expected = fixed_vector if is_fixed else tip_vector
                local = [v*(1 if end_index==0 else -1) for v in endpoints[end_index]['raw']]
                expected_local = decompose(expected,s['theoretical_axes'])
                global_vector = compose(local,s['matrix_rows'])
                back_to_local = decompose(global_vector,s['matrix_rows'])
                errors = [maximum_error(local,expected_local),maximum_error(global_vector,expected),maximum_error(back_to_local,local)]
                rows.append([s['name'],c['name'],end_name,'fixed' if is_fixed else 'tip',*expected,*global_vector,*expected_local,*local,*errors])
                raw_rows.append(dict(specimen=s['name'],line=s['line'],fixed_end='end' if s['reversed'] else 'start',
                    node_end=end_name,combination=c['name'],matrix_rows=s['matrix_rows'],
                    expected_on_fixed_node=expected,ends=endpoints))
        model.Lines[s['line']].Selected = True
        model.Nodes.Selected[s['base']] = True
        print(f'Checked {s["name"]}: {len(combinations)*2} end vectors',flush=True)
    header = ['specimen','combination','end','node_role',
        *('expected_global_'+c for c in FIELDS),*('actual_global_'+c for c in FIELDS),
        *('expected_local_'+c for c in ('N','Vy','Vz','Tx','My','Mz')),
        *('actual_local_'+c for c in ('N','Vy','Vz','Tx','My','Mz')),
        'decomposition_error','composition_error','roundtrip_error']
    with (output/'force-moment-checks.csv').open('w',encoding='utf-8-sig',newline='') as f:
        writer=csv.writer(f,delimiter=';'); writer.writerow(header); writer.writerows(rows)
    for filename,data in [('raw-end-forces.json',raw_rows),('support-checks.json',supports),
                          ('specimens.json',dict(specimens=specimens,combinations=combinations))]:
        (output/filename).write_text(json.dumps(data,indent=2),encoding='utf-8')
    summary = dict(pyaxisvm_version=version('axisvm'),specimens=len(specimens),combinations=len(combinations),
        checked_end_vectors=len(rows),tolerance=TOL,units='kN, kNm',
        max_frame_error=max(s['frame_error'] for s in specimens),
        max_decomposition_error=max(r[-3] for r in rows),max_composition_error=max(r[-2] for r in rows),
        max_roundtrip_error=max(r[-1] for r in rows),max_support_force_error=max(r['max_error'] for r in supports),
        failures=sum(max(r[-3:])>TOL for r in rows)+sum(r['max_error']>TOL for r in supports))
    (output/'summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
    # Save to a fresh filename: preserve the pre-analysis model too.
    calculated_path=path.with_name('pyaxisvm-oblique-calibration-calculated.axs')
    require(model.SaveToFile(str(calculated_path), True), 'save calculated results')
    model.FitInView(); model.Refresh(); app.BringToFront()
    raw_app.CloseOnLastReleased = False
    print(json.dumps(summary,indent=2),flush=True)
    print(f'Saved calculated model: {calculated_path}',flush=True)
    if summary['failures']:
        raise AssertionError('Calibration failed; inspect CSV and JSON outputs')


if __name__ == '__main__':
    main()
