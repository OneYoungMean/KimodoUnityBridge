# Archived HDRP AOV capture path

The unused HDRP AOV reflection path was removed from the runtime analysis renderer.
It consisted of `HdrpAovState`, `ResolveHdrpAovTarget`, `CompleteHdrpAov`,
`CreateHdrpAllocator`, `CreateHdrpCallback`, and `RenderHdrpAovToTexture` in
`Command/command_capture_rendering.cs`.

The active renderer has no call sites for that path. Analysis capture uses the
plain camera target texture route in `RenderCameraToTexture` and the explicit
material replacement route in `RenderCameraDepthToTexture`.
