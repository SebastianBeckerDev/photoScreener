"""Assemble an unsigned Apple Silicon .app/zip from a cross-published payload.

Does not establish that the app runs on macOS. Run package-macos.sh on a Mac for
native validation, signing and optional notarization.
"""
from pathlib import Path
import json
import plistlib
import shutil
import struct
import zipfile

root = Path(__file__).resolve().parent.parent
payload = root / "artifacts/publish/osx-arm64"
app = root / "artifacts/LumaPick.app"
native = []


def macho(data, name):
    magic = data[:4]
    if magic in (b"\xca\xfe\xba\xbe", b"\xca\xfe\xba\xbf"):
        count = struct.unpack_from(">I", data, 4)[0]
        wide = magic[-1] == 0xbf
        for index in range(count):
            offset = 8 + index * (32 if wide else 20)
            cpu = struct.unpack_from(">I", data, offset)[0]
            if cpu == 0x100000c:
                start, size = struct.unpack_from(">QQ" if wide else ">II", data, offset + 8)
                return macho(data[start:start + size], name)
        raise ValueError(f"No ARM64 slice: {name}")
    if magic != b"\xcf\xfa\xed\xfe":
        return None
    cpu, _, _, commands = struct.unpack_from("<IIII", data, 4)
    if cpu != 0x100000c:
        raise ValueError(f"Unexpected architecture: {name}")
    minimum = None
    offset = 32
    for _ in range(commands):
        command, size = struct.unpack_from("<II", data, offset)
        if size < 8 or offset + size > len(data):
            raise ValueError(f"Malformed native load command: {name}")
        if command == 0x32:
            platform, value = struct.unpack_from("<II", data, offset + 8)
            if platform != 1:
                raise ValueError(f"Native binary is not for macOS: {name}")
            minimum = [value >> 16, (value >> 8) & 255, value & 255]
        elif command == 0x24:
            value = struct.unpack_from("<I", data, offset + 8)[0]
            minimum = [value >> 16, (value >> 8) & 255, value & 255]
        offset += size
    return {"file": name, "architecture": "arm64", "minimumMacOS": minimum}


if not (payload / "PhotoScreener.Desktop").is_file():
    raise SystemExit("Publish osx-arm64 before assembling the bundle.")
for path in payload.iterdir():
    if path.is_file():
        info = macho(path.read_bytes(), path.name)
        if info:
            native.append(info)
if not any(binary["file"] == "PhotoScreener.Desktop" for binary in native):
    raise SystemExit("App host is not an ARM64 Mach-O binary.")
plist = plistlib.loads((root / "packaging/macos/Info.plist").read_bytes())
profile = tuple(int(v) for v in plist["LSMinimumSystemVersion"].split("."))
profile = profile + (0,) * (3 - len(profile))
for binary in native:
    if binary["minimumMacOS"] and tuple(binary["minimumMacOS"]) > profile:
        raise SystemExit(f"Deployment profile is below {binary}")
contents = app / "Contents"
(contents / "MacOS").mkdir(parents=True, exist_ok=True)
(contents / "Resources").mkdir(exist_ok=True)
shutil.copytree(payload, contents / "MacOS", dirs_exist_ok=True)
shutil.copyfile(root / "packaging/macos/Info.plist", contents / "Info.plist")
shutil.copyfile(root / "src/PhotoScreener.Desktop/Assets/lumapick.icns", contents / "Resources/lumapick.icns")
archive = root / "artifacts/LumaPick-osx-arm64-unsigned.zip"
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as bundle:
    for path in sorted(app.rglob("*")):
        if not path.is_file():
            continue
        entry = zipfile.ZipInfo(path.relative_to(app.parent).as_posix())
        entry.create_system = 3
        entry.external_attr = (0o100755 if path.name in ("PhotoScreener.Desktop", "createdump") else 0o100644) << 16
        entry.compress_type = zipfile.ZIP_DEFLATED
        bundle.writestr(entry, path.read_bytes())
report = {"bundle": str(app), "archive": str(archive), "deploymentProfile": profile,
          "native": native, "signed": False, "executedOnMac": False}
(root / "artifacts/mac-payload-inspection.json").write_text(json.dumps(report, indent=2))
print(json.dumps(report, indent=2))
