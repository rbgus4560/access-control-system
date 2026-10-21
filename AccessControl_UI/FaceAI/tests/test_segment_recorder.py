# 파일 역할: 합성 특징·합성 MP4 또는 모델 대역으로 동작을 확인하는 개발용 회귀 검사입니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

"""Deterministic recording lifecycle checks with an injected encoder.
These do not claim to test native OpenCV/MP4 codecs or a Windows camera.
"""

import json
from pathlib import Path
import sys
import tempfile
import time
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from segment_recorder import SegmentRecorder, atomic_json


class Frame:
    shape = (48, 64, 3)

    def copy(self):
        return self


class Codec:
    CAP_PROP_FRAME_COUNT, CAP_PROP_FPS = 7, 5

    def __init__(self, broken=False):
        self.files = {}
        self.broken = broken

    def VideoWriter_fourcc(self, *args):
        return 0

    def VideoWriter(self, path, fourcc, fps, size):
        owner = self
        owner.files[path] = dict(count=0, fps=fps, open=True)

        class Writer:
            def isOpened(self):
                return True

            def write(self, frame):
                if not owner.broken:
                    owner.files[path]["count"] += 1
                    with open(path, "ab") as stream:
                        stream.write(b"test-encoder-frame")

            def release(self):
                owner.files[path]["open"] = False

        return Writer()

    def VideoCapture(self, path):
        entry = self.files.get(path, dict(count=0, fps=10))

        class Reader:
            def isOpened(self):
                return entry["count"] > 0

            def read(self):
                return entry["count"] > 0, None

            def get(self, key):
                return entry["count"] if key == 7 else entry["fps"]

            def release(self):
                pass

        return Reader()


def feed(recorder, seconds):
    until = time.monotonic() + seconds
    while time.monotonic() < until:
        recorder.offer(Frame())
        time.sleep(0.025)


class RecordingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.codec = Codec()
        self.recorder = None

    def tearDown(self):
        if self.recorder is not None:
            self.recorder.close()
        self.temp.cleanup()

    def start(self, **kwargs):
        self.recorder = SegmentRecorder(self.root, self.codec, min_free_bytes=0, **kwargs)
        self.recorder.start()
        return self.recorder

    def entries(self):
        return [json.loads(p.read_text()) for p in sorted(self.root.glob("*/*.json"))]

    def test_partial_segment_finalizes_and_file_matches(self):
        r = self.start()
        feed(r, 0.24)
        r.close()
        (entry,) = self.entries()
        self.assertEqual(entry["Status"], "READY")
        self.assertGreater(entry["FrameCount"], 0)
        self.assertEqual(entry["FileSizeBytes"], Path(entry["FilePath"]).stat().st_size)
        self.assertTrue(entry["EndTime"] > entry["StartTime"])

    def test_rotation_has_unique_ids_and_bounded_count(self):
        r = self.start(seconds=0.3)
        feed(r, 0.8)
        r.close()
        entries = self.entries()
        self.assertGreaterEqual(len(entries), 2)
        self.assertEqual(len({e["SegmentId"] for e in entries}), len(entries))
        for entry in entries:
            self.assertEqual(entry["Status"], "READY")
            self.assertLessEqual(entry["FrameCount"], 3)
        entries.sort(key=lambda e: e["StartTime"])
        for first, second in zip(entries, entries[1:]):
            self.assertLessEqual(first["EndTime"], second["StartTime"])

    def test_capture_gap_closes_without_recording_black_gap(self):
        r = self.start()
        feed(r, 0.15)
        time.sleep(0.65)
        (first,) = self.entries()
        self.assertEqual(first["Status"], "READY")
        feed(r, 0.15)
        r.close()
        self.assertEqual(len(self.entries()), 2)

    def test_encoder_silent_failure_is_failed(self):
        self.codec = Codec(broken=True)
        r = self.start()
        feed(r, 0.12)
        r.close()
        (entry,) = self.entries()
        self.assertEqual(entry["Status"], "FAILED")

    def test_queue_retains_only_latest(self):
        r = SegmentRecorder(self.root, self.codec, min_free_bytes=0)
        for _ in range(50):
            r.offer(Frame())
        self.assertEqual(r.frames.qsize(), 1)

    def test_only_one_recorder_owns_camera_directory(self):
        self.start()
        other = SegmentRecorder(self.root, self.codec, min_free_bytes=0)
        with self.assertRaises(RuntimeError):
            other.start()

    def test_recovery_checks_actual_readability(self):
        r = self.start()
        feed(r, 0.15)
        r.close()
        (manifest,) = self.root.glob("*/*.json")
        entry = json.loads(manifest.read_text())
        entry.update(Status="RECORDING", EndTime=None, FileSizeBytes=None, FrameCount=0)
        atomic_json(manifest, entry)
        r.recover()
        self.assertEqual(json.loads(manifest.read_text())["Status"], "READY")
        atomic_json(manifest, entry)
        self.codec.files.clear()
        r.recover()
        self.assertEqual(json.loads(manifest.read_text())["Status"], "FAILED")

    def test_disk_reserve_stops_recording(self):
        r = SegmentRecorder(self.root, self.codec, min_free_bytes=10**30)
        self.recorder = r
        r.start()
        feed(r, 0.12)
        r.close()
        self.assertFalse(self.entries())
        self.assertTrue(any("20GB" in n for n in list(r.notices.queue)))


if __name__ == "__main__":
    unittest.main()
