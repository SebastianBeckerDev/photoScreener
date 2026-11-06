# Export format and legacy compatibility

Updated 2026-11-06. This is the normative on-disk format for the rewrite's reads **and writes**. It supersedes the proposed source-relative destination tree. The [implementation contract](implementation-contract.md) supplies background scheduling, verification and source protection.

## Folder binding and layout

For ordinary review of a source folder, the default target is its immediate `export` subfolder. Reuse an existing directory; create a missing one lazily on the first pick. Merely opening a source folder does not create files. Each reviewed folder has its own binding. An explicitly chosen custom target overrides that folder's default and remains in effect when returning to it; loading the folder must not reset the override. Without optional binding metadata, defaults are discoverable and custom targets can be explicitly selected again.

Write picked photos directly into the effective target, with no source-relative subdirectories:

```text
Trip/
  Day1/
    DSC_0001.JPG
    export/
      DSC_0001.kAFQ.JPG
      .photoscreener/             # optional metadata and owned staging only
  Day2/
    DSC_0001.JPG
    export/
      DSC_0001.<hash4>.JPG
```

Source-relative paths identify rows, cursors and cache entries internally; they do not change the export layout. Exclude `export` subtrees and explicitly designated targets from source discovery. Reject a target equal to or containing protected sources; validate a nested target before permitting mutations. Opening old exports requires no rename, move, migration or database.

Normal folder navigation can continue while pending jobs finish against their captured bindings. Changing a binding for the same folder requires finishing or explicitly canceling its pending changes. A project-wide Picked view combines the indexes for each folder's effective target, rather than comparing every source folder against whichever target was last active. Custom/shared targets are indexed once and associated with the folders explicitly bound to them.

## Exact filename algorithm

Given source path `source` and effective target `target`:

1. Read the complete original file bytes and compute their MD5 digest.
2. Encode the digest using standard Base64 and take its first four characters.
3. Replace `+` with `-` and `/` with `_` in those four characters.
4. Use `Path.GetFileNameWithoutExtension(source) + "." + hash4 + Path.GetExtension(source)` directly under `target`.

Preserve the original filename stem, extension and case. Preserve the original image bytes, including metadata; do not re-encode the image. Path, timestamps and operating system do not affect the suffix. A dotted stem retains its dots; hash case is significant. If the destination filesystem cannot represent the name, report an error rather than silently rename it.

| Original filename | Exact bytes used as a test fixture | Expected exported filename |
| --- | --- | --- |
| `DSC_0001.JPG` | ASCII `abc` (3 bytes) | `DSC_0001.kAFQ.JPG` |
| `Photo.test.png` | ASCII `message digest` (14 bytes) | `Photo.test.-Wtp.png` |

These small fixtures test file-format helpers, not image decoding. The unchanged legacy `PhotoFileService.ComputeTargetPath` and `BuildTargetIndex` were executed against both vectors on 2026-11-06: exact filenames and filename/length lookup round trips passed. Replacement implementation and macOS interoperability remain acceptance work.

## Reading existing picks safely

The old app enumerates immediate target files, strips one final `.[A-Za-z0-9_-]{4}` stem suffix, and matches the resulting original filename plus file length, ignoring case. Its `TryAdd` keeps only the first candidate. The rewrite retains compatibility with those names while improving association correctness:

- Enumerate immediate final image files in each effective target; do not include staging or metadata.
- Retain all filename/length candidates. Consider both an unchanged filename and one possible stripped suffix, since a natural dotted filename can resemble a hash suffix.
- Verify uncertain candidates against the source's full content in the background before confirming Picked or deleting anything. Filename, length and the four-character suffix alone are insufficient. Unsuffixed copies recognized by the old app may be reused when verified; new copies always use the algorithm above.
- If natural dotted names allow one target to match different original filenames with identical bytes, content alone cannot establish its owner. Require an explicit association before deletion; do not silently merge differently named sources. Shared associations below apply only to equal original names.
- Retain the actual verified target paths. Unpick removes all matching associated copies of that source revision within its target, so a duplicate does not restore the pick after restart. If one removal fails, report the remaining observed copies and the failed request.
- Do not infer associations across unbound targets. An older export with missing source or unresolved identity remains visible as unmatched/outdated and is never automatically removed.

Default per-folder exports separate identical filenames in different source folders. In an explicitly shared flat target, equal original filenames **and identical bytes** have one shared pick association; the format cannot encode their source origins. Show this relationship and keep all associated rows consistent. Mutations serialize/coalesce by shared association, using the latest request sequence across those rows. Equal filenames with different content remain separate when their suffixes differ. Same length alone never shares a pick.

## Background writes and collisions

Compute MD5 for naming and SHA-256 for full-content verification while streaming the source once into an owned `.photoscreener/staging/*.part` file. Hashing never runs in a key handler or blocks the first preview. Verify staged bytes and source revision, then atomically publish using the exact legacy name with no-clobber semantics.

If that destination already exists, reuse it only after full-content verification proves it is the same photo. Otherwise report a collision and leave it untouched; do not overwrite it or invent a longer/non-legacy suffix. Four Base64 characters contain only 24 bits and are not a unique identity. MD5 here is a compatibility naming rule, not the content-verification authority.

A changed source version may produce a different suffix. Picking it adds its current-version copy without automatically replacing/removing older exported versions. Those older bytes remain explicit unmatched/outdated files; unpick affects verified current-version associations only. Publication must never expose partial final files.

New completed picks can be recognized by the old app's filename/length index for the same source/target binding without conversion. Its weak matching and lack of participation in the rewrite's writer locks remain legacy limitations; concurrent editing by the old app is treated as an external filesystem change.

## Legacy source evidence

- [PhotoFileService.cs](../../PhotoScreener/Services/PhotoFileService.cs): `ComputeHash4`, `ComputeTargetPath`, `StripHashSuffix`, `MakePickKey`, `BuildTargetIndex`.
- [PickService.cs](../../PhotoScreener/Services/PickService.cs): `PickPhoto` writes the computed path directly into the target; `UnpickPhoto` deletes the associated target.
- [MainWindow.PhotoLoading.cs](../../PhotoScreener/MainWindow.PhotoLoading.cs): `LoadFolder` selects `Path.Combine(path, "export")` before immediate-file scanning.
- [MainWindow.Misc.cs](../../PhotoScreener/MainWindow.Misc.cs): explicit target picker; its reload/default override bug is not part of the required compatibility.
