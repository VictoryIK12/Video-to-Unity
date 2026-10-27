# Basic sample
Import this sample from Package Manager, then open Tools → Video to Unity → 영상 이펙트 만들기.
Choose one of the included diagnostic videos, select the matching mode, save the effect and create a screen/world-space instance.

- packed-alpha.mp4: PackedAlpha. Logical size 128×64; encoded size 256×64.
- native-alpha.webm: NativeAlpha, Keep Alpha + Transcode.
- black-background.mp4: Additive or BlackKey. Black artwork cannot be recovered separately from black background.
- Source~/source-alpha.mov: original diagnostic alpha source for converter verification. The ignored Source~ folder prevents Unity from trying to import ProRes directly on unsupported Editor platforms. Select this file through the converter's file picker.

The pattern has transparent, opaque-black, half-red and opaque-green regions, plus a moving blue marker. It is a small verification fixture, not a performance benchmark.
