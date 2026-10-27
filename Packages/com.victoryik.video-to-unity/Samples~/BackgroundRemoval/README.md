# MP4 background extraction samples

These are opaque synthetic inputs, 160×96, 12 fps, 24 frames. They contain a moving red marker, a soft blue glow and black artwork enclosed in a light frame. Neither file carries an alpha channel.

1. Open **Tools → Video to Unity → MP4 배경 분리** and select `flat-black.mp4` or `flat-green.mp4` from this imported sample folder.
2. Analyze the clip, review the suggested background color and preview several times.
3. Keep border-connected removal enabled for the black sample. The enclosed black artwork should remain while the outer black background disappears. Removing every matching pixel also removes that black artwork.
4. Use a fixed protected rectangle if needed. Coordinates start at the top left; for the central panel use X=0.3125, Y=0.28125, Width=0.375, Height=0.4375.
5. Convert and save a new effect asset, then open the placement workflow.

This is color-based extraction. Rectangles stay in the same screen position; they do not follow moving objects. A single-frame preview has no prior frame for temporal stabilization. Soft glow transparency is estimated, not recovered from an original alpha channel. Use the source with actual alpha when exact preservation is required.

The repository's `Verification/GenerateBackgroundFixtures.py` generates these clips without using production game assets or user videos.
