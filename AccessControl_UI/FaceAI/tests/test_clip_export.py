# 파일 역할: 합성 특징·합성 MP4 또는 모델 대역으로 동작을 확인하는 개발용 회귀 검사입니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

"""Actual OpenCV codec checks using synthetic frames in a temporary directory."""

from datetime import datetime, timezone, timedelta
import hashlib
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from export_clip import export

try:
    import cv2
    import numpy as np
except ImportError:
    cv2 = None


@unittest.skipIf(cv2 is None, "Native OpenCV is required")
class NativeExportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.env = patch.dict(os.environ, {"LOCALAPPDATA": self.temp.name})
        self.env.start()
        self.root = Path(self.temp.name) / "AccessControlSystem"
        self.start = datetime.now(timezone.utc) - timedelta(minutes=5)
        folder = self.root / "recordings" / "CAM-01" / self.start.strftime("%Y%m%d")
        folder.mkdir(parents=True)
        self.first, self.second = folder / (uuid.uuid4().hex + ".mp4"), folder / (
            uuid.uuid4().hex + ".mp4"
        )
        for path, count, green in [(self.first, 60, 40), (self.second, 40, 200)]:
            writer = cv2.VideoWriter(str(path), cv2.VideoWriter_fourcc(*"mp4v"), 10, (96, 64))
            self.assertTrue(writer.isOpened())
            for i in range(count):
                frame = np.zeros((64, 96, 3), np.uint8)
                frame[:] = (i * 3, green, 20)
                writer.write(frame)
            writer.release()
        cid = uuid.uuid4()
        self.output = self.root / "clips" / (cid.hex + ".mp4")
        self.job = dict(
            Clip=dict(
                ClipId=str(cid),
                EventId=None,
                StartTime=(self.start + timedelta(seconds=4)).isoformat(),
                EndTime=(self.start + timedelta(seconds=8.004)).isoformat(),
                FilePath=str(self.output),
                Status="EXPORTING",
            ),
            Width=96,
            Height=64,
            Fps=10,
            Parts=[
                dict(FilePath=str(self.first), StartFrame=40, FrameCount=20),
                dict(FilePath=str(self.second), StartFrame=0, FrameCount=20),
            ],
        )

    def tearDown(self):
        self.env.stop()
        self.temp.cleanup()

    def test_real_mp4_join_frame_start_and_hash(self):
        result = export(self.job, cv2)
        self.assertEqual(result["Status"], "READY")
        self.assertEqual(result["FrameCount"], 40)
        self.assertEqual(result["DurationSeconds"], 4)
        self.assertEqual(result["FileHash"], hashlib.sha256(self.output.read_bytes()).hexdigest())
        video = cv2.VideoCapture(str(self.output))
        try:
            frames = []
            while True:
                ok, frame = video.read()
                if not ok:
                    break
                frames.append(frame)
            self.assertEqual(len(frames), 40)
            self.assertLess(abs(float(frames[0][32, 48, 0]) - 120), 10)
            self.assertLess(abs(float(frames[20][32, 48, 1]) - 200), 10)
        finally:
            video.release()
        with self.assertRaises(ValueError):
            export(self.job, cv2)

    def test_client_temp_does_not_replace_server_clip(self):
        self.output.parent.mkdir(parents=True)
        self.output.write_bytes(b"existing server copy")
        temporary = self.output.parent / "client_temp" / self.output.name
        self.job["Clip"]["FilePath"] = str(temporary)
        result = export(self.job, cv2)
        self.assertEqual(result["Status"], "READY")
        self.assertTrue(temporary.exists())
        self.assertEqual(self.output.read_bytes(), b"existing server copy")

    def test_source_end_failure(self):
        self.job["Parts"][0]["StartFrame"] = 599
        with self.assertRaises(RuntimeError):
            export(self.job, cv2)

    def test_outside_source_rejected(self):
        self.job["Parts"][0]["FilePath"] = str(Path(self.temp.name) / "other.mp4")
        with self.assertRaises(ValueError):
            export(self.job, cv2)


if __name__ == "__main__":
    unittest.main()
