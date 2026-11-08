"""Regenerate LumaPick's vector mark and desktop icons (requires Pillow).

The committed assets are sufficient for normal builds; Pillow is only needed
when editing this geometric logo. No photo or generated build output is used.
"""
from pathlib import Path
from math import cos, sin, radians
from io import BytesIO
import struct
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parent.parent
assets = root / "src/PhotoScreener.Desktop/Assets"
assets.mkdir(parents=True, exist_ok=True)
colors = ["#77EFD3", "#50E5C3", "#3FC6AC", "#268F88", "#2DB09D", "#42CBB3"]

def point(radius, angle):
    return (256 + radius * cos(radians(angle)), 256 + radius * sin(radians(angle)))

blades = []
for i in range(6):
    blades.append([point(174, -90 + i * 60), point(174, -30 + i * 60),
                   point(67, 30 + i * 60), point(67, -30 + i * 60)])
tick = [(218, 253), (248, 282), (302, 221)]
svg = ['<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" role="img" aria-labelledby="title desc">',
       '<title id="title">LumaPick</title>',
       '<desc id="desc">A mint camera aperture around a white selection check on a dark rounded square.</desc>',
       '<rect width="512" height="512" rx="104" fill="#101721"/>']
for blade, color in zip(blades, colors):
    coords = " ".join(f"{x:.3f},{y:.3f}" for x, y in blade)
    svg.append(f'<polygon points="{coords}" fill="{color}"/>')
svg.append('<path d="M218 253 L248 282 L302 221" fill="none" stroke="#F4FFF9" stroke-width="23" stroke-linecap="round" stroke-linejoin="round"/>')
svg.append('</svg>')
(assets / "lumapick.svg").write_text("\n".join(svg) + "\n", encoding="utf-8")

def raster(size):
    # Supersampling keeps small icon edges and the check readable.
    scale = max(2, 1024 // size)
    factor = size * scale / 512
    image = Image.new("RGBA", (size * scale, size * scale))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle((0, 0, image.width - 1, image.height - 1), radius=104 * factor, fill="#101721")
    for blade, color in zip(blades, colors):
        draw.polygon([(x * factor, y * factor) for x, y in blade], fill=color)
    points = [(x * factor, y * factor) for x, y in tick]
    width = max(1, round(23 * factor))
    draw.line(points, fill="#F4FFF9", width=width, joint="curve")
    for x, y in points:
        radius = width / 2
        draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill="#F4FFF9")
    return image.resize((size, size), Image.Resampling.LANCZOS)

logo = raster(1024)
logo.save(assets / "lumapick.png")
logo.save(assets / "lumapick.ico", sizes=[(size, size) for size in (16, 24, 32, 48, 64, 128, 256)])
# PNG-backed ICNS representations are supported by the macOS 14+ profile.
chunks = []
for tag, size in ((b"icp4", 16), (b"icp5", 32), (b"icp6", 64), (b"ic07", 128),
                  (b"ic08", 256), (b"ic09", 512), (b"ic10", 1024)):
    output = BytesIO(); raster(size).save(output, format="PNG"); data = output.getvalue()
    chunks.append(tag + struct.pack(">I", len(data) + 8) + data)
body = b"".join(chunks)
(assets / "lumapick.icns").write_bytes(b"icns" + struct.pack(">I", len(body) + 8) + body)
print(f"Generated SVG, PNG, ICO and ICNS in {assets}")
