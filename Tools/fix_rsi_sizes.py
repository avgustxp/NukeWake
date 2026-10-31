#!/usr/bin/env python3
"""Synchronise RSI meta.json size with the real PNG frame size.

For every ``*.rsi`` directory under the given root the script reads
``meta.json``, finds the PNG behind the state called ``image`` (or the only
state, when there is just one) and rewrites ``size.x`` / ``size.y`` so that
they match the actual frame size of the image.

Only the two numbers inside the ``"size"`` block are touched, so the rest of
the file keeps its original formatting and CRLF line endings.

Examples
--------
    # show what would change, change nothing
    python Tools/fix_rsi_sizes.py

    # actually rewrite the metadata
    python Tools/fix_rsi_sizes.py --apply

    # CI mode: fail if any meta.json is out of sync
    python Tools/fix_rsi_sizes.py --check
"""

from __future__ import annotations

import argparse
import json
import re
import struct
import sys
from pathlib import Path

DEFAULT_ROOT = Path("Resources/Textures/_NukeWake/icons")

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"

# "size": { "x": 113, "y": 112 }  (indentation and key order may differ)
SIZE_RE = re.compile(
    r'("size"\s*:\s*\{\s*)'
    r'("x"\s*:\s*)(\d+)'
    r'(\s*,\s*)'
    r'("y"\s*:\s*)(\d+)',
    re.DOTALL,
)


def png_size(path: Path) -> tuple[int, int]:
    """Return (width, height) from the IHDR chunk without any image library."""
    with path.open("rb") as handle:
        head = handle.read(24)

    if len(head) < 24 or head[:8] != PNG_SIGNATURE:
        raise ValueError(f"not a PNG file: {path}")

    width, height = struct.unpack(">II", head[16:24])
    return int(width), int(height)


def frame_count(state: dict) -> int:
    """Number of frames in a state (animation frames x directions)."""
    delays = state.get("delays")
    if not delays:
        return 1

    # "delays" is a list per direction: [[0.1, 0.1], [0.1, 0.1], ...]
    if isinstance(delays[0], (int, float)):
        return len(delays)
    return sum(len(direction) for direction in delays)


def frame_size(
    png: tuple[int, int],
    frames: int,
    known: tuple[int, int] | None = None,
) -> tuple[tuple[int, int], str | None]:
    """Work out the size of a single frame inside a state PNG.

    A state PNG may pack several frames into a grid. When the frame size is
    already correct (``known`` divides the PNG and the grid holds exactly
    ``frames`` frames) it is kept as is. Otherwise a horizontal strip is
    assumed, which is how the RSI tools animate states.
    """
    width, height = png

    if frames <= 1:
        return (width, height), None

    if known and known[0] > 0 and known[1] > 0:
        columns = width // known[0]
        rows = height // known[1]
        if (
            width % known[0] == 0
            and height % known[1] == 0
            and columns * rows == frames
        ):
            return known, None

    if width % frames == 0:
        return (width // frames, height), None
    if height % frames == 0:
        return (width, height // frames), None

    return (width, height), (
        f"{frames} frames do not fit {width}x{height}; using the whole image"
    )


def pick_state(meta: dict) -> dict:
    """The state whose image defines the frame size."""
    states = meta.get("states") or []
    if not states:
        raise ValueError("no states in meta.json")

    for state in states:
        if state.get("name") == "image":
            return state

    return states[0]


def state_png(state: dict, rsi_dir: Path) -> Path:
    name = state.get("name")
    if not name:
        raise ValueError("state without a name in meta.json")

    frame = rsi_dir / f"{name}.png"
    if not frame.is_file():
        raise ValueError(f"missing frame for state '{name}': {frame}")

    return frame


def check_state_sizes(
    meta: dict,
    rsi_dir: Path,
    expected: tuple[int, int],
) -> list[str]:
    """Return warnings for states whose frame size differs from the chosen one."""
    warnings: list[str] = []

    for state in meta.get("states") or []:
        name = state.get("name") or "<unnamed>"
        try:
            frame = state_png(state, rsi_dir)
            size, note = frame_size(png_size(frame), frame_count(state), expected)
        except (ValueError, OSError) as error:
            warnings.append(f"state '{name}': {error}")
            continue

        if note:
            warnings.append(f"state '{name}': {note}")
        if size != expected:
            warnings.append(
                f"state '{name}': {size[0]}x{size[1]} != {expected[0]}x{expected[1]}"
            )

    return warnings


def sync_meta(path: Path, apply: bool) -> tuple[str, list[str]]:
    """Return (status, messages).

    Status: ``unchanged`` / ``updated`` / ``would-update`` / ``error``.
    """
    rsi_dir = path.parent
    raw = path.read_text(encoding="utf-8", newline="")

    try:
        meta = json.loads(raw)
    except json.JSONDecodeError as error:
        return "error", [f"invalid JSON: {error}"]

    match = SIZE_RE.search(raw)
    old_size = None
    if match is not None:
        old_size = (int(match.group(3)), int(match.group(6)))

    try:
        state = pick_state(meta)
        width, height = png_size(state_png(state, rsi_dir))
        size, note = frame_size((width, height), frame_count(state), old_size)
    except (ValueError, OSError) as error:
        return "error", [str(error)]

    messages = check_state_sizes(meta, rsi_dir, size)
    messages = [f"warning: {message}" for message in messages]
    if note:
        messages.append(f"warning: {note}")

    if match is None:
        messages.append('warning: no "size" block, rebuilding meta.json')
        if not apply:
            messages.append(f"would set size to {size[0]}x{size[1]}")
            return "would-update", messages

        newest: dict = {}
        for key, value in meta.items():
            if key == "size":
                continue
            newest[key] = value
            # Keep "size" close to the header fields.
            if key == "copyright":
                newest["size"] = {"x": size[0], "y": size[1]}
        newest.setdefault("size", {"x": size[0], "y": size[1]})

        newline = "\r\n" if "\r\n" in raw else "\n"
        text = json.dumps(newest, indent=2, ensure_ascii=False)
        path.write_text(text.replace("\n", newline) + newline, encoding="utf-8", newline="")
        return "updated", messages

    assert old_size is not None
    if old_size == size:
        return "unchanged", messages

    state_name = state.get("name", "<unnamed>")
    messages.append(
        f"size {old_size[0]}x{old_size[1]} -> {size[0]}x{size[1]} (state '{state_name}')"
    )
    if not apply:
        return "would-update", messages

    new_raw = SIZE_RE.sub(
        lambda m: f"{m.group(1)}{m.group(2)}{size[0]}{m.group(4)}{m.group(5)}{size[1]}",
        raw,
        count=1,
    )
    path.write_text(new_raw, encoding="utf-8", newline="")
    return "updated", messages


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--root",
        type=Path,
        default=DEFAULT_ROOT,
        help=f"directory to scan recursively (default: {DEFAULT_ROOT})",
    )
    parser.add_argument(
        "--apply",
        action="store_true",
        help="rewrite meta.json files (default: dry run)",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="exit with code 1 when anything is out of sync (implies dry run)",
    )
    parser.add_argument(
        "--quiet",
        action="store_true",
        help="print only changed/errored files",
    )
    args = parser.parse_args(argv)

    if not args.root.is_dir():
        parser.error(f"root directory not found: {args.root}")

    apply = args.apply and not args.check
    metas = sorted(args.root.rglob("meta.json"))
    if not metas:
        parser.error(f"no meta.json found under {args.root}")

    counts = {"unchanged": 0, "updated": 0, "would-update": 0, "error": 0}

    for meta_path in metas:
        status, messages = sync_meta(meta_path, apply)
        counts[status] += 1
        if status == "unchanged" and args.quiet and not messages:
            continue

        relative = meta_path.parent.relative_to(args.root)
        prefix = {
            "unchanged": "ok",
            "updated": "fixed",
            "would-update": "todo",
            "error": "ERROR",
        }[status]
        print(f"[{prefix}] {relative}")
        for message in messages:
            print(f"    {message}")

    print(
        f"\n{len(metas)} RSI: "
        f"{counts['unchanged']} ok, "
        f"{counts['updated'] + counts['would-update']} out of sync "
        f"({'updated' if apply else 'dry run'}), "
        f"{counts['error']} error(s)"
    )

    if args.check and (counts["would-update"] or counts["error"]):
        return 1
    return 1 if counts["error"] else 0


if __name__ == "__main__":
    sys.exit(main())
