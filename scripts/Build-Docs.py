"""Regenerates the tool lists in the docs from tools.json and refreshes the install pictures.

Usage:  python scripts/Build-Docs.py

Regions between  <!-- gen:NAME -->  and  <!-- /gen:NAME -->  are rewritten:
  tools-root   README.md                      table with icon, link, summary and status
  tools-guide  docs/user-guide/README.md      table with icon, link and summary
  dll-list     docs/INSTALLATION.md, README.md  bullet list of the DLLs to copy
A tool that has no guide page yet gets a stub page. Everything else in the docs is hand-written.
"""
import json
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
PLUGIN = ROOT / "AxisVM plugin"
DOCS = PLUGIN / "docs"
tools = json.loads((ROOT / "tools.json").read_text(encoding="utf-8"))["tools"]


def name(t):
    return t["title"] + (" (WIP)" if t["status"] == "wip" else "")


def root_table():
    rows = ["| | Tool | What it does | Status |", "|---|---|---|---|"]
    for t in tools:
        rows.append("| ![%s](AxisVM%%20plugin/docs/images/icons/%s@4x.png) | [%s](AxisVM%%20plugin/docs/user-guide/%s) | %s | %s |"
                    % (t["title"], t["id"], t["title"], t["guide"], t["summary"], "**WIP**" if t["status"] == "wip" else "Released"))
    return "\n".join(rows)


def guide_table():
    rows = ["| | Tool | Use it for |", "|---|---|---|"]
    for t in tools:
        rows.append("| ![%s](../images/icons/%s@4x.png) | [%s](%s) | %s |" % (t["title"], t["id"], name(t), t["guide"], t["summary"]))
    return "\n".join(rows)


def dll_list():
    return "\n".join("- `%s`" % t["dll"] for t in tools)


def inject(path, region, text):
    s = path.read_text(encoding="utf-8")
    pat = re.compile(r"(<!-- gen:%s -->)(.*?)(<!-- /gen:%s -->)" % (region, region), re.S)
    if not pat.search(s):
        return
    path.write_text(pat.sub(lambda m: m.group(1) + "\n" + text + "\n" + m.group(3), s), encoding="utf-8", newline="\n")
    print("updated", region, "in", path.relative_to(ROOT))


inject(ROOT / "README.md", "tools-root", root_table())
inject(DOCS / "user-guide" / "README.md", "tools-guide", guide_table())
for p in (ROOT / "README.md", DOCS / "INSTALLATION.md"):
    inject(p, "dll-list", dll_list())

for t in tools:
    page = DOCS / "user-guide" / t["guide"]
    if not page.exists():
        page.write_text(
            "# %s\n\n![%s toolbar icon](../images/icons/%s.png) %s\n\n## Steps\n\n1. Select the objects in AxisVM.\n"
            "2. Open %s.\n3. TODO: describe the dialog and result.\n\n[All tools](README.md) | [Installation](../INSTALLATION.md)\n"
            % (t["title"], t["title"], t["id"], t["summary"], t["menu"]), encoding="utf-8")
        print("created stub guide", page.name)

subprocess.run([sys.executable, str(ROOT / "scripts" / "make_install_svgs.py")], check=True)
