# GH Copilot — Open Questions & Verified API Log

## Phase 0 Architecture Decision (Hybrid `.rhp` + `.gha` + `.Core`)
- **Decision**: Built as a hybrid solution from Phase 0:
  - `GhCopilot.Core` (`net7.0;net48`): zero Rhino/Grasshopper/Eto references; holds `PluginConstants`, `IGrasshopperHostBridge`, and `CopilotHostRegistry`.
  - `GhCopilot.Rhino` (`net48;net7.0-windows` -> `.rhp`): subclasses `Rhino.PlugIns.PlugIn`, registers the dockable `Eto.Forms.Panel` (`CopilotDockPanel`), and exposes the `GhCopilot` Rhino command.
  - `GhCopilot.Gh` (`net48;net7.0-windows` -> `.gha`): subclasses `GH_AssemblyInfo` and `GH_AssemblyPriority`, registers `GrasshopperHostBridge`, and injects the Grasshopper menu bar items.
- **Why**: A standalone `.gha` cannot call `Rhino.UI.Panels.RegisterPanel(...)` because `RegisterPanel` requires a `Rhino.PlugIns.PlugIn` instance. Adding `.rhp` in Phase 0 resolves the dockable panel risk immediately and prepares the product for future Rhino-side commands.
- **Deployment note**: Rhino 8 discovers both `.rhp` and `.gha` inside `%APPDATA%\McNeel\Rhinoceros\packages\8.0\GhCopilot\0.1.0\`. On a target machine, keep **either** the Yak package folder **or** `%APPDATA%\Grasshopper\Libraries\GhCopilot\` (not both on the same machine) to avoid duplicate Grasshopper assembly ID warnings.

## NuGet Package & Cross-Targeting Note
- `RhinoCommon` and `Grasshopper` NuGet version **`8.0.23304.9001`** are referenced with `ExcludeAssets="runtime"` and `PrivateAssets="all"` so host DLLs (`RhinoCommon.dll`, `Grasshopper.dll`, `GH_IO.dll`, `Rhino.UI.dll`, `Eto.dll`) are never copied into build/deploy outputs.
- **NU1701 finding**: `8.0.23304.9001` NuGet packages ship only `lib\net48\` reference assemblies (RhinoCommon added `.NETFramework7.0` package groups in later 8.x releases such as `8.23+`). Keeping `8.0.23304.9001` ensures compatibility with all Rhino 8 service releases (`8.0`–`8.36+`); `<NoWarn>$(NoWarn);NU1701</NoWarn>` is applied in `GhCopilot.Rhino.csproj` and `GhCopilot.Gh.csproj` while targeting `net7.0-windows`.

## Verified Rhino / Grasshopper / Eto APIs (Phase 0 Reflection Check)
Inspected against `%USERPROFILE%\.nuget\packages\rhinocommon\8.0.23304.9001\lib\net48` and `grasshopper\8.0.23304.9001\lib\net48`:
- `RhinoCommon` v8.0.23304.9001:
  - `Rhino.PlugIns.PlugIn`: `protected virtual LoadReturnCode OnLoad(ref string errorMessage)`
  - `Rhino.PlugIns.PlugInDescriptionAttribute(DescriptionType, string)`
  - `Rhino.PlugIns.LoadReturnCode`: `ErrorShowDialog, Success, ErrorNoDialog`
  - `Rhino.Commands.Command`: `public abstract string EnglishName`, `protected abstract Result RunCommand(RhinoDoc doc, RunMode mode)`
  - `Rhino.RhinoApp`: `InvokeOnUiThread(Delegate method, params object[] args)`, `WriteLine(string)`
- `Rhino.UI` v8.0.23304.9001:
  - `Rhino.UI.Panels.RegisterPanel(PlugIn plugin, Type panelType, string caption, Icon icon)`
  - `Rhino.UI.Panels.OpenPanel(Guid panelId)`
- `Eto` v2.7.0.0:
  - `Eto.Forms.Panel` (`Content`), `Eto.Forms.DynamicLayout` (`Padding`, `Spacing`, `BeginVertical()`, `EndVertical()`, `Add(...)`), `Eto.Forms.Label`, `Eto.Forms.Button` (`Click`), `Eto.Forms.MessageBox.Show(Control, string, string, MessageBoxType)`
- `Grasshopper` v8.0.23304.9001:
  - `Grasshopper.Kernel.GH_AssemblyInfo` (`Name`, `Description`, `Id`, `AuthorName`, `AuthorContact`, `Version`)
  - `Grasshopper.Kernel.GH_AssemblyPriority`: `public abstract GH_LoadingInstruction PriorityLoad()` (`Proceed`, `Abort`)
  - `Grasshopper.Instances.CanvasCreated` (`CanvasCreatedEventHandler(GH_Canvas)`), `Instances.DocumentEditor` (`GH_DocumentEditor : Form`, `MenuStrip MainMenuStrip`), `Instances.ActiveCanvas`, `Instances.ComponentServer.ObjectProxies`

## Verified Phase 1 Canvas, Undo & Special Object APIs
Inspected via reflection and `Grasshopper.xml` (`8.0.23304.9001`):
- `GH_Document`:
  - `SolutionState` (`GH_ProcessStep`: `PreProcess, Process, PostProcess, Aborted`)
  - `ScheduleSolution(int delay, GH_ScheduleDelegate callback)` (`void Invoke(GH_Document doc)`)
  - `AddObject(IGH_DocumentObject docObject, bool update, int index = int.MaxValue)`
  - `FindObject(Guid id, bool topLevelOnly)`
  - `RemoveObjects(IEnumerable<IGH_DocumentObject> objs, bool update)`
  - `NewSolution(bool expireAllObjects)`
- `GH_Document.GH_UndoUtil`:
  - `RecordAddObjectEvent(string name, IEnumerable<IGH_DocumentObject> objs)`
  - `RecordRemoveObjectEvent(string name, IEnumerable<IGH_DocumentObject> objs)`
  - `RecordGenericObjectEvent(string name, IGH_DocumentObject obj)`
- `IGH_ObjectProxy` / `IGH_DocumentObject` / `IGH_Attributes` / `IGH_Param` / `IGH_Component` / `IGH_VariableParameterComponent`:
  - `proxy.CreateInstance()` returns a fresh `IGH_DocumentObject` per placement
  - `CreateAttributes()`, `Attributes.Pivot` (`PointF`), `Attributes.ExpireLayout()`, `Attributes.PerformLayout()`
  - `IGH_Param.AddSource(IGH_Param)`, `IGH_Component.Params.Input`, `IGH_Component.Params.Output`
  - `IGH_VariableParameterComponent`: `CanInsertParameter`, `CreateParameter`, `GH_ComponentParamServer.RegisterInputParam`, `VariableParameterMaintenance()`
- `GH_NumberSlider`: `Slider` (`GH_SliderBase`: `Type` (`GH_SliderAccuracy.Integer`/`Float`), `DecimalPlaces`, `Minimum`, `Maximum`), `SetSliderValue(decimal)`
- `GH_Panel`: `SetUserText(string)`
- `GH_Group`: `Colour`, `Border` (`GH_GroupBorder.Box`), `NickName`, `AddObject(Guid)`, `ExpireCaches()`

## Verified Phase 2 Index & Proxy Scanning APIs
Inspected via reflection (`Grasshopper.dll` `8.0.23304.9001`):
- `GH_ComponentServer`: `ObjectProxies` (`IList<IGH_ObjectProxy>`), `Libraries` (`ReadOnlyCollection<GH_AssemblyInfo>`), `EmitObjectProxy(Guid)`
- `IGH_ObjectProxy`: `Guid`, `LibraryGuid`, `Kind` (`GH_ObjectType`: `None, CompiledObject, UserObject`), `Type`, `Obsolete`, `Exposure` (`GH_Exposure` including `hidden`), `Desc` (`Name`, `NickName`, `Category`, `SubCategory`, `Description`), `CreateInstance()`
- `GH_AssemblyInfo`: `Id`, `Name`, `Version`, `IsCoreLibrary`
- `IGH_Param`: `Name`, `NickName`, `TypeName`, `Access` (`GH_ParamAccess`: `item, list, tree`), `Optional`

## Verified Phase 3 Gemini REST API & RuntimeMessage APIs
- **Gemini REST API (`v1beta`, verified against Google docs `ai.google.dev`)**:
  - Endpoint: `POST https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent`
  - Model list check: `GET https://generativelanguage.googleapis.com/v1beta/models` (supports `pageToken` / `nextPageToken`)
  - Auth header: `x-goog-api-key` (never passed in query string or logged)
  - Structured output fields inside `generationConfig`:
    - `responseMimeType`: `"application/json"`
    - `responseJsonSchema`: standard JSON Schema object (`responseSchema` is omitted because the two fields are mutually exclusive)
- **Grasshopper RuntimeMessages (inspected via reflection on `Grasshopper.dll` `8.0.23304.9001`)**:
  - `IGH_ActiveObject.RuntimeMessages(GH_RuntimeMessageLevel)` (`GH_RuntimeMessageLevel`: `Blank, Warning, Error, Remark`)

## Verified Phase 4 Dockable Panel Spike & Eto UI APIs
- **Dockable Panel Decision**:
  - Verified that `Rhino.UI.Panels.RegisterPanel(...)` strictly requires a `Rhino.PlugIns.PlugIn` instance, which a standalone `.gha` does not have.
  - With our Hybrid `.rhp` + `.gha` architecture:
    1. `GhCopilot.Rhino.rhp` registers the native Rhino dockable panel (`CopilotDockPanel`).
    2. `GhCopilot.Gh.gha` also provides a modeless `Eto.Forms.Form` fallback (`CopilotPanel.ShowModelessForm()`) owned by `Rhino.UI.RhinoEtoApp.MainWindowForOwner` and opened from `GH Copilot -> Open Copilot Window (Modeless)`.
  - Both host the shared `CopilotPanel` (`PromptTab`, `SettingsTab`, `PreviewBar`) compiled via MSBuild `<Compile Include="..\GhCopilot.Gh\UI\*.cs" />` (`internal` visibility) to avoid code duplication and cross-plugin Grasshopper dependency leaks.
- **Verified Eto APIs (`Eto.dll` v2.7.0.0 & `Rhino.UI.dll` v8.0.23304.9001)**:
  - `TabControl` (`Pages`), `TabPage` (`Text`, `Content`), `TextArea` (`Text`, `ReadOnly`, `Wrap`, `Height`), `PasswordBox` (`Text`), `CheckBox` (`Text`, `Checked`), `ProgressBar` (`Indeterminate`, `Visible`), `SelectFolderDialog` (`Title`, `Directory`, `ShowDialog`), `Form` (`Title`, `ClientSize`, `MinimumSize`, `Resizable`, `Owner`, `Visible`, `Show()`, `BringToFront()`, `Closed`), `RhinoEtoApp.MainWindowForOwner`, `DynamicLayout.Add(Control, bool? xscale, bool? yscale)`.

## Verified Phase 5 Canvas Serialization & Replacement Undo APIs
Inspected via reflection (`Grasshopper.dll` `8.0.23304.9001`):
- `GH_Document.SelectedObjects()` -> `List<IGH_DocumentObject>`
- `IGH_Attributes.GetTopLevel.DocObject` resolves parameter owners back to top-level `IGH_Component` / `IGH_Param`
- `IGH_Param.Sources` and `IGH_Param.Recipients` (`IList<IGH_Param>`)
- `GH_NumberSlider.CurrentValue` (`decimal`), `GH_Panel.UserText` (`string`), `IGH_ActiveObject.RuntimeMessages(GH_RuntimeMessageLevel)`
- `GH_Document.GH_UndoUtil.MergeRecords(int count)` merges removing originals + recolouring the accepted group into one Ctrl+Z record.

## Verified Phase 6 Python 3 Script Component Spike
Inspected via reflection (`Grasshopper.dll` and `GH_IO.dll` `8.0.23304.9001`):
- **Compile-time available APIs**:
  - `GH_IO.Serialization.GH_Archive`: `Deserialize_Xml(string)`, `Serialize_Xml()`, `ExtractObject(GH_ISerializable, string)`, `AppendObject(GH_ISerializable, string)`, `GetRootNode` (`GH_Chunk`).
  - `Grasshopper.Kernel.Parameters.Param_ScriptVariable`: `Name`, `NickName`, `Access` (`GH_ParamAccess`), `TypeHint` (`IGH_TypeHint`), `ShowHints`, `AllowTreeAccess`.
  - `Grasshopper.Kernel.Parameters.Param_GenericObject` (for outputs), `IGH_VariableParameterComponent` (`CanInsertParameter`, `CreateParameter`, `DestroyParameter`, `VariableParameterMaintenance`), and `Grasshopper.Kernel.Parameters.Hints.*` (`GH_DoubleHint_CS`, `GH_IntegerHint_CS`, `GH_BooleanHint_CS`, `GH_StringHint_CS`, `GH_Point3dHint`, `GH_Vector3dHint`, `GH_PlaneHint`, `GH_BoxHint`, `GH_IntervalHint`, `GH_LineHint`, `GH_CircleHint`, `GH_ArcHint`, `GH_PolylineHint`, `GH_CurveHint`, `GH_SurfaceHint`, `GH_ExtrusionHint`, `GH_BrepHint`, `GH_SubDHint`, `GH_MeshHint`, `GH_GeometryBaseHint`, `GH_ColorHint`, `GH_TypeHintServer`).
- **Runtime-only Rhino 8 component (`RhinoCodePluginGH.gha` / `GhPython.gha`)**:
  - Rhino 8's Python 3 script component assembly (`RhinoCodePluginGH.gha`) is NOT distributed in the `Grasshopper` NuGet package, so it cannot be referenced directly at compile time.
  - `PythonScriptSpawner` therefore resolves the Python 3 proxy from `Instances.ComponentServer.ObjectProxies` (falling back to embedded `Python3Template.ghx` via `GH_Archive`), configures parameters via `IGH_Component` / `IGH_VariableParameterComponent` + `Param_ScriptVariable` / `Param_GenericObject`, and injects `code` via reflection + `GH_Archive` chunk rehydration. On a Rhino 8 target machine, any unrecognized runtime script-property layout is logged to `%LOCALAPPDATA%\GhCopilot\python-component-spike.txt` for immediate inspection.

## Verified Phase 7 Company Library & UserObject/Cluster APIs
Inspected via reflection and `Grasshopper.xml` (`Grasshopper.dll` `8.0.23304.9001`):
- `Grasshopper.Folders`: `UserObjectFolders` and `ClusterFolders` (`IList<string>`, read-only properties returning mutable lists).
- `Grasshopper.Kernel.GH_UserObject`: `.ctor(string path)`, `ReadFromFile()`, `InstantiateObject()`, `Description` (`GH_InstanceDescription`), `Icon`, `Exposure`, `Data`, `Guid` (note: transient per load, so `UserObjectFolderLoader` assigns deterministic GUIDs from normalized relative file paths for index stability).
- `Grasshopper.Kernel.Special.GH_Cluster`: `CreateFromFilePath(string path)` loads loose `.gh` / `.ghx` cluster files into a `GH_Cluster` instance.
- `GH_ComponentServer.AddProxy(IGH_ObjectProxy)` registers custom `IGH_ObjectProxy` wrappers (`CompanyFileObjectProxy`) for company `.ghuser` and loose `.gh`/`.ghx` clusters.

## Remaining Verification Items (Target Rhino 8 Machine)
1. **Build vs. Runtime Environment**: Build machine uses `.NET SDK 10.0.401` (compile & Core tests only). Live Rhino 8 verification (`net48` and `net7.0` via `SetDotNetRuntime`) requires a machine with Rhino 8 installed.
2. **API Policy**: Confirm organization policy for sending component names/canvas metadata and key tier.
3. **Shared Library**: Confirm shared folder path and contents (`.ghuser`, loose `.gh` clusters, or both).
4. **Target OS**: Windows-only (`net48` + `net7.0-windows`) or future Rhino for Mac (`net7.0` / AppKit considerations).
