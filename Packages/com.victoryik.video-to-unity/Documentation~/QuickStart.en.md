# Drag a video into your Unity scene

## Install

In Unity 2022.3 or later, open **Window → Package Manager → Add package from git URL** and paste:

```text
https://github.com/VictoryIK12/Video-to-Unity.git?path=/Packages/com.victoryik.video-to-unity#v3.0.0-preview.5
```

The repository is public; repository access credentials are not required. Unity needs Git installed for this method. Alternatively, download the ZIP from the [preview.5 release](https://github.com/VictoryIK12/Video-to-Unity/releases/tag/v3.0.0-preview.5), extract it and use **Add package from disk** to select `package.json`.

Editor labels are currently Korean; the steps below include their English meanings. uGUI and Timeline dependencies are resolved by Package Manager.

## Create an effect

1. Install the package using Package Manager, then open **Tools → Video to Unity → 영상으로 이펙트 만들기** (make an effect from video).
2. Drop one video from your file browser or Unity Project window. The tool finds FFmpeg/ffprobe automatically. If missing, use **영상 도구 자동 준비** (prepare video tools) on Windows x64, or expand manual paths.
3. Review the recommendation and before/after preview. Move the time slider to check another frame; it refreshes automatically.
4. Choose full screen, an existing UI Canvas, or a world-space plane. Click **씬에 추가** (add to scene).
5. Save the scene and enter Play mode.

The same window copies or converts the video, chooses a new output path, saves an effect asset and creates the scene object. Originals are preserved. Outputs are stored under `Assets/VideoToUnity/Generated`; with WebGL selected, media goes into `Assets/StreamingAssets/VideoToUnity`.

## Recommendations

- Alpha-capable sources or sampled transparent pixels: preserve source alpha; the full conversion verifies transparency.
- Consistent border color: remove the flat background. Check that wanted artwork survives.
- Uncertain background: retain the original. Choose a color manually if needed.
- Choose additive manually for light/glow, or packed alpha for an existing left-color/right-alpha movie. These are not inferred from filenames.

The preview is a still frame at the chosen time. Enter Play mode after scene addition to inspect continuous playback. Open **색과 보호 영역 조절** for color and keep-region controls. Rectangles stay fixed throughout the clip and do not track objects. Selecting a different source resets them. Changed settings disable scene addition until the new preview is ready.

FFmpeg preparation runs only when requested, downloads a pinned Windows build into the user cache, and verifies it before use. It does not change system PATH or bundle executables into your Unity project. Other platforms use discovered or manually configured tools.

Effects are silent by default. Undo removes the created scene object while preserving reusable assets. Closing the window or entering Play mode cancels pending preparation. If the active scene changes during conversion, the asset is kept but automatic placement stops.

## Limits

This is color-based extraction, not semantic AI segmentation or layer recovery. Smoke, glow and artwork sharing the background color may need adjustment. Prefer actual straight-alpha source artwork when available. The converter's SDR/CFR/even-dimension restrictions still apply. Packed output doubles the encoded width; native codec support and Web autoplay rules vary by target. World effects are planes, not volumetric particles.

Advanced import/compositing controls remain under **Tools → Video to Unity → 고급**. See the Korean [one-window guide](QuickWorkflow.ko.md), [formats](VideoFormats.ko.md), and repository verification report for precise coverage.
