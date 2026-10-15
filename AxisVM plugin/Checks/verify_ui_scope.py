"""Verify the UI-only source boundary against the pre-refresh Git revision."""
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
BASE = sys.argv[1] if len(sys.argv) > 1 else 'be6a79103528f5f58adb4bc888d570d8c9414fd4'
SOURCE = 'AxisVM plugin/AxisVM plugin/'


def old_file(path):
    return subprocess.check_output(['git', 'show', BASE + ':' + path], cwd=ROOT).decode('utf-8-sig').replace('\r\n', '\n')


def without_layout(source, marker):
    """Mask strings/comments before locating the braces of just one permitted layout block."""
    tokens = re.compile(r'//[^\n]*|/\*[\s\S]*?\*/|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'')
    masked = list(source)
    for match in tokens.finditer(source):
        masked[match.start():match.end()] = ' ' * (match.end() - match.start())
    start = source.index(marker)
    end = source.index('{', start) + 1
    depth = 1
    while depth:
        if masked[end] == '{':
            depth += 1
        elif masked[end] == '}':
            depth -= 1
        end += 1
    return source[:start] + source[end:]


paths = subprocess.check_output(['git', 'ls-tree', '-r', '--name-only', BASE], cwd=ROOT).decode().splitlines()
protected = 0
for path in paths:
    if (path.startswith(SOURCE + 'Functions/') or path.startswith(SOURCE + 'Core/') or path.startswith('Rhino/')) and path.endswith('.cs'):
        assert old_file(path) == (ROOT / path).read_text(encoding='utf-8-sig'), 'Protected source changed: ' + path
        protected += 1
    elif path.startswith(SOURCE + 'UI/') and path.endswith('.cs'):
        marker = 'public OneWayPanelReportForm(' if path.endswith('OneWayPanelReportForm.cs') else 'private void InitializeComponent('
        before = without_layout(old_file(path), marker)
        after = without_layout((ROOT / path).read_text(encoding='utf-8-sig'), marker)
        if path.endswith('OneWayLoadPanelForm.cs'):
            for paint in ('private void PicPreview_Paint(', 'private void DrawLegend('):
                before, after = without_layout(before, paint), without_layout(after, paint)
        assert before.strip() == after.strip(), 'Non-layout UI source changed: ' + path
    elif path.startswith(SOURCE + 'Addons/') and path.endswith('.cs'):
        before, after = old_file(path), (ROOT / path).read_text(encoding='utf-8-sig')
        if before != after:
            normalize = lambda value: re.sub(r'\s+', '', re.sub(r'//[^\n]*|/\*[\s\S]*?\*/', '', value))
            marker = 'public byte[] Addon_IconBitmap'
            assert normalize(without_layout(before, marker)) == normalize(without_layout(after, marker)), 'Non-icon entry point changed: ' + path
print(f'PASS: {protected} protected core/function/Rhino source files unchanged; input/option handlers and non-icon entry points unchanged.')
