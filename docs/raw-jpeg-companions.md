# JPEG review with Nikon NEF companions

Review and selection use the JPEG. A same-stem `.NEF` beside a `.JPG` or `.JPEG` is copied unchanged with that selection. NEFs never appear as extra thumbnails, enter preview decoding, or increase selected-photo counts. PNGs have no RAW companion. Stem and extension matching accepts case variants; multiple matching NEFs or multiple JPEG extensions sharing a NEF are rejected as ambiguous.

Each source file keeps the existing flat legacy naming rule: `<originalStem>.<its-own-MD5-hash4><originalExtension>`. For example, `DSC_001.JPG` and `DSC_001.NEF` produce two files with the original stem and their independently calculated four-character suffixes. Extensions and bytes are preserved. Independent RAW suffixes allow an edited NEF to be exported without overwriting an older RAW version. There is no selection database or mandatory pair ledger.

Picking publishes verified current NEF companions before the JPEG. Source and exported RAW revisions are rechecked immediately before JPEG publication. A failed RAW copy or collision keeps a new JPEG pick incomplete and reports an error. Cancellation or interruption can leave a **complete** orphan NEF; it does not establish selection and is preserved/reused on retry. Owned partial staging files are cleaned using the existing recovery protocol.

Unpicking removes verified current RAW companions before the JPEG, including matching duplicate RAW copies. A remaining/locked/substituted companion leaves the authoritative JPEG selected with an error, allowing retry. Older RAW versions, unrelated files, ambiguous associations and exported NEFs whose originals are missing remain preserved. Sources stay read-only.

Resume continues to infer selection from verified JPEG exports alone. Existing JPEG-only picks stay selected; an explicit pick/Retry can add a missing current NEF companion. Opening a folder does not bulk-export RAW files. In explicitly shared targets, identical original JPEG names/bytes share selection; picking copies each associated folder's current NEF, even if their RAW contents differ, and unpicking removes their verified current copies.

Use Space as before: copy/remove the current JPEG selection and its NEF companions without advancing. Errors identify the NEF when possible; Retry repeats the requested operation. Held Space is suppressed and navigation remains available during background copying.
