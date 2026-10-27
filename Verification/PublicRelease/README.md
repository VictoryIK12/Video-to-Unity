# Usability verification

The focused usability checks cover preview color sampling, letterboxing, inclusive
image edges, malformed buffers and protected-region capacity. They use the production
geometry source and the same NUnit test methods that Unity can run.

```powershell
./Verification/PublicRelease/RunUsabilityChecks.ps1 -NUnitAssembly '<NUNIT_FRAMEWORK_DLL>'
```

The `BackgroundUsabilityAuditTests` class additionally covers persistent error messages,
bounded region allocation, normalized numeric fields, preview freshness, source changes,
and manual black/green presets that preserve protected regions. Those tests require an
isolated Unity validation project with the `.video-to-unity-validation` marker. They are
not executed by the standalone runner. The marker follows the existing native test convention.

Use the repository's `Verification/CompileManaged.ps1` for compilation against installed
Unity assemblies. Managed compilation does not validate visual behavior or native video import.

`AuditLocalHistory.py` inventories local refs, every historical tree, every local Git blob,
and nested ZIP/tar/Unity package payloads without extracting archives or contacting a remote.
Supply `--private-output` pointing outside the checkout. Optional private project markers
can be supplied in a JSON array using `--private-pattern-file`, also outside the checkout.
Only aggregate counts are printed. Detailed locations belong in private evidence, never
in a public report. Pattern matches are review candidates; no matches do not certify secrecy
or media provenance. Local unreachable objects are included but are not assumed public.

`PrepareSnapshot.py --destination <NEW_FOLDER> --author-name <PUBLIC_NAME>
--author-email <PUBLIC_EMAIL>` exports the committed, clean distribution tree into a
new local Git repository with one commit and no remotes. It rejects unexpected root
entries and links. Run the history/archive audit again in that repository. This is
an alternative distribution source, not a rewrite of the original repository. Existing
private history, tags and hosted attachments require separate review before changing
the original repository's visibility. The script does not choose or grant a license.
