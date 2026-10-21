# 파일 역할: 합성 특징·합성 MP4 또는 모델 대역으로 동작을 확인하는 개발용 회귀 검사입니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

"""Automatic startup tests without model downloads or a physical camera."""

import queue
import threading
import time
import unittest
from unittest.mock import patch
import numpy as np
from ai_worker import run_worker
from recognition_start import NeutralPoseGate
from face_logic import POSES, PIPELINE_ID


def rotation(yaw=0):
    a = np.radians(yaw)
    return np.array([[np.cos(a), 0, np.sin(a)], [0, 1, 0], [-np.sin(a), 0, np.cos(a)]])


def angles(matrix, baseline):
    import cv2

    return cv2.RQDecomp3x3(matrix @ baseline.T)[0]


class FakeEngine:
    def __enter__(self):
        return self

    def __exit__(self, *args):
        pass

    def observe(self, frame):
        from face_engine import Observation

        code = int(frame[0, 0, 0])
        if code == 7:
            raise RuntimeError("simulated inference error")
        return Observation(
            valid=code != 5,
            reason="MULTIPLE_FACES" if code == 5 else "OK",
            rotation=rotation(35 if code == 1 else 0),
            face=code,
        )

    angles = staticmethod(angles)

    def embedding(self, obs):
        vector = np.zeros(512)
        vector[1 if obs.face == 9 else 0] = 1
        return vector


class AutoStartTests(unittest.TestCase):
    def test_neutral_and_motion_gates(self):
        gate = NeutralPoseGate()
        for t in [0, 0.4, 0.8, 1.2]:
            self.assertFalse(gate.offer(rotation(35), t, angles))
        self.assertFalse(gate.offer(rotation(), 2, angles))
        self.assertFalse(gate.offer(rotation(), 2.4, angles))
        self.assertTrue(gate.offer(rotation(), 2.9, angles))
        gate.reset()
        self.assertFalse(gate.offer(rotation(), 3, angles))
        self.assertFalse(gate.offer(rotation(8), 3.5, angles))  # movement resets hold
        self.assertFalse(gate.offer(rotation(8), 3.9, angles))
        self.assertTrue(gate.offer(rotation(8), 4.4, angles))
        self.assertFalse(gate.offer(rotation(8), 7, angles))  # a long gap resets
        self.assertFalse(gate.offer(rotation(8), 7, angles))  # same sample cannot advance

    def run_scenario(self, automatic):
        frames, commands, results = queue.Queue(1), queue.Queue(8), queue.Queue(1)
        stop = threading.Event()
        worker = threading.Thread(
            target=run_worker,
            args=(
                frames,
                commands,
                results,
                stop,
                {
                    "pitch_sign": 1,
                    "yaw_sign": 1,
                    "distance_threshold": 0.45,
                    "automatic_recognition": automatic,
                },
                FakeEngine,
            ),
        )
        worker.start()
        sequence = 0
        command_id = 0

        def sample(code=0):
            nonlocal sequence
            sequence += 1
            frames.put(
                {
                    "sequence": sequence,
                    "captured_at": time.monotonic(),
                    "frame": np.full((8, 8, 3), code, np.uint8),
                },
                timeout=2,
            )
            return results.get(timeout=3)

        def command(key, **kwargs):
            nonlocal command_id
            command_id += 1
            commands.put({"id": command_id, "key": key, "created_at": time.monotonic(), **kwargs})

        vector = [1.0] + [0.0] * 511
        reference = {"pipeline": PIPELINE_ID, "features": {p: vector for p in POSES}}
        try:
            first = sample()
            self.assertFalse(first["matching"])
            command("load", reference=reference, label="employee A")
            loaded = sample(1)
            self.assertFalse(loaded["matching"])
            self.assertFalse(loaded["calibrated"])
            # Holding a sideways face cannot calibrate or start recognition.
            for _ in range(3):
                time.sleep(0.3)
                side = sample(1)
                self.assertFalse(side["calibrated"])
                self.assertIsNone(side["comparison"])
            current = sample()
            for _ in range(3):
                time.sleep(0.3)
                current = sample()
            if not automatic:
                self.assertFalse(current["calibrated"])
                self.assertFalse(current["matching"])
                return
            self.assertTrue(current["calibrated"])
            self.assertTrue(current["matching"])
            self.assertTrue(current["comparison"][2])
            other = sample(9)
            self.assertFalse(
                other["comparison"][2]
            )  # automatic mode does not relax identity threshold
            multiple = sample(5)
            self.assertIsNone(multiple["comparison"])
            failed = sample(7)
            self.assertIsNone(failed["comparison"])
            command("t")
            paused = sample()
            self.assertFalse(paused["matching"])
            self.assertFalse(paused["auto_start_enabled"])
            for _ in range(3):
                self.assertFalse(sample()["matching"])
            command("0")
            self.assertFalse(sample()["matching"])  # recalibration respects manual pause
            command("t")
            self.assertTrue(sample()["matching"])
            # Invalid replacement must not restart the previous reference.
            command("load", reference={"pipeline": "wrong"})
            self.assertFalse(sample()["matching"])
            self.assertFalse(sample()["matching"])
            command("load", reference=reference, label="employee B")
            sample()
            for _ in range(3):
                time.sleep(0.3)
                current = sample()
            self.assertTrue(current["matching"])
            self.assertEqual(current["reference_label"], "employee B")
            command("c")
            cleared = sample()
            self.assertFalse(cleared["matching"])
            self.assertEqual(cleared["reference_count"], 0)
            for _ in range(3):
                time.sleep(0.3)
                current = sample()
            self.assertFalse(current["matching"])
            self.assertIsNone(current["comparison"])
        finally:
            stop.set()
            worker.join(3)
            self.assertFalse(worker.is_alive())

    def test_recognition_auto_start_pause_new_reference_and_clear(self):
        with patch("ai_worker.annotate", lambda frame, obs: frame):
            self.run_scenario(True)

    def test_manual_session_does_not_auto_start(self):
        with patch("ai_worker.annotate", lambda frame, obs: frame):
            self.run_scenario(False)


if __name__ == "__main__":
    unittest.main()
