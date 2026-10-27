# One-window workflow verification — preview.4

2026-10-27. Separate, explicitly marked validation projects; production game projects were not edited.

## Automated native checks

Unity 2022.3.62f3 and 6000.3.23f1, Windows D3D11, Gamma and Linear: 82 Editor and 5 PlayMode cases per configuration, 348 passed with no failures or skips. The 24 new cases cover recommendation policy, preview compositing, native drag/drop dispatch, full five-mode import/scene placement, stale-preview refusal, source reset, package setup archive integrity and extraction safety. The multi-mode case also remuxes a real MOV and processes its opaque background.

The full test sequence exposed two additional save problems: long source filenames exceeded the Windows temporary-path limit, and Unity retained GUIDs for recently deleted assets while suggesting those paths as unused. Generated folders and media names are now short; automatic naming checks disk, metadata and Unity GUIDs. The current full suite passed after both fixes.

## Desktop interaction

Computer Use inspected the real UI Toolkit window in Unity 6000.3.23f1 on Windows DX12. File Select was clicked, the included flat-black.mp4 fixture was chosen in the native file dialog, recommendation and original/result previews appeared automatically, and Add to Scene was clicked. The service saved an effect and selected its scene object. Entering Play Mode through Unity CLI confirmed IsReady=true, IsPlaying=true and an advancing playback time. The fixed scene-placement footer remained visible while the main content could scroll.

The preview is a selected still frame, not continuous video playback. Browser drag/drop and other desktop operating systems were not visually tested.

## Real FFmpeg preparation

Installed tools were discovered first. Then invalid paths simulated a missing installation, and the actual Install service was invoked through Unity CLI. It downloaded the pinned Gyan.dev FFmpeg 9.0.2 essentials ZIP, verified its size and SHA-256, extracted FFmpeg/ffprobe and license notices into the per-user cache, checked execution and required encoders, and reported Ready=true. This was a real download and process launch, not a mocked success. No system PATH or Unity-project executable installation was performed.

Pinned archive: https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip

SHA-256: `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba`

The checksum was independently compared with the publisher's release checksum and GitHub asset digest before inclusion. The Windows installer is x64 only; macOS/Linux discovery and manual setup require platform verification. Network failures can be retried or handled with manually installed tools. The source package contains no FFmpeg binaries.
