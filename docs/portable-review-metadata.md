# Moving photo folders and review metadata

`<export target>/.photoscreener/review.json` stores optional cursor, filter and review history. Schema version 2 identifies each source folder **relative to the directory containing `review.json`**, using `/` separators on both Windows and macOS.

For example:

```text
Holiday/
  photo.JPG
  Export/Luminar/Again Exported/.photoscreener/review.json
```

The source folder's key in this file is `../../../..`. Copying or moving `Holiday` together with its exports, or renaming it, preserves this relationship. Open the relocated source and resolve its export target as usual; a custom target still needs an explicit choice or `--export PATH`. Metadata never automatically grants permission to mutate a target.

Version 1 files with absolute folder keys remain readable at their original location. The next successful metadata save upgrades all their folder entries to version 2, retaining other folders' history in a shared target. Save with the updated app before relocating an old version 1 tree; a file already copied with obsolete absolute paths cannot reliably establish its original folder mapping.

Relative paths preserve relationships when the source and target move together. Moving only the target or changing their relative layout requires pairing them again. Separate Windows volumes cannot be expressed by a relative path, so those entries retain an absolute folder key.

The saved cursor restores by filename, length and modification time, because copying changes native file identity/change tokens. This is position restoration only: picks and safe export mutations still require current file/content verification. Existing review history remains optional historical information.

The user-local `PhotoScreener/session-v1.json` separately remembers the last opened folder with an absolute path. It is outside the photo tree and cannot locate a moved folder automatically; open the new location once to update it. Completed picks remain recoverable from export files even when every optional metadata file is missing.
