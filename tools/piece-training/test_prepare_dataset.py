"""Developer-only exporter validation using bounded synthetic image headers."""

import hashlib
import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch
import zlib

import prepare_dataset as exporter


def png(width=100, height=80):
    ihdr = b"IHDR" + struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + struct.pack(">I", 13) + ihdr + struct.pack(">I", zlib.crc32(ihdr) & 0xffffffff)


def jpeg(width=100, height=80):
    # APP segment followed by baseline frame header; pixel decoding is deliberately out of scope.
    frame = bytes([8]) + struct.pack(">HHB", height, width, 3) + b"\x01\x11\x00\x02\x11\x00\x03\x11\x00"
    return b"\xff\xd8\xff\xe0\x00\x04xx\xff\xc0" + struct.pack(">H", len(frame) + 2) + frame


def exif_segment(orientation=1, endian="little", *, field_type=3, count=1):
    byte_order, fmt = (b"II", "<") if endian == "little" else (b"MM", ">")
    tiff = byte_order + struct.pack(fmt + "HIH", 42, 8, 1)
    tiff += struct.pack(fmt + "HHI", 0x0112, field_type, count) + struct.pack(fmt + "H", orientation) + b"\0\0"
    tiff += struct.pack(fmt + "I", 0)
    payload = b"Exif\0\0" + tiff
    return b"\xff\xe1" + struct.pack(">H", len(payload) + 2) + payload


def image(name="board.png", *, group="session-one", split="train", boxes=None):
    return {"fileName": name, "width": 100, "height": 80, "group": group,
            "split": split, "reviewed": True, "boxes": [] if boxes is None else boxes}


def train():
    return {"kind": "train", "color": "black", "x": 10.5, "y": 20.25, "width": 30.5, "height": 12.75}


class DatasetTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.images = self.root / "images"
        self.images.mkdir()
        self.labels = self.root / "labels.json"
        self.output = self.root / "dataset"
        (self.images / "board.png").write_bytes(png())

    def write_labels(self, images):
        self.labels.write_text(json.dumps({"version": 1, "images": images}), encoding="utf-8")

    def export(self, images):
        self.write_labels(images)
        return exporter.export_dataset(self.labels, self.images, self.output)

    def test_success_keeps_exact_float_coordinates_colors_hashes_and_image_bytes(self):
        (self.images / "negative.jpg").write_bytes(jpeg())
        marker = {"kind": "player-marker", "color": "green", "x": 70, "y": 60, "width": 10, "height": 10}
        first = image(boxes=[train(), marker])
        first["sha256"] = hashlib.sha256(png()).hexdigest().upper()
        summary = self.export([first, image("negative.jpg", group="session-two", split="validation")])
        coco = json.loads((self.output / "annotations/instances_train2017.json").read_text())
        self.assertEqual(coco["annotations"][0]["bbox"], [10.5, 20.25, 30.5, 12.75])
        self.assertEqual(coco["annotations"][0]["area"], 30.5 * 12.75)
        self.assertEqual(coco["annotations"][1]["category_id"], 2)
        self.assertEqual(coco["annotations"][1]["color"], "green")
        self.assertEqual(coco["images"][0]["group"], "session-one")
        self.assertEqual(coco["categories"], exporter.CATEGORIES)
        self.assertEqual(summary["splits"]["train"]["positiveImages"], 1)
        self.assertEqual(summary["splits"]["validation"]["negativeImages"], 1)
        self.assertEqual(summary["splits"]["test"]["images"], 0)
        self.assertEqual(summary["totalBoxes"], 2)
        self.assertEqual(summary["images"][0]["sha256"], hashlib.sha256(png()).hexdigest())
        self.assertEqual(summary["labelsSha256"], hashlib.sha256(self.labels.read_bytes()).hexdigest())
        self.assertEqual((self.output / "train2017/board.png").read_bytes(), png())
        self.assertEqual((self.output / "val2017/negative.jpg").read_bytes(), jpeg())
        self.assertTrue((self.output / "annotations/instances_test2017.json").is_file())
        self.assertFalse(list(self.root.glob(".piece-dataset-*")))

    def test_reviewed_empty_image_is_exported_as_negative(self):
        summary = self.export([image()])
        self.assertEqual(summary["totalBoxes"], 0)
        self.assertEqual(summary["splits"]["train"]["negativeImages"], 1)

    def test_group_leakage_is_rejected_case_insensitively(self):
        with self.assertRaisesRegex(exporter.DatasetError, "leaks"):
            self.export([image(), image("second.png", group="SESSION-ONE", split="test")])
        self.assertFalse(self.output.exists())

    def test_unreviewed_or_missing_review_is_rejected(self):
        for value in (False, None, "true", 1):
            with self.subTest(value=value):
                data = image()
                data["reviewed"] = value
                with self.assertRaisesRegex(exporter.DatasetError, "reviewed"):
                    self.export([data])

    def test_unassigned_group_is_rejected(self):
        with self.assertRaisesRegex(exporter.DatasetError, "group"):
            self.export([image(group="Unassigned")])

    def test_path_traversal_drives_and_windows_special_names_are_rejected(self):
        for name in ("../board.png", "..\\board.png", "/board.png", "C:\\board.png", "C:board.png",
                     "CON.png", "CON .png", "nul.JPG", "COM1.png", "LPT².png", "board.png ", "x:ads.png", "sub/board.png",
                     "name\x00.png", "*.png", "board.png."):
            with self.subTest(name=name), self.assertRaises(exporter.DatasetError):
                self.export([image(name)])
        self.assertFalse(self.output.exists())

    def test_duplicate_names_are_rejected_case_insensitively(self):
        with self.assertRaisesRegex(exporter.DatasetError, "Duplicate image"):
            self.export([image(), image("BOARD.PNG")])

    def test_bad_dimensions_are_rejected(self):
        for value in (0, -1, 16385, True, 100.0, float("inf")):
            with self.subTest(value=value):
                data = image()
                data["width"] = value
                with self.assertRaises(exporter.DatasetError):
                    self.export([data])

    def test_boxes_must_be_finite_positive_and_in_source_bounds(self):
        changes = [("x", -1), ("y", -0.1), ("width", 0), ("height", -1), ("x", float("nan")),
                   ("height", float("inf")), ("x", True), ("width", 100), ("y", 79), ("kind", "printed-track"),
                   ("color", "orange")]
        for field, value in changes:
            with self.subTest(field=field, value=value):
                box = train()
                box[field] = value
                with self.assertRaises(exporter.DatasetError):
                    self.export([image(boxes=[box])])

    def test_box_touching_image_boundary_is_valid(self):
        box = train()
        box.update(x=0, y=0, width=100, height=80)
        self.assertEqual(self.export([image(boxes=[box])])["totalBoxes"], 1)

    def test_header_dimension_mismatch_removes_only_owned_temporary_output(self):
        unrelated = self.root / ".piece-dataset-unrelated"
        unrelated.mkdir()
        (unrelated / "keep.txt").write_text("keep")
        data = image()
        data["height"] = 81
        with self.assertRaisesRegex(exporter.DatasetError, "dimensions disagree"):
            self.export([data])
        self.assertFalse(self.output.exists())
        self.assertEqual(list(self.root.glob(".piece-dataset-*")), [unrelated])
        self.assertEqual((unrelated / "keep.txt").read_text(), "keep")

    def test_source_hash_mismatch_rejects_changed_image(self):
        data = image()
        data["sha256"] = "0" * 64
        with self.assertRaisesRegex(exporter.DatasetError, "SHA-256 mismatch"):
            self.export([data])
        self.assertFalse(self.output.exists())
        self.assertFalse(list(self.root.glob(".piece-dataset-*")))

    def test_malformed_optional_hash_is_rejected(self):
        for digest in ("abcd", "g" * 64, 3):
            data = image()
            data["sha256"] = digest
            with self.subTest(digest=digest), self.assertRaisesRegex(exporter.DatasetError, "SHA-256"):
                self.export([data])

    def test_output_collision_preserves_existing_contents(self):
        self.output.mkdir()
        (self.output / "existing.txt").write_text("untouched")
        with self.assertRaisesRegex(exporter.DatasetError, "already exists"):
            self.export([image()])
        self.assertEqual((self.output / "existing.txt").read_text(), "untouched")

    def test_output_inside_source_is_rejected(self):
        self.output = self.images / "nested-dataset"
        with self.assertRaisesRegex(exporter.DatasetError, "outside"):
            self.export([image()])

    def test_output_created_during_preparation_is_preserved(self):
        original_write = exporter._write_json

        def add_collision(path, value):
            original_write(path, value)
            if path.name == "dataset-summary.json":
                self.output.mkdir()
                (self.output / "keep.txt").write_text("external output")

        with patch.object(exporter, "_write_json", side_effect=add_collision):
            with self.assertRaisesRegex(exporter.DatasetError, "appeared"):
                self.export([image()])
        self.assertEqual((self.output / "keep.txt").read_text(), "external output")
        self.assertFalse(list(self.root.glob(".piece-dataset-*")))

    def test_malformed_png_checksum_and_jpeg_header_are_rejected(self):
        for header in (b"not an image", png()[:20], png()[:-1] + b"x", b"\xff\xd8\xff\xe0\x00\x01",
                       b"\xff\xd8\xff\xc0\x00\x08" + b"\x08\x00\x50\x00\x64\x03"):
            with self.subTest(header=header), self.assertRaises(exporter.DatasetError):
                exporter.image_dimensions(header)

    def test_wrong_extension_rejected(self):
        (self.images / "board.png").write_bytes(jpeg())
        with self.assertRaisesRegex(exporter.DatasetError, "extension"):
            self.export([image()])

    def test_jpeg_without_exif_and_upright_exif_are_allowed(self):
        self.assertEqual(exporter.image_dimensions(jpeg()), ("jpeg", 100, 80))
        for endian in ("little", "big"):
            with self.subTest(endian=endian):
                content = jpeg()[:2] + exif_segment(1, endian) + jpeg()[2:]
                self.assertEqual(exporter.image_dimensions(content), ("jpeg", 100, 80))

    def test_jpeg_rotated_exif_is_rejected_even_when_dimensions_do_not_change(self):
        for orientation in (3, 6):
            for endian in ("little", "big"):
                with self.subTest(orientation=orientation, endian=endian):
                    content = jpeg()[:2] + exif_segment(orientation, endian) + jpeg()[2:]
                    (self.images / "rotated.jpg").write_bytes(content)
                    with self.assertRaisesRegex(exporter.DatasetError, "upright PNG"):
                        self.export([image("rotated.jpg")])
                    self.assertFalse(self.output.exists())

    def test_jpeg_orientation_metadata_after_frame_header_is_checked(self):
        with self.assertRaisesRegex(exporter.DatasetError, "upright PNG"):
            exporter.image_dimensions(jpeg() + exif_segment(3) + b"\xff\xda")

    def test_malformed_exif_orientation_metadata_is_rejected(self):
        malformed = [exif_segment(field_type=4), exif_segment(count=2),
                     b"\xff\xe1\x00\x06Exif", b"\xff\xe1\x00\x10Exif\0\0II\x2a\x00\xff\xff\xff\xff"]
        # A cyclic linked directory must not cause unbounded traversal.
        cyclic = bytearray(exif_segment())
        cyclic[-4:] = struct.pack("<I", 8)
        malformed.append(bytes(cyclic))
        for segment in malformed:
            with self.subTest(segment=segment), self.assertRaisesRegex(exporter.DatasetError, "upright PNG"):
                exporter.image_dimensions(jpeg()[:2] + segment + jpeg()[2:])

    def test_size_bounds_are_enforced(self):
        with patch.object(exporter, "MAX_IMAGE_BYTES", 10):
            with self.assertRaisesRegex(exporter.DatasetError, "image limit"):
                self.export([image()])
        with patch.object(exporter, "MAX_LABEL_BYTES", 10):
            with self.assertRaisesRegex(exporter.DatasetError, "Labels file"):
                self.export([image()])
        with patch.object(exporter, "MAX_BOXES_PER_IMAGE", 0):
            with self.assertRaisesRegex(exporter.DatasetError, "boxes"):
                self.export([image(boxes=[train()])])

    def test_duplicate_json_keys_are_rejected(self):
        self.labels.write_text('{"version":1,"version":1,"images":[]}')
        with self.assertRaisesRegex(exporter.DatasetError, "Duplicate JSON key"):
            exporter.read_labels(self.labels)

    def test_symlink_source_is_rejected_when_platform_allows_it(self):
        target = self.images / "alias.png"
        try:
            target.symlink_to(self.images / "board.png")
        except OSError:
            self.skipTest("Creating symlinks requires privileges on this host")
        with self.assertRaisesRegex(exporter.DatasetError, "regular file"):
            self.export([image("alias.png")])

    def test_source_symlink_guard_does_not_require_symlink_creation_privilege(self):
        with patch.object(Path, "is_symlink", return_value=True):
            with self.assertRaisesRegex(exporter.DatasetError, "regular file"):
                exporter._read_image(self.images.resolve(), "board.png")

    def test_init_inventory_has_hash_and_is_unreviewed(self):
        (self.images / "extra.jpg").write_bytes(jpeg())
        inventory = exporter.init_labels(self.images, self.labels)
        self.assertEqual(len(inventory["images"]), 2)
        self.assertEqual(inventory["images"][0]["sha256"], hashlib.sha256(png()).hexdigest())
        self.assertTrue(all(entry["reviewed"] is False and entry["group"] == "unassigned" and not entry["boxes"]
                            for entry in inventory["images"]))
        before = self.labels.read_bytes()
        with self.assertRaisesRegex(exporter.DatasetError, "already exists"):
            exporter.init_labels(self.images, self.labels)
        self.assertEqual(self.labels.read_bytes(), before)
        with self.assertRaisesRegex(exporter.DatasetError, "reviewed"):
            exporter.export_dataset(self.labels, self.images, self.output)

    def test_cli_reports_validation_failure_without_publishing_dataset(self):
        self.write_labels([image(group="unassigned")])
        self.assertEqual(exporter.main(["--labels", str(self.labels), "--images", str(self.images),
                                        "--output", str(self.output)]), 1)
        self.assertFalse(self.output.exists())


if __name__ == "__main__":
    unittest.main()
