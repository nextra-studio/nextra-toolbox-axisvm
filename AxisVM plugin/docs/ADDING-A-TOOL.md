# Adding a tool

Everything that lists tools (installer, release ZIP, README, guide index, install pictures) reads [`tools.json`](../../tools.json). Adding a tool is one registry entry plus the code.

## 1. Write the add-on

Follow [HOW-TO-BUILD.md](HOW-TO-BUILD.md) section 3: new `Addons/<Id>Addon.cs`, `Functions/`, `UI/`, a `<Id>Addon.csproj`, and an entry in `AxisVM plugin.slnx`. Start the menu text as `N. <Name>   (WIP)` while the tool is unvalidated; remove `(WIP)` when it is released.

## 2. Add the icon

Add the drawing to `Core/AddonIcons.cs` and the id to the `names` array in `Checks/UiPreview.cs` (`Icons()`), then run `.\scripts\Preview-UI.ps1`. It writes one transparent PNG per tool: `docs/images/<Id>-icon.png`, `docs/images/icons/<Id>.png` (16 px) and `docs/images/icons/<Id>@4x.png` (64 px). There is no combined icon sheet, so nothing else changes.

## 3. Register it

Add an entry to `tools.json`:

```json
{ "id": "MyTool", "menu": "N. My Tool   (WIP)", "title": "My tool", "dll": "MyToolAddon.dll",
  "guide": "my-tool.md", "status": "wip", "summary": "One sentence for the tables." }
```

`status` is `wip` or `released`. Then run:

```powershell
python scripts/Build-Docs.py
```

This rewrites the tool tables and DLL lists, regenerates the install pictures (the AxisVM menu picture gains the new icon), and creates a stub `docs/user-guide/my-tool.md` if it is missing. Fill the stub with steps and a dialog screenshot.

## 4. Release

```powershell
.\scripts\New-Release.ps1                       # builds dist\nextra-axisvm-toolbox.zip
gh release create v1.1.0 dist\nextra-axisvm-toolbox.zip --title "v1.1.0" --notes "Adds My Tool"
```

Keep the asset name `nextra-axisvm-toolbox.zip`: the README links to `releases/latest/download/`. Users then download the ZIP and run `Install-Addons.ps1`, which installs every tool in the bundled `tools.json`.

## 5. Rhino plug-in

If the Rhino side changed, raise `version` in `Rhino/yak/manifest.yml`, run `.\scripts\Build-YakPackage.ps1`, then `yak login` and `yak push` the `.yak` from `Rhino\yak\out`.
