# Changelog

## 3.0.0-preview.5 — 2026-10-27

- Public packaging (2026-10-28): include MIT license and external-tool notices; update installation links and the English quick start. Runtime/editor implementation is unchanged.

- Keep generated effect asset names equal to their filenames, including after scene placement, to prevent Unity's main-object name mismatch warning. Keep the source video's name on the scene object.
- Clarify that Video Effect Asset is the plugin's reusable settings asset, separate from the video file.

## 3.0.0-preview.4 — 2026-10-27

- Add one window for video drag/drop, automatic mode recommendations, time-selectable before/after previews, and direct scene placement with Undo. Save reusable assets and videos automatically.
- Discover and validate FFmpeg/ffprobe from saved paths, PATH and common installations. Add an explicit Windows x64 portable download with pinned version, SHA-256 verification, bounded safe extraction and cancellation.
- Keep preview results tied to the current source/settings; cancel obsolete work and reject stale scene additions. Preserve native alpha, support additive/opaque/packed modes, and save WebGL media under StreamingAssets.
- Keep advanced color/protected-region controls folded away and scene placement visible in a fixed footer. Preserve advanced windows under the Advanced menu.

## 3.0.0-preview.3 — 2026-10-27

- Fix open-GOP preview/analysis seeks and retain the exact source clock in packed output. Reject existing transparency with a redirect to the alpha conversion workflow.
- Speed up offline processing with bounded parallel matte/codec work and complete per-frame integrity checks. Record both timing gains and the additional child-process memory cost.
- Add manual black/green presets, source-color sampling, preview freshness feedback and protected-region editing safeguards; correct preview layout.
- Remove project-specific prototypes from the distribution tree and scrub machine paths from validation evidence. Historical repository refs still require private preservation; provide a fresh-snapshot export tool.

- Add offline MP4 color-background extraction: conservative edge-color analysis, manual color fallback, border-connected matting, fixed protected rectangles and stable-edge temporal smoothing.
- Add a Korean UI Toolkit window with original/checkerboard preview, time selection, numeric/drag protection controls and creation of new video/effect assets.
- Stream full-resolution frames through a lossless verified intermediate into the existing packed H.264 or alpha WebM converter; retain straight RGB, dimensions and source timing.
- Publish completed video files atomically and refuse to overwrite existing output. Cancel owned processes and clean temporary files without blocking the UI.
- Add opaque black/green sample clips and focused core, FFmpeg, geometry and native editor workflow validation. This estimates alpha from color; it does not recover original layers, perform semantic AI segmentation or track moving objects.

## 3.0.0-preview.2 — 2026-10-27

- Preserve H.264 frame/container/SPS color tags; encode VP8 with its appropriate BT.601 matrix. Fix the native Windows color shift found in preview.1.
- Verify decoded RGB, all frame color tags and bitstream metadata; expand standalone conversion coverage to 43 cases.
- Regenerate tagged diagnostic sources and both alpha outputs.
- Pass 116 native Built-in test cases across Unity 2022.3/6, Gamma/Linear, EditMode/PlayMode; add scene lifecycle and real Canvas mask checks.
- Verify Unity 6 URP/HDRP 17.3 camera Canvas playback with masks: 14 scenarios and 56 RGBA checks.
- Add reproducible native batch helpers and portable evidence. Other platforms, standalone builds and visual Editor UX remain unverified.

## 3.0.0-preview.1 — 2026-10-27

- Independent UPM package with reusable effect assets, one native decoder per player and shareable views.
- Native alpha, packed alpha, additive, black key, chroma key and opaque compositing.
- UI Toolkit creation window, screen/existing UI/world-space placement and Timeline track.
- Cancelable ProRes/alpha-WebM conversion to packed H.264 or VP8-alpha WebM with full-frame verification.
- StreamingAssets/URL sources, diagnostic media and development tests.
- Managed compilation and standalone tests verified; native Unity/device verification remains pending.

Earlier project-specific prototypes are excluded from the distribution and are not dependencies.
