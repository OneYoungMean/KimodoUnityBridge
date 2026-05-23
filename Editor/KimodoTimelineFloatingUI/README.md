# Kimodo Timeline Floating UI

This folder isolates the temporary Timeline floating toolbar implementation.

- `KimodoTimelineFloatingToolbarWindow.cs`: popup toolbar anchored at Timeline window bottom-center.
- `KimodoGenerateAndBakeService.cs`: shared generation entry for inspector and floating toolbar.

Behavior:
- Toolbar collapses when no `KimodoPlayableClip` is selected in Timeline.
- Toolbar expands when a `KimodoPlayableClip` is selected.
- `Dialog` toggles prompt input area.
- `Send` writes prompt to clip and runs existing Generate & Bake flow.
