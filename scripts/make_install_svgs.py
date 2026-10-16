"""Generates the installation pictures in AxisVM plugin/docs/images/install/.

Run from anywhere:  python scripts/make_install_svgs.py
The "tools in the AxisVM menu" and "DLL list" pictures are built from tools.json,
so a newly registered tool appears in them automatically.
"""
import base64
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "AxisVM plugin" / "docs" / "images" / "install"
ICONS = ROOT / "AxisVM plugin" / "docs" / "images" / "icons"
TOOLS = json.loads((ROOT / "tools.json").read_text(encoding="utf-8"))

CSS = """
.bg{fill:#f6f8fa}.win{fill:#fff;stroke:#d0d7de;stroke-width:1.5}.bar{fill:#eaeef2}
.t{font:600 15px Segoe UI,Helvetica,Arial,sans-serif;fill:#1f2328}
.s{font:13px Segoe UI,Helvetica,Arial,sans-serif;fill:#57606a}
.h{font:700 18px Segoe UI,Helvetica,Arial,sans-serif;fill:#1f2328}
.m{font:13px Consolas,Menlo,monospace;fill:#1f2328}
.f{fill:#fff;stroke:#d0d7de}.sel{fill:#ddf4ff}.card{fill:#fff;stroke:#d0d7de;stroke-width:1.5}
.btn{fill:#1f883d}.bt{font:600 13px Segoe UI,Helvetica,Arial,sans-serif;fill:#fff}
.blue{fill:#0066b4}.orange{fill:#d86712}
.ring{fill:none;stroke:#d1242f;stroke-width:3;stroke-dasharray:6 4}
.badge{fill:#d1242f}.bn{font:700 13px Segoe UI,Arial,sans-serif;fill:#fff}
.arrow{fill:none;stroke:#0066b4;stroke-width:3;stroke-linecap:round;stroke-linejoin:round}
.term{fill:#0d1117}.tt{font:13px Consolas,Menlo,monospace;fill:#e6edf3}
.tg{font:13px Consolas,Menlo,monospace;fill:#3fb950}.tp{font:13px Consolas,Menlo,monospace;fill:#8b949e}
@media (prefers-color-scheme: dark){
.bg{fill:#0d1117}.win,.card{fill:#161b22;stroke:#3d444d}.bar{fill:#21262d}
.t,.m,.h{fill:#e6edf3}.s{fill:#9da7b3}.f{fill:#0d1117;stroke:#3d444d}.sel{fill:#1f3a5f}
.blue{fill:#4da3ff}.arrow{stroke:#4da3ff}}
"""


def esc(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def svg(name, w, h, body):
    OUT.mkdir(parents=True, exist_ok=True)
    text = ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %d %d" width="%d" height="%d">'
            '<style>%s</style><rect class="bg" width="%d" height="%d" rx="12"/>%s</svg>' % (w, h, w, h, CSS, w, h, body))
    (OUT / name).write_text(text, encoding="utf-8")


def window(x, y, w, h, title):
    return ('<rect class="win" x="%d" y="%d" width="%d" height="%d" rx="10"/>'
            '<path class="bar" d="M%d %dV%da9 9 0 0 1 9-9h%da9 9 0 0 1 9 9v24z"/>'
            '<circle cx="%d" cy="%d" r="5" fill="#ff5f57"/><circle cx="%d" cy="%d" r="5" fill="#febc2e"/>'
            '<circle cx="%d" cy="%d" r="5" fill="#28c840"/>'
            '<text class="s" x="%d" y="%d" text-anchor="middle">%s</text>'
            % (x, y, w, h, x + 1, y + 34, y + 10, w - 20, x + 18, y + 17, x + 34, y + 17, x + 50, y + 17,
               x + w / 2, y + 22, esc(title)))


def badge(x, y, n):
    return '<circle class="badge" cx="%d" cy="%d" r="11"/><text class="bn" x="%d" y="%d" text-anchor="middle">%d</text>' % (x, y, x, y + 4.5, n)


def ring(x, y, w, h):
    return '<rect class="ring" x="%d" y="%d" width="%d" height="%d" rx="9"/>' % (x, y, w, h)


def button(x, y, w, label):
    return '<rect class="btn" x="%d" y="%d" width="%d" height="32" rx="6"/><text class="bt" x="%s" y="%d" text-anchor="middle">%s</text>' % (x, y, w, x + w / 2, y + 21, esc(label))


def field(x, y, w, text):
    return '<rect class="f" x="%d" y="%d" width="%d" height="30" rx="6"/><text class="m" x="%d" y="%d">%s</text>' % (x, y, w, x + 10, y + 20, esc(text))


def icon_uri(tool_id):
    p = ICONS / (tool_id + "@4x.png")
    return "data:image/png;base64," + base64.b64encode(p.read_bytes()).decode("ascii")


def arrow(x1, y1, x2, y2):
    return ('<path class="arrow" d="M%d %dL%d %d"/><path class="arrow" d="M%d %dl-10 -7M%d %dl-10 7"/>'
            % (x1, y1, x2, y2, x2, y2, x2, y2))


tools = TOOLS["tools"]
rhino = TOOLS["rhino"]["name"]

# 1. Overview: three steps ------------------------------------------------------------------------------
cards = [
    ("1", "Download", "Get the toolbox ZIP", "from GitHub Releases"),
    ("2", "Install add-ons", "Run Install-Addons.ps1", "or copy the DLLs by hand"),
    ("3", "Install in Rhino", "Package Manager:", rhino),
]
body = '<text class="h" x="360" y="38" text-anchor="middle">Install in three steps</text>'
for i, (n, title, l1, l2) in enumerate(cards):
    x = 24 + i * 236
    body += '<rect class="card" x="%d" y="62" width="212" height="150" rx="12"/>' % x
    body += '<circle class="blue" cx="%d" cy="96" r="17"/><text class="bn" x="%d" y="102" text-anchor="middle" style="font-size:16px">%s</text>' % (x + 36, x + 36, n)
    body += '<text class="t" x="%d" y="102">%s</text>' % (x + 62, title)
    body += '<text class="s" x="%d" y="146">%s</text><text class="s" x="%d" y="166">%s</text>' % (x + 22, esc(l1), x + 22, esc(l2))
    if i < 2:
        body += arrow(x + 214, 137, x + 232, 137)
body += '<text class="s" x="360" y="244" text-anchor="middle">Then start AxisVM and Rhino. The N. tools appear in the AxisVM add-ons menu.</text>'
svg("overview.svg", 720, 262, body)

# 2. GitHub release download ------------------------------------------------------------------------------
body = window(20, 16, 680, 232, "GitHub - Releases")
body += '<text class="t" x="44" y="76">Nextra toolbox for AxisVM</text><text class="s" x="44" y="96">Latest release</text>'
body += '<text class="s" x="44" y="124">Assets</text>'
body += '<rect class="f" x="44" y="132" width="632" height="40" rx="8"/><rect class="sel" x="45" y="133" width="630" height="38" rx="7"/>'
body += '<text class="m" x="60" y="157">nextra-axisvm-toolbox.zip</text><text class="s" x="560" y="157">download</text>'
body += ring(40, 128, 640, 48) + badge(690, 126, 1)
body += '<rect class="f" x="44" y="180" width="632" height="36" rx="8"/><text class="m" x="60" y="203">Source code (zip)</text><text class="s" x="440" y="203">not needed</text>'
svg("download-release.svg", 720, 264, body)

# 3. PowerShell script ------------------------------------------------------------------------------------
lines = ['<tspan class="tp">PS C:\\Users\\you\\Downloads\\nextra-axisvm-toolbox&gt;</tspan> <tspan class="tt">.\\Install-Addons.ps1</tspan>']
lines += ['<tspan class="tg">Installed %s</tspan>' % t["dll"] for t in tools]
lines += ['<tspan class="tp">Previous versions: %LOCALAPPDATA%\\NextraToolbox\\backups\\...</tspan>',
          '<tspan class="tt">Restart AxisVM to load the updated dialogs and icons.</tspan>']
h = 60 + 20 * len(lines)
body = '<rect class="term" x="20" y="16" width="680" height="%d" rx="10"/>' % (h - 20)
for i, l in enumerate(lines):
    body += '<text x="40" y="%d">%s</text>' % (48 + 20 * i, l)
body += badge(690, 22, 1)
body += '<text class="s" x="360" y="%d" text-anchor="middle">Close AxisVM first. Other AxisVM folder: add  -AddOnsDir "D:\\AxisVM X8\\AddOns"</text>' % (h + 16)
svg("run-script.svg", 720, h + 30, body)

# 4. Manual copy -----------------------------------------------------------------------------------------
n = len(tools)
rowh = 22
wh = 80 + rowh * n
body = window(20, 16, 320, wh, "Toolbox ZIP  \\  AddOns")
body += window(380, 16, 320, wh, "C:\\AXISVM X8\\AddOns")
for i, t in enumerate(tools):
    y = 64 + i * rowh
    body += '<rect class="sel" x="34" y="%d" width="292" height="20" rx="4"/><text class="m" x="44" y="%d">%s</text>' % (y - 14, y, esc(t["dll"]))
    body += '<text class="m" x="394" y="%d">%s</text>' % (y, esc(t["dll"]))
body += arrow(332, 40 + wh // 2, 372, 40 + wh // 2)
body += ring(30, 46, 304, rowh * n + 12) + badge(338, 46, 1) + badge(692, 46, 2)
body += '<text class="s" x="360" y="%d" text-anchor="middle">1 select the DLLs, copy    2 paste into the AxisVM AddOns folder and replace older files</text>' % (wh + 44)
svg("manual-copy.svg", 720, wh + 60, body)

# 5. Unblock ---------------------------------------------------------------------------------------------
body = window(160, 16, 400, 214, "RhinoImportAddon.dll Properties")
body += '<text class="s" x="184" y="76">Type of file:   Application extension (.dll)</text>'
body += '<text class="s" x="184" y="98">Location:       C:\\Users\\you\\Downloads</text>'
body += '<text class="s" x="184" y="136">Security:  This file came from another computer</text>'
body += '<text class="s" x="184" y="154">and might be blocked.</text>'
body += '<rect class="f" x="450" y="164" width="16" height="16" rx="3"/><path d="M453 172l4 4 7-8" fill="none" stroke="#1f883d" stroke-width="2.5"/>'
body += '<text class="t" x="474" y="177">Unblock</text>' + ring(442, 156, 100, 32) + badge(552, 154, 1)
body += button(420, 192, 60, "OK") + badge(490, 190, 2)
body += '<text class="s" x="360" y="252" text-anchor="middle">Only needed if AxisVM or Rhino refuses to load a downloaded file. Repeat for every file.</text>'
svg("unblock.svg", 720, 270, body)

# 6. Rhino package manager --------------------------------------------------------------------------------
body = window(20, 16, 680, 218, "Rhino - Package Manager")
body += '<text class="s" x="44" y="72">Search</text>' + field(44, 78, 300, rhino) + badge(352, 98, 1)
body += '<rect class="f" x="44" y="128" width="632" height="76" rx="8"/>'
body += '<rect x="60" y="142" width="48" height="48" rx="10" fill="#0066b4"/><path d="M73 180v-28l22 22v-22" fill="none" stroke="#fff" stroke-width="5" stroke-linecap="round" stroke-linejoin="round"/>'
body += '<text class="t" x="124" y="162">%s</text><text class="s" x="124" y="184">by Tatsuki - Rhino companion of the Nextra AxisVM toolbox</text>' % esc(rhino)
body += button(560, 150, 96, "Install") + ring(554, 144, 108, 44) + badge(660, 142, 2)
svg("rhino-package-manager.svg", 720, 250, body)

# 7. Rhino manual install -----------------------------------------------------------------------------------
body = window(20, 16, 680, 232, "Rhino Options - Plug-ins")
body += '<rect class="f" x="44" y="62" width="200" height="170" rx="6"/>'
for i, name in enumerate(["Appearance", "Files", "Plug-ins", "Toolbars", "View"]):
    y = 82 + i * 28
    if name == "Plug-ins":
        body += '<rect class="sel" x="46" y="%d" width="196" height="26" rx="4"/>' % (y - 18)
    body += '<text class="m" x="58" y="%d">%s</text>' % (y, name)
body += badge(236, 90 + 2 * 28 - 18, 1)
body += button(268, 74, 140, "Install...") + ring(262, 68, 152, 44) + badge(416, 66, 2)
body += '<rect class="f" x="268" y="126" width="408" height="30" rx="6"/><text class="m" x="278" y="146">Select AxisBridge.rhp</text>' + badge(684, 126, 3)
body += '<text class="s" x="268" y="190">Keep AxisBridge.rhp and Newtonsoft.Json.dll in the same folder,</text>'
body += '<text class="s" x="268" y="210">then restart Rhino. (Or drag AxisBridge.rhp onto the Rhino window.)</text>'
svg("rhino-manual.svg", 720, 264, body)

# 8. Result: the tools in AxisVM ----------------------------------------------------------------------------
rows = len(tools)
h = 96 + rows * 34
body = window(20, 16, 680, h - 20, "AxisVM X8 - Add-ons")
body += '<rect class="f" x="44" y="60" width="300" height="%d" rx="6"/>' % (rows * 34 + 12)
for i, t in enumerate(tools):
    y = 66 + i * 34
    body += '<image href="%s" x="54" y="%d" width="28" height="28"/>' % (icon_uri(t["id"]), y)
    label = t["menu"] + ("  (WIP)" if t.get("status") == "wip" else "")
    body += '<text class="m" x="94" y="%d">%s</text>' % (y + 19, esc(label))
body += '<text class="t" x="372" y="84">Seven tools - one icon each</text>'
body += '<text class="s" x="372" y="108">Select objects in AxisVM first,</text><text class="s" x="372" y="128">then click a tool.</text>'
body += '<text class="s" x="372" y="164">Not visible? Check the DLL is in the</text><text class="s" x="372" y="184">AddOns folder and restart AxisVM.</text>'
svg("axisvm-tools.svg", 720, h, body.replace("Seven tools", "%d tools" % rows))

print("Wrote", len(list(OUT.glob("*.svg"))), "pictures to", OUT)
