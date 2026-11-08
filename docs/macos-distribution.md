# Distributing LumaPick on macOS

The current self-contained package targets Apple Silicon and macOS 14+. Normal builds use .NET SDK 10.0.303. `scripts/package-macos.sh` assembles `artifacts/LumaPick.app`, including the native libraries and Finder/Dock icon.

## Direct download: signing and notarization

You need Apple Developer Program membership, a **Developer ID Application** certificate with its private key in the build Mac's keychain, Apple command-line tools providing `codesign`, `notarytool`, `stapler` and `spctl`, and internet access. Xcode or a suitable Command Line Tools installation provides the tools. Use `security find-identity -v -p codesigning` to find the exact certificate identity.

Create a keychain profile for notarization (the command prompts for the app-specific password):

```bash
xcrun notarytool store-credentials "lumapick-notary" \
  --apple-id "YOUR_APPLE_ID" --team-id "YOUR_TEAM_ID"
```

An App Store Connect API key is an alternative; `notarytool store-credentials --help` documents its key/issuer options. Keep passwords, API keys and private certificate keys outside this repository.

Build and distribute:

```bash
SIGNING_IDENTITY="Developer ID Application: Your Name (TEAM_ID)" \
NOTARY_PROFILE="lumapick-notary" \
bash scripts/package-macos.sh --notarize
```

The script signs the native libraries and executable with hardened runtime settings, signs/verifies the complete app, submits `artifacts/LumaPick-osx-arm64.zip`, waits for Apple, staples the accepted ticket, assesses Gatekeeper and rebuilds the ZIP with the ticket included. Test the resulting artifact on a separate Mac. Our unsigned CI bundle checks are distinct from successful signing/notarization; those steps require real developer credentials and have not been executed here.

## Mac App Store

The same membership permits an App Store submission, but Developer ID/notarization is not that submission path. A Store release needs an App Store Connect listing and bundle registration, the appropriate Store distribution certificates/provisioning, sandbox entitlements, upload and App Review, plus screenshots and accurate privacy declarations.

LumaPick's current package is **not App Store-ready**. Its chosen source/export folder access needs sandbox support and persistent security-scoped bookmarks so access survives restarts; the Store signing/upload pipeline also remains to be implemented. The present scripts implement direct distribution only. Intel Macs would need their own build/test profile.
