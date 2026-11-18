# GH Copilot — Installation & Verification Guide

## 1. Prerequisites
- **Rhino 8 for Windows** installed on the target machine.
- An LLM API key (**Google Gemini**, **Anthropic Claude**, or **OpenAI**; never share keys between machines).
- Optional: Read access to a shared library folder (`.ghuser` / `.gh` cluster files).

---

## 2. Install on a Windows PC
Copy **`GhCopilot-Setup-0.1.0.exe`** (from `dist/`) to the target machine and double-click it — or use the `.yak` package directly:

### Option A — One-Click Windows Setup Installer (Recommended)
1. Double-click **`GhCopilot-Setup-0.1.0.exe`** (or run `GhCopilot-Setup-0.1.0.exe --install` silently).
2. The installer automatically:
   - Extracts the multi-target plugin (`net48/` + `net7.0/` + `manifest.yml`) into `%APPDATA%\McNeel\Rhinoceros\packages\8.0\GhCopilot\0.1.0\`.
   - Removes Mark-of-the-Web (`:Zone.Identifier`) and any duplicate `%APPDATA%\Grasshopper\Libraries\GhCopilot\` copy.
   - Registers `GhCopilot.Rhino.rhp` in `HKCU\Software\McNeel\Rhinoceros\8.0\Plug-Ins\8A4E9C12-3B7D-4F6A-9E21-5C8D7A1B3E40` so Rhino 8 + Grasshopper auto-load GH Copilot on startup.
3. Optional: enter your **API Key**, **Model ID**, and **Shared Library Folder** directly inside the setup window before clicking **Install / Update GH Copilot**.

### Option B — Install via Yak CLI
```powershell
Unblock-File .\ghcopilot-0.1.0-rh8_0-any.yak
& "C:\Program Files\Rhino 8\System\Yak.exe" install .\ghcopilot-0.1.0-rh8_0-any.yak
```

### Option C — Manual Package Folder Extract
1. Extract `ghcopilot-0.1.0-rh8_0-any.yak` (rename `.yak` → `.zip` if needed) into:
   `%APPDATA%\McNeel\Rhinoceros\packages\8.0\GhCopilot\0.1.0\`
   Confirm the folder layout has `manifest.yml` at the root beside `net48\` and `net7.0\`.
2. Remove any separate copy in `%APPDATA%\Grasshopper\Libraries\GhCopilot\` so Grasshopper does not report a duplicate assembly ID.
3. Unblock all files:
   ```powershell
   Get-ChildItem "$env:APPDATA\McNeel\Rhinoceros\packages\8.0\GhCopilot\0.1.0" -Recurse -File | Unblock-File
   ```

---

## 3. Configure Key, Provider, Model & Shared Library
1. Launch **Rhino 8** and run the command `GhCopilot` (or open Grasshopper and click **`GH Copilot -> Open Copilot Window (Modeless)`**).
2. Open the **Settings** tab:
   - Choose your **LLM Provider** (`Gemini`, `Anthropic`, or `OpenAI`).
   - Paste your **API Key** (stored DPAPI-encrypted per-user at `%LOCALAPPDATA%\GhCopilot\api-key.dpapi`).
   - Confirm **Model ID** (e.g., `gemini-3.6-flash`, `claude-opus-4-6`, or `gpt-4.1`) and click **Verify Model**.
   - Set **Shared Library Folder** (local path or `\\server\share\gh-library`).
   - Keep **Send canvas data** unchecked unless approved for your environment.
   - Click **Save Settings**.

---

## 4. Acceptance Smoke Checklist (Test Both Runtimes)
Run `SetDotNetRuntime` in Rhino 8 (restart Rhino after switching) and verify on both **`NETFramework` (`net48`)** and **`NETCore` (`net7.0`)**:
1. **Menu & Dockable Panel**: Both `GhCopilot` dockable panel and Grasshopper `GH Copilot` menu load without errors.
2. **Phase 1 Demo**: Click **Spawn Demo (No LLM)** → amber preview group spawns (`12.5 + 7.5 = 20`); **Reject Preview** deletes all spawned nodes cleanly; `Ctrl+Z` restores cleanly.
3. **LLM Prompt**: Enter `circle with a radius slider` → **Send** → amber group spawns and solves; **Accept Preview** turns group green.
4. **Canvas Fix**: Select a red/broken component → **Fix Selection** → replacement preview spawns without deleting the broken original until **Accept Preview**.
5. **Python 3 Fallback**: Enter `make a spiral staircase` → spawns a Python 3 Script node with typed params.
6. **Custom Cluster**: Ask for a task matching a cluster in your shared folder → verify `[COMPANY]` cluster is selected over a native equivalent.

---

## 5. Logs & Troubleshooting
- **Index dump**: `%LOCALAPPDATA%\GhCopilot\index-dump.json`
- **Index cache**: `%LOCALAPPDATA%\GhCopilot\index-cache.json` (auto-rebuilds when plugins or shared library files change)
- **Privacy audit log**: `%LOCALAPPDATA%\GhCopilot\sent-payloads.jsonl`
- **Eval run log**: `%LOCALAPPDATA%\GhCopilot\runs.jsonl`
- If a plugin fails to load, re-run `Unblock-File` recursively on the package folder and check `SetDotNetRuntime`.
