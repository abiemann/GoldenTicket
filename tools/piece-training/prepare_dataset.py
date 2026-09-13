#!/usr/bin/env python3
"""Prepare explicitly reviewed GoldenTicket labels for offline COCO training.

No image decoding, transformations, detection, downloads, or training occur here.
PNG/JPEG header checks establish format and dimensions, not complete decodability.
JPEG EXIF rotation/mirroring is rejected: export an upright PNG before labeling.
All captures from one session/setup must share a group to prevent split leakage.
Use --init-labels to inventory source images; review and label every image before
export. Image files are copied unchanged, with SHA-256 recorded in the summary.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import shutil
import stat
import struct
import sys
import tempfile
import zlib

MAX_LABEL_BYTES = 64 * 1024 * 1024
MAX_IMAGE_BYTES = 48 * 1024 * 1024
MAX_TOTAL_IMAGE_BYTES = 32 * 1024 * 1024 * 1024
MAX_HEADER_BYTES = 1024 * 1024
MAX_IMAGES = 10_000
MAX_BOXES_PER_IMAGE = 1_000
MAX_TOTAL_BOXES = 1_000_000
MAX_DIMENSION = 16_384
SPLITS = {"train": "train2017", "validation": "val2017", "test": "test2017"}
CATEGORIES = [{"id": 1, "name": "train"}, {"id": 2, "name": "player-marker"}]
COLORS = {"black", "blue", "green", "red", "yellow", "unknown"}
EXTENSIONS = {".png", ".jpg", ".jpeg"}
_RESERVED = {"CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$"}
_RESERVED.update(f"{prefix}{digit}" for prefix in ("COM", "LPT") for digit in "123456789¹²³")


class DatasetError(ValueError):
    """Invalid or unreviewed input; no completed dataset should be published."""


def _filename(value: object) -> str:
    if not isinstance(value, str) or not value or len(value) > 240:
        raise DatasetError("fileName must be a plain filename of 1–240 characters")
    if (value in {".", ".."} or value[-1] in " ." or
            any(ord(c) < 32 or c in '<>:"/\\|?*' for c in value) or
            value.split(".", 1)[0].rstrip(" .").upper() in _RESERVED):
        raise DatasetError(f"Unsafe image filename: {value!r}")
    if Path(value).suffix.lower() not in EXTENSIONS:
        raise DatasetError(f"Only PNG and JPEG source files are supported: {value!r}")
    return value


def _number(value: object, field: str, *, positive: bool = False) -> int | float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise DatasetError(f"{field} must be a finite number")
    try:
        finite = math.isfinite(value)
    except OverflowError:
        finite = False
    if not finite or value < 0 or (positive and value == 0):
        raise DatasetError(f"{field} must be finite and {'positive' if positive else 'nonnegative'}")
    return value


def _dimension(value: object, field: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or not 1 <= value <= MAX_DIMENSION:
        raise DatasetError(f"{field} must be an integer in 1..{MAX_DIMENSION}")
    return value


def _unique_object(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            raise DatasetError(f"Duplicate JSON key: {key}")
        result[key] = value
    return result


def _reject_constant(value: str) -> None:
    raise DatasetError(f"Invalid JSON number: {value}")


def read_labels(path: Path) -> tuple[dict, str]:
    with path.open("rb") as source:
        raw = source.read(MAX_LABEL_BYTES + 1)
    if len(raw) > MAX_LABEL_BYTES:
        raise DatasetError("Labels file exceeds the 64 MiB limit")
    try:
        data = json.loads(raw, object_pairs_hook=_unique_object, parse_constant=_reject_constant)
    except DatasetError:
        raise
    except (UnicodeError, ValueError, RecursionError) as error:
        raise DatasetError(f"Invalid labels JSON: {error}") from error
    return data, hashlib.sha256(raw).hexdigest()


def validate_labels(data: object) -> list[dict]:
    if not isinstance(data, dict) or type(data.get("version")) is not int or data["version"] != 1:
        raise DatasetError("Labels must be an object with version: 1")
    images = data.get("images")
    if not isinstance(images, list) or not 1 <= len(images) <= MAX_IMAGES:
        raise DatasetError(f"images must contain 1..{MAX_IMAGES} entries")
    names, groups, total_boxes = set(), {}, 0
    for image in images:
        if not isinstance(image, dict):
            raise DatasetError("Each image must be an object")
        name = _filename(image.get("fileName"))
        if name.casefold() in names:
            raise DatasetError(f"Duplicate image filename (case insensitive): {name}")
        names.add(name.casefold())
        width = _dimension(image.get("width"), f"{name}.width")
        height = _dimension(image.get("height"), f"{name}.height")
        if image.get("reviewed") is not True:
            raise DatasetError(f"Image must be explicitly reviewed before export: {name}")
        group = image.get("group")
        if (not isinstance(group, str) or not group.strip() or group != group.strip() or len(group) > 128 or
                any(ord(c) < 32 for c in group) or group.casefold() == "unassigned"):
            raise DatasetError(f"Assign a nonempty capture group before exporting: {name}")
        split = image.get("split")
        if not isinstance(split, str) or split not in SPLITS:
            raise DatasetError(f"Invalid split for {name}; choose train, validation, or test")
        previous_split = groups.setdefault(group.casefold(), split)
        if previous_split != split:
            raise DatasetError(f"Capture group {group!r} leaks across dataset splits")
        digest = image.get("sha256")
        if digest is not None and (not isinstance(digest, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", digest)):
            raise DatasetError(f"Invalid optional SHA-256 for {name}")
        boxes = image.get("boxes")
        if not isinstance(boxes, list) or len(boxes) > MAX_BOXES_PER_IMAGE:
            raise DatasetError(f"boxes must be an array with at most {MAX_BOXES_PER_IMAGE} entries: {name}")
        total_boxes += len(boxes)
        if total_boxes > MAX_TOTAL_BOXES:
            raise DatasetError("Dataset has too many boxes")
        for box in boxes:
            if not isinstance(box, dict) or box.get("kind") not in ("train", "player-marker"):
                raise DatasetError(f"Invalid box kind in {name}")
            if not isinstance(box.get("color"), str) or box["color"] not in COLORS:
                raise DatasetError(f"Invalid box color in {name}")
            x, y = (_number(box.get(field), field) for field in ("x", "y"))
            bw, bh = (_number(box.get(field), field, positive=True) for field in ("width", "height"))
            if x + bw > width or y + bh > height:
                raise DatasetError(f"Box extends outside source image: {name}")
    return images


def _check_exif_orientation(payload: bytes) -> None:
    """Read bounded APP1 TIFF directories without decoding image or thumbnail data."""
    guidance = "Malformed JPEG EXIF orientation metadata; export an upright PNG before labeling"
    if not payload.startswith(b"Exif\0\0"):
        raise DatasetError(guidance)
    tiff = payload[6:]
    if len(tiff) < 8 or tiff[:2] not in (b"II", b"MM"):
        raise DatasetError(guidance)
    endian = "little" if tiff[:2] == b"II" else "big"
    if int.from_bytes(tiff[2:4], endian) != 42:
        raise DatasetError(guidance)
    offset = int.from_bytes(tiff[4:8], endian)
    if offset < 8:
        raise DatasetError(guidance)
    visited = set()
    while offset:
        # A JPEG APP1 segment is at most 65,533 bytes; tighter traversal limits
        # additionally reject cyclic or adversarial TIFF directory chains.
        if offset in visited or len(visited) >= 16 or offset < 8 or offset + 2 > len(tiff):
            raise DatasetError(guidance)
        visited.add(offset)
        count = int.from_bytes(tiff[offset:offset + 2], endian)
        end = offset + 2 + count * 12
        if count > 4096 or end + 4 > len(tiff):
            raise DatasetError(guidance)
        orientation_seen = False
        for position in range(offset + 2, end, 12):
            if int.from_bytes(tiff[position:position + 2], endian) != 0x0112:
                continue
            field_type = int.from_bytes(tiff[position + 2:position + 4], endian)
            value_count = int.from_bytes(tiff[position + 4:position + 8], endian)
            if orientation_seen or field_type != 3 or value_count != 1:
                raise DatasetError(guidance)
            orientation_seen = True
            orientation = int.from_bytes(tiff[position + 8:position + 10], endian)
            if orientation != 1:
                raise DatasetError(f"JPEG EXIF orientation {orientation} is not upright; export an upright PNG before labeling")
        offset = int.from_bytes(tiff[end:end + 4], endian)


def image_dimensions(header: bytes) -> tuple[str, int, int]:
    if header.startswith(b"\x89PNG\r\n\x1a\n"):
        if len(header) < 33 or header[8:16] != b"\0\0\0\rIHDR":
            raise DatasetError("Invalid PNG IHDR header")
        payload = header[16:29]
        if zlib.crc32(header[12:29]) & 0xffffffff != struct.unpack(">I", header[29:33])[0]:
            raise DatasetError("Invalid PNG IHDR checksum")
        width, height, depth, color, compression, filtering, interlace = struct.unpack(">IIBBBBB", payload)
        depths = {0: {1, 2, 4, 8, 16}, 2: {8, 16}, 3: {1, 2, 4, 8}, 4: {8, 16}, 6: {8, 16}}
        if depth not in depths.get(color, set()) or compression != 0 or filtering != 0 or interlace not in (0, 1):
            raise DatasetError("Unsupported or invalid PNG header fields")
        return "png", _dimension(width, "PNG width"), _dimension(height, "PNG height")
    if not header.startswith(b"\xff\xd8"):
        raise DatasetError("Image is not a PNG or JPEG by signature")
    position = 2
    dimensions = None
    header_complete = False
    sof_markers = {0xc0, 0xc1, 0xc2, 0xc3, 0xc5, 0xc6, 0xc7, 0xc9, 0xca, 0xcb, 0xcd, 0xce, 0xcf}
    while position < len(header):
        if header[position] != 0xff:
            raise DatasetError("Invalid JPEG marker header")
        while position < len(header) and header[position] == 0xff:
            position += 1
        if position >= len(header):
            break
        marker = header[position]
        position += 1
        if marker in (0xda, 0xd9):
            header_complete = True
            break
        if marker == 0x01 or 0xd0 <= marker <= 0xd7:
            continue
        if marker in (0x00, 0xd8) or position + 2 > len(header):
            raise DatasetError("Invalid JPEG marker")
        length = int.from_bytes(header[position:position + 2], "big")
        if length < 2 or position + length > len(header):
            raise DatasetError("Truncated JPEG header or header exceeds 1 MiB limit")
        if marker == 0xe1:
            payload = header[position + 2:position + length]
            if payload.startswith(b"Exif"):
                _check_exif_orientation(payload)
        if marker in sof_markers:
            if length < 8:
                raise DatasetError("Invalid JPEG frame header")
            precision = header[position + 2]
            height = int.from_bytes(header[position + 3:position + 5], "big")
            width = int.from_bytes(header[position + 5:position + 7], "big")
            components = header[position + 7]
            if precision not in (8, 12, 16) or not 1 <= components <= 4 or length != 8 + 3 * components:
                raise DatasetError("Invalid JPEG frame components")
            if dimensions is not None:
                raise DatasetError("Multiple JPEG frame headers are unsupported")
            dimensions = (_dimension(width, "JPEG width"), _dimension(height, "JPEG height"))
        position += length
    # Continue past SOF to inspect APP1 segments before the scan. Reaching the
    # bounded read limit cannot establish that later orientation metadata is absent.
    if dimensions is not None and (header_complete or len(header) < MAX_HEADER_BYTES):
        return "jpeg", *dimensions
    raise DatasetError("JPEG dimensions were not found within the bounded header")


def _source_root(path: Path) -> Path:
    root = path.resolve(strict=True)
    if not root.is_dir():
        raise DatasetError("--images must be an existing directory")
    return root


def _read_image(root: Path, name: str, destination: Path | None = None) -> dict:
    source_path = root / _filename(name)
    before = source_path.lstat()
    if not stat.S_ISREG(before.st_mode) or source_path.is_symlink() or source_path.resolve(strict=True).parent != root:
        raise DatasetError(f"Source image must be a regular file directly in --images: {name}")
    descriptor = os.open(source_path, os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0))
    with os.fdopen(descriptor, "rb") as source:
        opened = os.fstat(source.fileno())
        if (not stat.S_ISREG(opened.st_mode) or not os.path.samestat(before, opened) or
                not 1 <= opened.st_size <= MAX_IMAGE_BYTES):
            raise DatasetError(f"Source changed or exceeds the 48 MiB image limit: {name}")
        header = source.read(MAX_HEADER_BYTES)
        file_format, width, height = image_dimensions(header)
        if (file_format == "png") != (Path(name).suffix.lower() == ".png"):
            raise DatasetError(f"Image extension disagrees with its signature: {name}")
        source.seek(0)
        digest, count = hashlib.sha256(), 0
        target = destination.open("xb") if destination is not None else None
        try:
            while chunk := source.read(1024 * 1024):
                count += len(chunk)
                if count > MAX_IMAGE_BYTES:
                    raise DatasetError(f"Source image grew beyond the size limit: {name}")
                digest.update(chunk)
                if target is not None:
                    target.write(chunk)
        finally:
            if target is not None:
                target.close()
        after = os.fstat(source.fileno())
        if count != opened.st_size or after.st_size != opened.st_size or after.st_mtime_ns != opened.st_mtime_ns:
            raise DatasetError(f"Source image changed while it was read: {name}")
    return {"fileName": name, "width": width, "height": height, "sha256": digest.hexdigest(), "bytes": count}


def _write_json(path: Path, value: object) -> None:
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, indent=2, ensure_ascii=False, allow_nan=False)
        stream.write("\n")


def init_labels(images_directory: Path, destination: Path) -> dict:
    root = _source_root(images_directory)
    if os.path.lexists(destination):
        raise DatasetError("Labels destination already exists; no file was overwritten")
    names = []
    for entry in root.iterdir():
        if entry.suffix.lower() in EXTENSIONS:
            names.append(_filename(entry.name))
            if len(names) > MAX_IMAGES:
                raise DatasetError("Too many images to inventory")
    if not names:
        raise DatasetError("No PNG or JPEG source images found")
    if len({name.casefold() for name in names}) != len(names):
        raise DatasetError("Duplicate image filenames (case insensitive)")
    images, total_bytes = [], 0
    for name in sorted(names, key=str.casefold):
        details = _read_image(root, name)
        total_bytes += details.pop("bytes")
        if total_bytes > MAX_TOTAL_IMAGE_BYTES:
            raise DatasetError("Dataset exceeds total source image size limit")
        details.update(group="unassigned", split="train", reviewed=False, boxes=[])
        images.append(details)
    document = {"version": 1, "images": images}
    _write_json(destination, document)
    return document


def export_dataset(labels_path: Path, images_directory: Path, output: Path) -> dict:
    data, labels_hash = read_labels(labels_path)
    images = validate_labels(data)
    root = _source_root(images_directory)
    if os.path.lexists(output):
        raise DatasetError("Output already exists; choose a new directory")
    output = output.resolve(strict=False)
    if output == root or root in output.parents:
        raise DatasetError("Output must be outside the source images directory")
    if not output.parent.is_dir():
        raise DatasetError("Output parent directory must already exist")
    temporary = Path(tempfile.mkdtemp(prefix=".piece-dataset-", dir=output.parent)).resolve(strict=True)
    temporary_identity = temporary.stat()
    published = False
    try:
        annotations = temporary / "annotations"
        annotations.mkdir()
        datasets = {}
        split_counts = {}
        for split, directory in SPLITS.items():
            (temporary / directory).mkdir()
            datasets[split] = {"images": [], "annotations": [], "categories": CATEGORIES}
            split_counts[split] = {"images": 0, "positiveImages": 0, "negativeImages": 0, "boxes": 0,
                                   "trains": 0, "playerMarkers": 0}
        summary = {"version": 1, "labelsSha256": labels_hash, "images": [], "groupSplits": {},
                   "splits": split_counts, "totalImages": len(images), "totalBoxes": 0, "totalBytes": 0}
        annotation_id = 1
        for image_id, image in enumerate(images, 1):
            name, split, group = image["fileName"], image["split"], image["group"]
            details = _read_image(root, name, temporary / SPLITS[split] / name)
            if (details["width"], details["height"]) != (image["width"], image["height"]):
                raise DatasetError(f"Label dimensions disagree with source image header: {name}")
            if image.get("sha256") is not None and details["sha256"] != image["sha256"].lower():
                raise DatasetError(f"SHA-256 mismatch; review the changed source image: {name}")
            summary["totalBytes"] += details["bytes"]
            if summary["totalBytes"] > MAX_TOTAL_IMAGE_BYTES:
                raise DatasetError("Dataset exceeds total source image size limit")
            boxes = image["boxes"]
            summary["images"].append({**details, "group": group, "split": split, "boxes": len(boxes)})
            summary["groupSplits"][group] = split
            datasets[split]["images"].append({"id": image_id, "file_name": name, "width": image["width"],
                                               "height": image["height"], "group": group})
            counts = split_counts[split]
            counts["images"] += 1
            counts["positiveImages" if boxes else "negativeImages"] += 1
            counts["boxes"] += len(boxes)
            summary["totalBoxes"] += len(boxes)
            for box in boxes:
                category_id = 1 if box["kind"] == "train" else 2
                counts["trains" if category_id == 1 else "playerMarkers"] += 1
                datasets[split]["annotations"].append({
                    "id": annotation_id, "image_id": image_id, "category_id": category_id,
                    "bbox": [box["x"], box["y"], box["width"], box["height"]],
                    "area": box["width"] * box["height"], "iscrowd": 0, "color": box["color"]})
                annotation_id += 1
        for split, directory in SPLITS.items():
            _write_json(annotations / f"instances_{directory}.json", datasets[split])
        _write_json(temporary / "dataset-summary.json", summary)
        if os.path.lexists(output):
            raise DatasetError("Output appeared during preparation; refusing to overwrite it")
        os.rename(temporary, output)
        published = True
        return summary
    finally:
        # Delete only the exact directory created by this invocation, never a user path.
        if not published and temporary.exists() and not temporary.is_symlink():
            if temporary.parent == output.parent and os.path.samestat(temporary_identity, temporary.stat()):
                shutil.rmtree(temporary)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--images", type=Path, required=True, help="Local PNG/JPEG source directory")
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--labels", type=Path, help="Reviewed version-1 labels JSON to export")
    mode.add_argument("--init-labels", type=Path, help="Create an unreviewed inventory JSON; never overwrite")
    parser.add_argument("--output", type=Path, help="New COCO dataset directory outside --images")
    args = parser.parse_args(argv)
    if bool(args.labels) != bool(args.output):
        parser.error("--output is required with --labels and cannot be used with --init-labels")
    try:
        if args.init_labels:
            document = init_labels(args.images, args.init_labels)
            print(f"Inventoried {len(document['images'])} unreviewed images. Assign groups/splits and review every image before export.")
        else:
            summary = export_dataset(args.labels, args.images, args.output)
            print(f"Exported {summary['totalImages']} reviewed images and {summary['totalBoxes']} boxes to {args.output}")
    except (DatasetError, OSError) as error:
        print(f"Dataset preparation failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
