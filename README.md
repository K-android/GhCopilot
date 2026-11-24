# GH Copilot

A standalone hybrid **Rhino 8 + Grasshopper + Rhino.Inside.Revit** AI copilot (`.rhp` + `.gha` + `.Core`) with BYOK multi-provider support (**Google Gemini**, **Anthropic Claude**, and **OpenAI**), **Direct Rhino 8 3D Mode** (no Grasshopper required), a **WhatsApp-style Plan-First chat UI**, **live multimodal Rhino viewport vision**, **component-first Grasshopper & third-party plugin wiring** (up to 200 nodes), and first-class **Rhino.Inside.Revit** support.

---

## Key Capabilities

### 1. Direct Rhino 8 Mode (No Grasshopper Required)
Create, deform, boolean, organize, and render native Rhino 8 geometry directly via `RhinoDoc` in a single native Rhino `Ctrl+Z` undo transaction (up to **250 operations per turn** across **49 operations**):
- **Curves & Profiles**: `add_point`, `add_line`, `add_arc` (3-point or center/radius/sweep), `add_circle`, `add_ellipse`, `add_polygon` (3–128 sides), `add_rectangle`, `add_polyline` (polylines & smooth NURBS curves), `add_helix` (cylindrical & conical spirals), `offset_curve`.
- **Surfaces, Solids, Sweeps & Shells**: `add_box`, `add_sphere`, `add_cylinder`, `add_cone`, `add_torus`, `extrude`, `loft`, `revolve`, `sweep1`, `pipe` (uniform or tapered), `planar_srf` (with inner holes), `cap_planar_holes`, `offset_surface`.
- **Booleans, Fillets & Contour Slicing**: `boolean_union`, `boolean_difference`, `boolean_intersection`, `fillet_edges` (fillet or chamfer), `contour_slice` (contour curves or extruded floor slabs/ribs).
- **Transforms, Space Morphs & Arrays**: `move`, `rotate`, `scale`, `mirror`, `twist`, `taper`, `copy`, `array_linear`, `array_polar` (up to 500 instances), `delete`.
- **Layers, Attributes & Viewport**: `create_layer` (nested `Parent::Child`), `set_layer`, `set_color`, `rename`, `select`, `hide`, `show`, `set_display_mode`, `zoom`, `set_view`, `set_camera`.

### 2. Grasshopper & Third-Party Plugin Component-First Wiring
- **Component-First Policy**: Prioritizes `[COMPANY]` clusters/user objects, native Grasshopper components, installed third-party plugins (**LunchBox**, **Pufferfish**, **Weaverbird**, **Human**, **Elefront**, **Kangaroo**, **Karamba**, etc.), and **Rhino.Inside.Revit** (`Revit>*`) components instead of falling back to Python scripts. Python 3 `script` generation is omitted from the initial schema unless you explicitly ask for Python.
- **200-Node Graphs & Guaranteed Inputs**: Supports up to **200 nodes** per definition (`140`-item retrieval limit), always reserves shortlist slots for `Number Slider`, `Panel`, `Boolean Toggle`, `Value List`, and `Params > Geometry` selection inputs (`Geometry`, `Curve`, `Brep`, `Surface`, `Mesh`, `Point`), and supports Grasshopper's native `Panel` (`Text`) → numeric/point/boolean/domain wiring.
- **Rhino.Inside.Revit (`Revit>*`) Support**: Automatically detects late-loaded `RhinoInside.Revit.GH` proxies, routes Revit/BIM prompts to Grasshopper in `Auto` mode, and boosts `Revit>*` components (`Add Wall`, `Add Floor`, `Query Elements`, `Query Levels`, `Element Parameter`, `Add DirectShape Geometry`, etc.).
- **Incremental Canvas Editing**: Snapshots selected or active canvas nodes, applies validated `GraphPatch` edits (`add_node`, `delete_node`, `set_param`, `wire`, `unwire`) with pin-level `Flatten`/`Graft`/`Simplify`/`Reverse`, and wraps each turn in **Accept / Reject** with one undo record and up to 3 automatic runtime-error repair attempts.

### 3. WhatsApp-Style Chat UI, Plan-First Architect & Viewport Vision
- **Conversational Messaging UI**: Dark chat bubbles, live AI thinking stages, elapsed timer & ETA, starter prompt chips (auto-hidden once chat starts), and **Auto / Rhino Direct / Grasshopper** mode switcher.
- **Plan-First Architect (`☑ Plan First`)**: Confirms intent, outlines a step-by-step component/geometry plan (with optional compact schematic diagrams when helpful), and asks targeted clarifying questions before building. Click **✓ Approve & Build** or reply `"build it"` to execute.
- **Live Viewport Vision**: Captures the active Rhino viewport before and after turns for the LLM to inspect and verify geometry (without cluttering chat bubbles with screenshots). Use **👁️ Verify Viewport** anytime to ask the AI to inspect the live scene.
- **Generous Request Timeout**: Defaults to **180s** (configurable in Settings).

---

## One-Click Install

1. Download **`dist/GhCopilot-Setup-0.1.0.exe`** (no PowerShell, Yak CLI, or manual `Unblock-File` required).
2. Close **Rhino 8** and **Autodesk Revit** (if using Rhino.Inside.Revit), then double-click **`GhCopilot-Setup-0.1.0.exe`** and click **Install / Update GH Copilot** (or run silently with `--install`).
   - If any plugin file is still locked, Setup uses Windows Restart Manager to report the exact locking process and PID.
   - Optionally enter your **API Key** (saved DPAPI-encrypted to `%LOCALAPPDATA%\GhCopilot\api-key.dpapi`), **Model ID** (`gemini-2.5-flash`), and **Shared Library Folder**.
3. Open **Rhino 8** (or **Rhino.Inside.Revit**) and run the **`GhCopilot`** command — the dockable **GH Copilot** panel and Grasshopper **`GH Copilot`** menu load automatically on startup.

See [docs/INSTALL.md](docs/INSTALL.md) and [docs/CHAT.md](docs/CHAT.md) for full details and troubleshooting.

---

## Solution Architecture

- `src/GhCopilot.Core/` (`net7.0;net48`): Zero Rhino/Grasshopper dependencies — `IndexStore`, `Retriever` (BM25 + plugin/Revit/company boosting), `CompanyLibraryScanner`, `GraphSpec`, `GraphPatch`, `TypeCompat`, `GraphValidator`, `SchemaBuilder`, `PromptBuilder`, `ChatPromptBuilder`, `RhinoActionPlan`, `RhinoPromptBuilder`, `GeminiClient` (multimodal Gemini/Claude/OpenAI), `RepairLoop`, `SecretStore` (DPAPI), `AppSettings`, `PayloadAuditLogger`.
- `src/GhCopilot.Rhino/` (`net48;net7.0-windows` → `.rhp`): Native Rhino 8 `PlugIn` (`LoadTime.AtStartup`), `GhCopilot` command, `RhinoHostBridge`, `RhinoActionExecutor`, and dockable `CopilotDockPanel`.
- `src/GhCopilot.Gh/` (`net48;net7.0-windows` → `.gha`): `GH_AssemblyInfo`, `GH_AssemblyPriority`, `ProxyScanner`, `ParamHydrator`, `UserObjectFolderLoader`, `GrasshopperHostBridge`, `ChatTurnTransaction`, `CanvasSpawner`, `WireConnector`, `GroupPreview`, `CanvasSerializer`, `RhinoViewportInspector`, `PythonScriptSpawner`, and `Eto.Forms` UI (`CopilotPanel`, `PromptTab`, `WhatsAppChatControl`, `SettingsTab`, `PreviewBar`).
- `src/GhCopilot.Tests/` (`xunit`): Automated unit tests (`dotnet test`).
- `tools/`: `deploy.ps1`, `build-yak.ps1`, `build-installer.ps1`, `UiSmokeTest/`, and `Installer/`.
- `docs/`: [CHAT.md](docs/CHAT.md), [INSTALL.md](docs/INSTALL.md), [EVALS.md](docs/EVALS.md), and [OPEN_QUESTIONS.md](docs/OPEN_QUESTIONS.md).

---

## Build From Source

```powershell
dotnet build GhCopilot.sln -c Release
dotnet test GhCopilot.sln -c Release --no-build
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 -Configuration Release -Version 0.1.0
```
