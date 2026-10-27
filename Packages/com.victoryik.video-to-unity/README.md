# Video to Unity

Reusable Unity video effects for screen overlays, existing UI and world-space canvases.

**3.0.0-preview.5:** drag a video into one window, inspect a recommendation and preview,
then add it to the scene. Includes automatic color-background extraction, alpha
conversion, GPU compositing, reusable settings assets and Timeline playback.

Open **Tools → Video to Unity → 영상으로 이펙트 만들기** (make an effect from video).

- [English quick start and installation](Documentation~/QuickStart.en.md)
- [한 창 사용법 / One-window guide](Documentation~/QuickWorkflow.ko.md)
- [Advanced usage / 세부 사용법](Documentation~/Usage.ko.md)
- [Video delivery formats / 전달 형식](Documentation~/VideoFormats.ko.md)
- [Timeline](Documentation~/Timeline.ko.md)
- [Architecture and alternatives / 설계 근거](Documentation~/Architecture.ko.md)
- [Diagnostic sample](Samples~/Basic/README.md)

Targets Unity 2022.3+, uGUI and Timeline. FFmpeg and ffprobe are used only for
Editor-time analysis/conversion and are not bundled. Runtime playback does not need them.

Native Windows tests cover Unity 2022.3/6 Built-in (Gamma/Linear) and Unity 6
URP/HDRP 17.3 (Linear). Preview.5 additionally verifies asset-name persistence.
Other platforms and standalone builds remain unverified; see the repository's
Verification report for exact versions and scope.

Background extraction is color-based, not semantic AI segmentation. It may remove
wanted artwork of the same color. Prefer actual alpha sources for predictable
transparency. Packed H.264 doubles encoded width; target-device codec limits apply.

## License

[MIT](LICENSE). External tools and Unity dependencies retain their own terms;
see [Third Party Notices](Third%20Party%20Notices.md).
