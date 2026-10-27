# Editor file-transfer validation

2026-10-27: **13 standalone checks passed** against the production VideoEffectFileTransfer.cs.

Covered byte-exact copying; refusal to overwrite/delete existing output; pre-cancellation; competing destination preservation; mid-copy cancellation and partial cleanup; intact source; missing source; owned temporary path; cleanup success; deletion-scope rejection.

Background-removal integration adds atomic publication: copying uses a dot-prefixed temporary on the destination volume, then moves the completed file without overwrite. Additional checks cover hidden-temporary cleanup after cancellation, a competing file created during copy, and cleanup after that collision. Unity should never receive an unfinished movie at the final import path.

```powershell
./Packages/com.victoryik.video-to-unity/Editor/UI/Validation~/TestFileTransfer.ps1 -DotnetRoot '<DOTNET_INSTALL_ROOT>'
```

These standalone checks exercise filesystem behavior. See the repository Verification/README.ko.md for native importer, UI API and playback integration coverage.
