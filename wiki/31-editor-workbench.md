# 31. Editor Workbench Status

This page is the current capability record for the native ImGui editor and the
HTML5 editor. It deliberately distinguishes shipped behaviour from planned
near-UE workflow parity. Do not infer an editor feature from the presence of a
runtime component or a design-document heading.

The versioned source of truth for shared terminology and command identifiers is
[`editor-workbench.json`](../editor-workbench.json). It currently establishes
the common default regions and the `W` / `E` / `R`, focus, undo/redo, duplicate
and delete command vocabulary. It also records each portable property type as
editable, read-only-with-reason or tracked-missing for each client. Contract
fixtures in both editor test suites reject a shortcut or property-status drift.

## Current capability matrix

| Workflow | Native editor | HTML5 editor | Status |
|---|---|---|---|
| Default information architecture | Dockable, resizable ImGui panels with persisted layout | Fixed responsive regions and tabs | Partial — same regions, browser docking/persistence missing |
| Command surface | Menu bar and toolbar commands | Searchable `Ctrl/Cmd+P` command palette backed by named commands | Partial — command registry and palette parity are still incomplete |
| Actor selection | One actor | One actor | Partial — no ordered multi-select, box select or selection locks |
| 3D transform | Local axis translation/rotation/scale; centre camera-plane move, screen-space rotate and uniform scale; snapping | World/local visual axes; axis/free translate, rotate, scale; snapping | Partial — no transform parity spec implementation for planes, pivots, advanced snapping or 2D visual parity |
| Undo/redo | MCP/assistant scene history; panel changes are not consistently transactions | Structural actor/component actions, focused Inspector field commits, and completed 3D gizmo drags | Partial — one transaction layer and stable actor identities are still required |
| Inspector primitives | Reflection widgets for primitive/vector/colour/enum fields | Schema widgets for primitives, vectors, colours, enums and validated JSON collection fields | Partial — typed asset/object references, maps, nested structures, reset/defaults and specialist inspectors missing |
| Project save access | Filesystem project workflows | Import/export and local development workflow | Partial — File System Access direct save and permission state are not complete |
| Asset authoring | File browser and image thumbnails | Basic asset panel | Missing typed pickers, previews, dependency repair and per-asset editors |
| Prefab, graph, animation, timeline, terrain and world partition authoring | Not available as end-to-end editor workflows | Not available as end-to-end editor workflows | Missing — runtime primitives and documentation are not editor certification |

## Delivered foundation

- Shared workbench contract, with a layout-region and command vocabulary.
- Shared `W` / `E` / `R` transform commands in both clients.
- Browser `Ctrl/Cmd+P` command palette dispatches the shared transform, focus,
  undo/redo, duplicate and delete IDs, plus save and play.
- Browser reversible history for structural actor/component actions, completed
  Inspector field edits and complete 3D gizmo drags; Escape cancels an active
  browser gizmo drag or Inspector field edit.
- Browser 3D transform handles with visible mode, world/local display axes and
  grid/angle/scale snapping.
- Native 3D centre-handle camera-plane movement, uniform scale and screen-space
  rotation, where it was previously a dead handle.
- Browser list inspector values are editable as validated scene-format JSON and
  rehydrated through their declared item schema. This is intentionally an
  interim generic collection editor, not a claim of specialised collection UX.

## Required certification before claiming parity

For every delivered workflow, record the build, platform, input method,
screenshots/video and linked issue in the editor issue tracker. The minimum
manual playbook is:

1. On desktop mouse/keyboard, create and select a 3D actor; use `W`, `E` and
   `R`, both axis and centre handles, then repeat with each snap increment.
2. Confirm one completed drag becomes one history item where the client
   advertises history; Escape restores the starting transform.
3. Exercise inspector validation: enter invalid collection JSON, verify no
   component mutation, then enter valid JSON and save/reopen the scene.
4. On tablet/touch browser, verify tap selection, visible transform feedback,
   drag cancellation and no accidental camera motion during a handle drag.
5. Review default layout and responsive breakpoints for focus, hover, selected,
   drag-target and validation-error states.

## Tracked parity gaps

The next core work is one transaction dispatcher with stable serialised actor
editor IDs, selection collections and deterministic duplicate/delete. Then come
the transform behaviour specification (coordinate spaces, pivots, plane and
surface/vertex snapping), generated component-property capability coverage,
typed asset references and specialist component inspectors. Prefab editing,
visual/material/animation graphs, Sequencer-style timelines, terrain/foliage
and world partition remain separate vertical slices: none is complete until
both clients can author, save, reopen, inspect and preview its portable asset.
