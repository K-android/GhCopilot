# Conversational canvas editing

## Workflow

- **Send** starts or continues the current chat. The last six complete user/assistant pairs are sent in full. Older pairs become a bounded, explicitly lossy local summary; no extra model call is made for summarization.
- Every send reads the **current Grasshopper selection**, or the last subgraph edited/spawned in this chat when nothing is selected. Neighboring components and the rest of the definition are not serialized. Boundary wires contain aliases only. Geometry and instance GUIDs are not included in the compact graph block.
- Stable `n1`, `n2`, … aliases are local to the chat. Moving, changing, deleting, or wiring nodes is observed on the next send. Changing the scoped graph while an API call is running aborts application of its stale response.
- Initial creation can return a complete graph, including the existing validated Python fallback. Once a subgraph exists, replies must use validated `add_node`, `delete_node`, `set_param`, `wire`, or `unwire` operations. Unchanged nodes retain their Grasshopper instance IDs.
- Explain Selection, Fix Selection, and Save Recipe are transcript quick actions. Recipe capture is local and does not require an API call. Explain uses a text response; ordinary graph-edit replies can use an empty operations list plus an explanation to answer without changes.
- **Accept** keeps the pending turn and publishes one Grasshopper undo record, including that turn's automatic repairs and explicitly requested input references. **Reject** reverses only that turn. Finish the pending preview before sending another turn or starting a new chat. Acceptance/rejection decisions are retained in session context.
- **New Chat** clears history, the summary, aliases, diagnostics, and spawned-node tracking. It does not remove accepted canvas objects. A session is tied to one Grasshopper definition; start a new chat after switching definitions.

## Required inputs and repair safety

Automatic repair runs only when required inputs contain valid data **and** runtime diagnostics contain an error. Warnings alone never trigger paid repair calls. Connected-but-empty inputs also count as missing. Invalid or unreadable goo blocks repair; missing downstream data is treated conservatively as missing input rather than guessed.

When input is missing, the transcript and diagnostics ask the user to insert valid data or explicitly enable **Use Rhino Selection** and send again. Fix Selection also checks inputs before calling the model. The checkbox defaults to off and resets after every request. It is never inferred from an empty input or a vague reference to a selection.

With explicit consent, compatible selected Rhino objects are bound locally as persistent geometry references to empty supported inputs. Supported types are generic geometry, curves, points, Breps, surfaces, and meshes. Existing wires and persistent data are preserved; an empty connected input must be populated at its source. Numeric inputs are not populated from arbitrary Rhino objects. No Rhino geometry is uploaded.

After solving a preview, at most **three** automatic patch requests are made. Each repair re-snapshots the turn's exact scope, validates operations, and rechecks input validity before applying. Invalid patches or API failures stop repair rather than relaxing validation or replacing the full graph. Remaining errors stay visible below the transcript. Cancellation rolls back the pending turn.

## Payload and cost

The system instruction contains component shortlist metadata. The user payload contains, in order: rolling summary, recent role-labelled messages, compact live graph JSON, previous/current validator and runtime diagnostics, and the new message. History is escaped as data, not treated as system instructions.

The chat always sends its scoped structural metadata, including panel text (up to 200 characters); the legacy non-selected-neighbor setting does not widen chat scope. Do not place secrets in prompts, panel text, or diagnostic messages. Existing payload audit logging still applies.

The panel displays a character-based approximate input-token count before each API call, including automatic repair calls. The final count totals estimates for all calls in the turn. It is **not** provider-reported usage, an output-token estimate, or a billing quote. No extra inference call is made just to estimate tokens.

## Manual Rhino 8 smoke checks

1. Generate a circle with a radius slider, Accept, then request a radius change. Verify the same slider instance is edited and a single Undo restores the prior accepted turn.
2. Change a slider manually, clear selection, and ask Explain Selection. Verify current values are explained from the last subgraph, not old model output.
3. Preview a wire change or node deletion with external connections, then Reject. Verify the original nodes, boundary wires, and group membership return without removing earlier accepted turns.
4. Generate a component requiring geometry without supplying it. Verify the request for valid input and **no automatic repair calls**. Supply geometry and send again.
5. Explicitly enable Use Rhino Selection with compatible selected geometry. Verify local references, checkbox reset, and Reject restoring the pre-turn input state.
6. With valid populated inputs, provoke an actual runtime error. Verify errors-only repair, at most three calls, one accepted undo transaction, and remaining diagnostics when unsuccessful. Repeat with warnings only and verify no repair call.
7. Cancel a request or switch active definitions while it runs. Verify stale output is not applied and any pending mutation is rolled back on its owning definition.
8. Continue beyond six turns, then use New Chat. Verify context rolling, stable aliases during the chat, and clean history afterward without deleting accepted geometry.

Automated Core tests cover context rolling, payload ordering/privacy, token estimates, patch validation, and repair gating. Grasshopper transactions, Rhino-reference binding, and Eto interaction require the live checks above; compilation does not validate native Rhino behavior.

## Diagnosing panel startup

Startup now records load, command, panel-construction checkpoints, and loaded host assembly identities in `%LOCALAPPDATA%\GhCopilot\startup.log`. Managed failures in plug-in registration, command execution, or shared panel construction are caught and reported. The command no longer queries live Grasshopper state merely to open the panel. This cannot intercept native crashes or stack overflows; a Rhino crash report is still needed for those.

The standalone `tools/UiSmokeTest` utility constructs the shared panel using the installed Eto WPF runtime and exercises native layout/disposal without launching Rhino or creating a Grasshopper document. This checks UI initialization only, not Rhino docking or native-host integration. It requires the standard Rhino 8 System installation directory (override `RhinoSystemDir` when building if necessary).