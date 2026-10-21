# 파일 역할: 합성 특징·합성 MP4 또는 모델 대역으로 동작을 확인하는 개발용 회귀 검사입니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

import queue
import threading
import time
import unittest
from unittest.mock import patch
import numpy as np
from face_logic import (
    POSES,
    NINE_POSES,
    PIPELINE_ID,
    Enrollment,
    ContinuousEnrollment,
    classify_coverage_pose,
    validate_reference,
)
from ai_worker import run_worker

ANGLES = [
    (0, 0, 0),
    (-20, 0, 0),
    (-20, 25, 0),
    (0, 25, 0),
    (20, 25, 0),
    (20, 0, 0),
    (20, -25, 0),
    (0, -25, 0),
    (-20, -25, 0),
]


class ContinuousTests(unittest.TestCase):
    def setUp(self):
        self.front = np.zeros(512)
        self.front[0] = 1
        self.other = np.zeros(512)
        self.other[1] = 1

    def test_coverage_angles_limits_and_signs(self):
        self.assertEqual(tuple(classify_coverage_pose(*a) for a in ANGLES), NINE_POSES)
        self.assertEqual(classify_coverage_pose(-20, 25, 0, yaw_sign=-1), "UP_LEFT")
        self.assertEqual(classify_coverage_pose(-20, 25, 0, pitch_sign=-1), "DOWN_RIGHT")
        for a in [(11, 0, 0), (0, 12, 0), (31, 0, 0), (0, 36, 0), (0, 0, 13), (float("nan"), 0, 0)]:
            self.assertEqual(classify_coverage_pose(*a), "UNKNOWN")

    def test_front_first_then_free_order_atomic_commit(self):
        state = ContinuousEnrollment(active={p: self.other.copy() for p in POSES})
        state.start()
        with self.assertRaises(ValueError):
            state.capture("UP_LEFT", self.front)
        self.assertFalse(state.capture("FRONT", self.front))
        for pose in ["LEFT", "DOWN_LEFT", "DOWN", "DOWN_RIGHT", "RIGHT", "UP_RIGHT", "UP"]:
            self.assertFalse(state.capture(pose, self.front))
            self.assertEqual(set(state.active), set(POSES))
        self.assertTrue(state.capture("UP_LEFT", self.front))
        self.assertIsNone(state.pending)
        self.assertEqual(set(state.active), set(NINE_POSES))
        self.assertTrue(state.compare(self.front, 0.45)[2])
        self.assertFalse(state.compare(self.other, 0.45)[2])

    def test_duplicates_other_face_and_cancel_do_not_replace_reference(self):
        state = ContinuousEnrollment(active={p: self.front.copy() for p in POSES})
        state.start()
        state.capture("FRONT", self.front)
        for pose, value in [("FRONT", self.front), ("LEFT", self.other), ("OTHER", self.front)]:
            with self.assertRaises(ValueError):
                state.capture(pose, value)
        self.assertEqual(list(state.pending), ["FRONT"])
        state.cancel()
        self.assertEqual(set(state.active), set(POSES))

    def test_legacy_and_nine_reference_contract(self):
        for poses in [POSES, NINE_POSES]:
            raw = {"pipeline": PIPELINE_ID, "features": {p: self.front.tolist() for p in poses}}
            self.assertEqual(set(validate_reference(raw)), set(poses))
            del raw["features"][poses[-1]]
            with self.assertRaises(ValueError):
                validate_reference(raw)

    def test_worker_auto_capture_no_space_and_export_then_cancel(self):
        from face_engine import Observation

        vector = self.front

        class FakeEngine:
            def __enter__(self):
                return self

            def __exit__(self, *args):
                pass

            def observe(self, frame):
                index = int(frame[0, 0, 0])
                return Observation(
                    valid=index < 9,
                    reason="OK" if index < 9 else "MULTIPLE_FACES",
                    rotation=np.array([index]),
                    timings={},
                )

            def angles(self, rotation, baseline):
                return ANGLES[int(rotation[0])]

            def embedding(self, obs):
                return vector.copy()

        frames, commands, results = queue.Queue(1), queue.Queue(8), queue.Queue(1)
        stop = threading.Event()
        sequence = 0

        def sample(index=0):
            nonlocal sequence
            sequence += 1
            frames.put(
                {
                    "sequence": sequence,
                    "captured_at": time.monotonic(),
                    "frame": np.full((2, 2, 3), index, dtype=np.uint8),
                },
                timeout=2,
            )
            state = results.get(timeout=5)
            self.assertNotIn("fatal", state)
            return state

        def command(key, index=0):
            commands.put({"id": sequence + 1, "key": key, "created_at": time.monotonic()})
            return sample(index)

        with patch("ai_worker.annotate", lambda frame, obs: frame.copy()):
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
                        "continuous_enrollment": True,
                        "extended_poses": True,
                    },
                    FakeEngine,
                ),
            )
            worker.start()
            try:
                command("0")
                command("e")
                multi = sample(99)
                self.assertEqual(multi["pending_count"], 0)
                for sector in [0, 7, 6, 5, 4, 3, 2, 1, 8]:
                    for _ in range(3):
                        time.sleep(0.32)
                        state = sample(sector)
                self.assertFalse(state["enrolling"])
                self.assertEqual(state["reference_count"], 9)
                exported = command("export")["export_reference"]
                self.assertEqual(set(validate_reference(exported)), set(NINE_POSES))
                command("e")
                cancelled = command("x")
                self.assertFalse(cancelled["enrolling"])
                self.assertEqual(cancelled["reference_count"], 9)
            finally:
                stop.set()
                worker.join(3)
                self.assertFalse(worker.is_alive())


if __name__ == "__main__":
    unittest.main()
