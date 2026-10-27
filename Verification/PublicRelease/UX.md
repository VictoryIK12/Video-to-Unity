# Editor usability check

2026-10-27, Windows, Unity 6000.3.23f1, isolated validation project.
Native mouse/keyboard interaction was explicitly authorized for this final audit.
No production game project was opened or edited.

Observed with the bundled synthetic black-background clip:

- Background analysis reported RGB 0,0,0 and 100% border consistency.
- Original and checkerboard results appeared side by side at equal scale; the
  white outline and enclosed black area remained visible.
- Dragging on the result created a fixed protected rectangle and populated its
  normalized numeric fields. The stale-preview notice appeared.
- Refreshing the preview retained the chosen black rectangle, while the surrounding
  background became transparent; the notice returned to current-preview status.
- Clicking the conversion button created a new packed MP4 and VideoEffectAsset.
  The completion message named both assets. The serialized asset used PackedAlpha,
  straight RGB and the original 160x96 content size.

The visual check found an unequal-column/image-position issue during integration;
the corrected layout was rechecked in the native editor. Automated native tests
separately cover both output formats, color spaces, lifecycle and numeric UI events.
This is an agent usability check, not feedback from external human beta testers.
