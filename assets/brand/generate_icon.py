import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent
BASE_SIZE = 256
SUPERSAMPLE = 4
CANVAS = BASE_SIZE * SUPERSAMPLE
CENTER = 128
RING_RX = 82
RING_RY = 46
RING_CY = 130
RING_ROTATION_DEGREES = -24
RING_WIDTH = 7

SVG = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" role="img" aria-label="SoulComputerUse brand icon">
  <defs>
    <linearGradient id="soul-gradient" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#4EA6F0"/>
      <stop offset="1" stop-color="#3FB950"/>
    </linearGradient>
    <radialGradient id="soul-glow" cx="35%" cy="25%" r="75%">
      <stop offset="0" stop-color="#FFFFFF" stop-opacity="0.48"/>
      <stop offset="0.45" stop-color="#FFFFFF" stop-opacity="0.12"/>
      <stop offset="1" stop-color="#FFFFFF" stop-opacity="0"/>
    </radialGradient>
    <linearGradient id="cursor-gradient" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#FFFFFF"/>
      <stop offset="1" stop-color="#DDF4FF"/>
    </linearGradient>
    <filter id="cursor-shadow" x="-30%" y="-20%" width="170%" height="160%">
      <feGaussianBlur stdDeviation="4"/>
    </filter>
  </defs>
  <rect x="22" y="22" width="212" height="212" rx="48" fill="#191D22" stroke="#343B43" stroke-width="2"/>
  <ellipse cx="128" cy="130" rx="82" ry="46" fill="none" stroke="#EAF7FF" stroke-opacity="0.96" stroke-width="7" transform="rotate(-24 128 130)"/>
  <circle cx="128" cy="130" r="58" fill="url(#soul-gradient)"/>
  <circle cx="128" cy="130" r="58" fill="url(#soul-glow)"/>
  <path d="M {front_start_x:.1f} {front_start_y:.1f} A 82 46 -24 0 1 {front_end_x:.1f} {front_end_y:.1f}" fill="none" stroke="#F5FBFF" stroke-opacity="0.96" stroke-width="7" stroke-linecap="round"/>
  <path d="{cursor_shadow_path}" fill="#071018" opacity="0.30" transform="translate(0 5)" filter="url(#cursor-shadow)"/>
  <path d="{cursor_outline_path}" fill="url(#cursor-gradient)"/>
</svg>
"""


def scale(value: float) -> int:
    return round(value * SUPERSAMPLE)


def rounded_rectangle(draw: ImageDraw.ImageDraw, box: tuple[int, int, int, int], radius: int, **kwargs) -> None:
    draw.rounded_rectangle(tuple(scale(v) for v in box), radius=scale(radius), **kwargs)


def linear_gradient(size: int, top: tuple[int, int, int, int], bottom: tuple[int, int, int, int]) -> Image.Image:
    gradient = Image.new("RGBA", (1, size))
    pixel = gradient.load()
    for y in range(size):
        t = y / max(1, size - 1)
        pixel[0, y] = tuple(
            round(top[channel] * (1.0 - t) + bottom[channel] * t)
            for channel in range(4)
        )
    return gradient.resize((size, size))


def ellipse_point(degrees: float) -> tuple[float, float]:
    angle = math.radians(degrees)
    x = CENTER + RING_RX * math.cos(angle)
    y = RING_CY + RING_RY * math.sin(angle)
    rotation = math.radians(RING_ROTATION_DEGREES)
    translated_x = x - CENTER
    translated_y = y - RING_CY
    rotated_x = translated_x * math.cos(rotation) - translated_y * math.sin(rotation)
    rotated_y = translated_x * math.sin(rotation) + translated_y * math.cos(rotation)
    return CENTER + rotated_x, RING_CY + rotated_y


def ring_arc_layer(start_degrees: float, end_degrees: float) -> Image.Image:
    layer = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    draw.arc(
        (scale(CENTER - RING_RX), scale(RING_CY - RING_RY), scale(CENTER + RING_RX), scale(RING_CY + RING_RY)),
        start=start_degrees,
        end=end_degrees,
        fill=(234, 247, 255, 245),
        width=scale(RING_WIDTH),
    )
    return layer.rotate(
        -RING_ROTATION_DEGREES,
        resample=Image.Resampling.BICUBIC,
        center=(scale(CENTER), scale(RING_CY)),
    )


CURSOR_POINTS = [
    (101, 90),
    (101, 159),
    (124, 145),
    (140, 169),
    (153, 161),
    (139, 137),
    (164, 130),
]
CURSOR_OUTLINE = 1.5
CURSOR_RADIUS = 3


def rounded_polygon(points: list[tuple[float, float]], radius: float, segments: int) -> list[tuple[float, float]]:
    rounded_points = []
    for index, point in enumerate(points):
        previous_point = points[index - 1]
        next_point = points[(index + 1) % len(points)]
        to_previous = (previous_point[0] - point[0], previous_point[1] - point[1])
        to_next = (next_point[0] - point[0], next_point[1] - point[1])
        previous_distance = math.hypot(to_previous[0], to_previous[1])
        next_distance = math.hypot(to_next[0], to_next[1])
        corner_radius = min(radius, previous_distance / 2, next_distance / 2)
        start = (
            point[0] + to_previous[0] / previous_distance * corner_radius,
            point[1] + to_previous[1] / previous_distance * corner_radius,
        )
        end = (
            point[0] + to_next[0] / next_distance * corner_radius,
            point[1] + to_next[1] / next_distance * corner_radius,
        )
        for step in range(segments + 1):
            t = step / segments
            rounded_points.append((
                (1 - t) * (1 - t) * start[0] + 2 * (1 - t) * t * point[0] + t * t * end[0],
                (1 - t) * (1 - t) * start[1] + 2 * (1 - t) * t * point[1] + t * t * end[1],
            ))
    return rounded_points


def cursor_outline_points() -> list[tuple[float, float]]:
    return rounded_polygon(CURSOR_POINTS, CURSOR_RADIUS, 10)


def cursor_path_data(points: list[tuple[float, float]], close: bool) -> str:
    commands = [f"M{points[0][0]:.2f} {points[0][1]:.2f}"]
    commands.extend(f"L{x:.2f} {y:.2f}" for x, y in points[1:])
    if not close:
        return " ".join(commands)
    commands.append("Z")
    return " ".join(commands)


def draw_brand(size: int) -> Image.Image:
    canvas = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    draw = ImageDraw.Draw(canvas)

    rounded_rectangle(
        draw,
        (22, 22, 234, 234),
        48,
        fill=(25, 29, 34, 255),
        outline=(52, 59, 67, 255),
        width=scale(2),
    )

    canvas.alpha_composite(ring_arc_layer(180, 360))

    orb_box = (scale(CENTER - 58), scale(RING_CY - 58), scale(CENTER + 58), scale(RING_CY + 58))
    gradient = linear_gradient(
        orb_box[3] - orb_box[1],
        (78, 166, 240, 255),
        (63, 185, 80, 255),
    )
    mask = Image.new("L", (orb_box[2] - orb_box[0], orb_box[3] - orb_box[1]), 0)
    mask_draw = ImageDraw.Draw(mask)
    mask_draw.ellipse((0, 0, mask.width, mask.height), fill=255)
    canvas.paste(gradient, (orb_box[0], orb_box[1]), mask)

    glow = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    glow_draw = ImageDraw.Draw(glow)
    glow_draw.ellipse((scale(88), scale(84), scale(156), scale(134)), fill=(255, 255, 255, 72))
    glow = glow.filter(ImageFilter.GaussianBlur(scale(18)))
    canvas.alpha_composite(glow)

    canvas.alpha_composite(ring_arc_layer(0, 180))

    pointer = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    outline_points = cursor_outline_points()
    scaled_outline = [(scale(x), scale(y)) for x, y in outline_points]

    shadow = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    shadow_draw = ImageDraw.Draw(shadow)
    shadow_draw.polygon(
        [(x, y + scale(5)) for x, y in scaled_outline],
        fill=(7, 16, 24, 76),
    )
    shadow = shadow.filter(ImageFilter.GaussianBlur(scale(4)))
    pointer.alpha_composite(shadow)

    cursor_box = (scale(96), scale(85), scale(169), scale(174))
    cursor_gradient = linear_gradient(
        cursor_box[3] - cursor_box[1],
        (255, 255, 255, 255),
        (221, 244, 255, 255),
    )
    cursor_mask = Image.new("L", (CANVAS, CANVAS), 0)
    cursor_mask_draw = ImageDraw.Draw(cursor_mask)
    cursor_mask_draw.polygon(scaled_outline, fill=255)
    gradient_layer = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    gradient_layer.paste(cursor_gradient, (cursor_box[0], cursor_box[1]))
    pointer.paste(gradient_layer, (0, 0), cursor_mask)

    canvas.alpha_composite(pointer)

    return canvas.resize((size, size), Image.Resampling.LANCZOS)


def main() -> None:
    ROOT.mkdir(parents=True, exist_ok=True)
    front_start = ellipse_point(0)
    front_end = ellipse_point(180)
    outline_points = cursor_outline_points()
    (ROOT / "soul-computer-use.svg").write_text(
        SVG.format(
            front_start_x=front_start[0],
            front_start_y=front_start[1],
            front_end_x=front_end[0],
            front_end_y=front_end[1],
            cursor_shadow_path=cursor_path_data(outline_points, True),
            cursor_outline_path=cursor_path_data(outline_points, True),
        ),
        encoding="utf-8",
    )

    source = draw_brand(BASE_SIZE)
    for size in (16, 24, 32, 48, 64, 128, 256, 512):
        source.resize((size, size), Image.Resampling.LANCZOS).save(ROOT / f"soul-computer-use-{size}.png")

    source.save(
        ROOT / "soul-computer-use.ico",
        format="ICO",
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )


if __name__ == "__main__":
    main()
