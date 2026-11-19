# GH Copilot

A standalone hybrid **Rhino 8 + Grasshopper** AI copilot (`.rhp` + `.gha` + `.Core`) with BYOK multi-provider support (**Google Gemini**, **Anthropic Claude**, and **OpenAI**), two-tier lazy component indexing, deterministic graph validation/repair, pin-level data-tree modifiers (`Flatten`/`Graft`/`Simplify`/`Reverse`), topological layered auto-layout, reusable canvas recipes, Python 3 script fallback, and company library preference.

---

## Chat and Incremental Editing

The prompt panel now keeps conversational context, snapshots only the selected/last chat subgraph, and uses validated incremental patches for follow-ups. Each turn has Accept/Reject and one accepted undo transaction. Automatic repair is limited to three attempts and runs only for runtime errors with valid required inputs; empty inputs ask for data or explicit **Use Rhino Selection** consent.

See [docs/CHAT.md](docs/CHAT.md) for behavior, privacy, token estimates, and the Rhino smoke-test checklist.

---

## One-Click Install

1. Download **`dist/GhCopilot-Setup-0.1.0.exe`** (no PowerShell, Yak CLI, or manual `Unblock-File` required).
2. Double-click **`GhCopilot-Setup-0.1.0.exe`** and click **Install / Update GH Copilot** (or run silently with `--install`).
   - Optionally enter your **API Key** (saved DPAPI-encrypted to `%LOCALAPPDATA%\GhCopilot\api-key.dpapi`), **Model ID** (`gemini-3.6-flash`), and **Shared Library Folder**.
3. Open **Rhino 8** and **Grasshopper**:
   - The **GH Copilot** dockable panel (`GhCopilot` command) and Grasshopper **`GH Copilot`** menu load automatically on startup.

See [docs/INSTALL.md](docs/INSTALL.md) for the full smoke-test checklist and troubleshooting notes.

---

## Solution Architecture

- `src/GhCopilot.Core/` (`net7.0;net48`): Zero Rhino/Grasshopper dependencies — `IndexStore`, `Retriever` (BM25 + company boost), `CompanyLibraryScanner`, `GraphSpec`, `TypeCompat`, `GraphValidator`, `SchemaBuilder`, `PromptBuilder`, `GeminiClient`, `RepairLoop`, `SecretStore` (DPAPI), `AppSettings`, `PayloadAuditLogger`.
- `src/GhCopilot.Rhino/` (`net48;net7.0-windows` → `.rhp`): Native Rhino 8 `PlugIn`, `GhCopilot` command, and dockable `CopilotDockPanel`.
- `src/GhCopilot.Gh/` (`net48;net7.0-windows` → `.gha`): `GH_AssemblyInfo`, `GH_AssemblyPriority`, `ProxyScanner`, `ParamHydrator`, `UserObjectFolderLoader`, `CanvasSpawner`, `WireConnector`, `GroupPreview`, `CanvasSerializer`, `PythonScriptSpawner`, and shared `Eto.Forms` UI (`CopilotPanel`, `PromptTab`, `SettingsTab`, `PreviewBar`).
- `src/GhCopilot.Tests/` (`xunit`): Automated Core unit tests (`dotnet test`).
- `tools/`: `deploy.ps1`, `build-yak.ps1`, `build-installer.ps1`, and `tools/Installer/`.
- `docs/`: [INSTALL.md](docs/INSTALL.md), [EVALS.md](docs/EVALS.md), and [OPEN_QUESTIONS.md](docs/OPEN_QUESTIONS.md).

---

## Build From Source

```powershell
dotnet build GhCopilot.sln -c Release
dotnet test GhCopilot.sln -c Release --no-build
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
```
