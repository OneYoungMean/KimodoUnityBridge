# Kimodo QuickServer Runtime

This package contains only the Kimodo model, motion representation, skeleton,
BVH export, and MotionCorrection code. QuickServer routing, TCP interaction,
session state, asset provisioning, and ARDY integration live in the sibling
`core` package and are started through `core.quickserver_cli`.

The upstream web demo, visualization, benchmark, metrics, development CLI,
Docker, and asset-build utilities are retained under the repository-level
`Archive~` directory and are not installed with the Unity QuickServer runtime.

Use the launchers in the parent `NvlabKimodoQuickServer~` directory to set up
and run the server.
