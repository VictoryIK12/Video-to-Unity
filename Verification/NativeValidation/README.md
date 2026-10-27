# Native Unity verification

Windows, a licensed Unity Editor, Python and a real graphics device are required for these development checks. The plugin itself does not require Python.

`Run.ps1` creates an isolated project below this repository's ignored `.validation` folder. It refuses an existing unmarked directory. **Do not point these scene lifecycle tests at a production project**: they create and reload temporary scenes and clear test Undo history. The marker `.video-to-unity-validation` enables the Editor integration checks.

```powershell
./Verification/NativeValidation/Run.ps1 `
  -UnityEditor 'C:/Program Files/Unity/Hub/Editor/6000.3.23f1/Editor/Unity.exe' `
  -Generation 6 -ProjectName Unity6
```

Use `-Generation 2022` with a Unity 2022.3 executable and a different project name for the minimum-version checks. Add `-Installed` only when reusing an already installed, marked validation project. Every Unity process is started hidden with `-batchmode -force-d3d11`; `-nographics` would invalidate GPU coverage. Installation uses `PackageManager.Client.AddAndRemove`, without hand-editing manifests. The runner checks the process exit code and rejects failed, skipped or empty test results.

The installer waits for package resolution before copying the tests, so the initial bootstrap has no dependency on the uninstalled package. Test assemblies receive separate validation names. PlayMode media is copied to the isolated project's StreamingAssets. Four native runs follow: Gamma/EditMode, Gamma/PlayMode, Linear/EditMode and Linear/PlayMode. Each has its own XML and log. UI Toolkit is inspected through its API without opening a visible window; this is not visual UX testing.

`Collect.py` collects the recorded `Unity2022` and `Unity6` project results into [portable evidence](evidence.json), with all test names, results, durations, native XML checksums, dependency versions, source fingerprints and fixture fingerprints. Raw logs and original XML stay local because they contain machine paths. Text fingerprints normalize line endings. This collector verifies existing evidence and does not launch Unity.

These checks cover Windows Editor playback/rendering, not standalone player builds or other platforms. The URP/HDRP camera rendering suite is [separate](../PipelineValidation/REPORT.md). Screen Space Overlay object creation is tested; its final backbuffer pixels are not captured by these suites.
