#!/usr/bin/env python3
"""Regenera o ícone original do Whisper Recorder usando Python e Pillow 12."""

from __future__ import annotations

from io import BytesIO
from math import cos, pi, sin
from pathlib import Path
import struct

from PIL import Image, ImageDraw, __version__ as PILLOW_VERSION


OUTPUT_DIR = Path(__file__).resolve().parent
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)
VIEWBOX = 256
BLUE = "#2463D4"
WHITE = "#FFFFFF"
CORAL = "#FF8A80"

# Uma única geometria alimenta o SVG editável e o desenho com Pillow.
TILE = (12, 12, 244, 244)
TILE_RADIUS = 52
CAPSULE = (96, 48, 144, 136)
CAPSULE_RADIUS = 24
MIC_CENTER = (120, 136)
SUPPORT_RADIUS = 48
SUPPORT_TOP = 120
STROKE = 16
STEM_BOTTOM = 200
BASE = (96, 200, 144, 200)
RECORD_CENTER = (184, 72)
RECORD_RADIUS = 18


def svg_source() -> str:
    """Serializa as mesmas formas geométricas, sem fontes ou recursos externos."""
    x0, y0, x1, y1 = TILE
    mx0, my0, mx1, my1 = CAPSULE
    cx, cy = MIC_CENTER
    left, right = cx - SUPPORT_RADIUS, cx + SUPPORT_RADIUS
    bottom = cy + SUPPORT_RADIUS
    bx0, by0, bx1, by1 = BASE
    rx, ry = RECORD_CENTER
    return f'''<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256"
     viewBox="0 0 {VIEWBOX} {VIEWBOX}" role="img" aria-labelledby="title desc">
  <title id="title">Whisper Recorder</title>
  <desc id="desc">Microfone branco e ponto de gravação coral sobre base azul arredondada.</desc>
  <rect x="{x0}" y="{y0}" width="{x1 - x0}" height="{y1 - y0}"
        rx="{TILE_RADIUS}" fill="{BLUE}"/>
  <g fill="none" stroke="{WHITE}" stroke-width="{STROKE}" stroke-linecap="round">
    <path d="M {cx} {bottom} V {STEM_BOTTOM} M {bx0} {by0} H {bx1}"/>
    <path d="M {left} {SUPPORT_TOP} V {cy}
             A {SUPPORT_RADIUS} {SUPPORT_RADIUS} 0 0 0 {right} {cy}
             V {SUPPORT_TOP}"/>
  </g>
  <rect x="{mx0}" y="{my0}" width="{mx1 - mx0}" height="{my1 - my0}"
        rx="{CAPSULE_RADIUS}" fill="{WHITE}"/>
  <circle cx="{rx}" cy="{ry}" r="{RECORD_RADIUS}" fill="{CORAL}"/>
</svg>
'''


def render(size: int) -> Image.Image:
    """Desenha cada tamanho separadamente; reduz com Lanczos em RGBA premultiplicado."""
    # Pelo menos 4x; as versões pequenas usam uma tela de 1024 px para curvas limpas.
    working_size = max(1024, size * 4)
    scale = working_size / VIEWBOX
    canvas = Image.new("RGBA", (working_size, working_size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(canvas)

    def box(coords: tuple[float, ...]) -> tuple[float, ...]:
        return tuple(value * scale for value in coords)

    def circle(center: tuple[float, float], radius: float, color: str) -> None:
        x, y = center
        draw.ellipse(box((x - radius, y - radius, x + radius, y + radius)), fill=color)

    def stroke(points: list[tuple[float, float]]) -> None:
        draw.line(
            [(x * scale, y * scale) for x, y in points],
            fill=WHITE,
            width=round(STROKE * scale),
            joint="curve",
        )
        for point in (points[0], points[-1]):
            circle(point, STROKE / 2, WHITE)

    draw.rounded_rectangle(box(TILE), radius=TILE_RADIUS * scale, fill=BLUE)
    cx, cy = MIC_CENTER
    stroke([(cx, cy + SUPPORT_RADIUS), (cx, STEM_BOTTOM)])
    bx0, by0, bx1, by1 = BASE
    stroke([(bx0, by0), (bx1, by1)])
    # Preenche o arco como faixa contínua; evita emendas entre segmentos de linha.
    # Os raios externo/interno equivalem ao arco central com stroke do SVG.
    support = []
    for radius, start, direction in (
        (SUPPORT_RADIUS + STROKE / 2, pi, -1),
        (SUPPORT_RADIUS - STROKE / 2, 0, 1),
    ):
        support.extend(
            (
                (cx + radius * cos(start + direction * step * pi / 384)) * scale,
                (cy + radius * sin(start + direction * step * pi / 384)) * scale,
            )
            for step in range(385)
        )
    draw.polygon(support, fill=WHITE)
    for side in (cx - SUPPORT_RADIUS, cx + SUPPORT_RADIUS):
        stroke([(side, SUPPORT_TOP), (side, cy)])
    draw.rounded_rectangle(box(CAPSULE), radius=CAPSULE_RADIUS * scale, fill=WHITE)
    circle(RECORD_CENTER, RECORD_RADIUS, CORAL)
    # Premultiplicar evita franjas escuras nas bordas transparentes.
    return canvas.convert("RGBa").resize((size, size), Image.Resampling.LANCZOS).convert("RGBA")


def png_bytes(image: Image.Image) -> bytes:
    buffer = BytesIO()
    image.save(buffer, format="PNG", optimize=True)
    return buffer.getvalue()


def ico_bytes(frames: dict[int, Image.Image]) -> bytes:
    """Empacota PNGs RGBA em ICO, inclusive nas resoluções pequenas (sem DIB/BMP)."""
    header = struct.pack("<HHH", 0, 1, len(ICO_SIZES))
    entries = []
    payloads = []
    offset = 6 + 16 * len(ICO_SIZES)
    for size in ICO_SIZES:
        payload = png_bytes(frames[size])
        dimension = 0 if size == 256 else size
        entries.append(
            struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(payload), offset)
        )
        payloads.append(payload)
        offset += len(payload)
    return header + b"".join(entries) + b"".join(payloads)


def main() -> None:
    if PILLOW_VERSION.split(".", 1)[0] != "12":
        raise SystemExit(f"Use Pillow 12.x; versão encontrada: {PILLOW_VERSION}")
    frames = {size: render(size) for size in ICO_SIZES}
    icon = ico_bytes(frames)
    # Confirma que Pillow consegue abrir todas as entradas antes de gravar.
    with Image.open(BytesIO(icon)) as decoded:
        if decoded.ico.sizes() != {(size, size) for size in ICO_SIZES}:
            raise RuntimeError("O ICO não contém todas as resoluções esperadas.")
        for size, frame in frames.items():
            if decoded.ico.getimage((size, size)).convert("RGBA").tobytes() != frame.tobytes():
                raise RuntimeError(f"O ICO alterou os pixels da entrada de {size} px.")
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    (OUTPUT_DIR / "icon.svg").write_text(svg_source(), encoding="utf-8")
    (OUTPUT_DIR / "icon.ico").write_bytes(icon)
    (OUTPUT_DIR / "icon-256.png").write_bytes(png_bytes(frames[256]))
    (OUTPUT_DIR / "icon-1024.png").write_bytes(png_bytes(render(1024)))
    print(f"Gerados em {OUTPUT_DIR}: icon.svg, icon.ico, icon-256.png, icon-1024.png")


if __name__ == "__main__":
    main()
