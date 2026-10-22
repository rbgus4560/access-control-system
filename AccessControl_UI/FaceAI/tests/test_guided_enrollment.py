# 파일 역할: 합성 특징·합성 MP4 또는 모델 대역으로 동작을 확인하는 개발용 회귀 검사입니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

import math
import queue
import threading
import time
import unittest
from unittest.mock import patch
import numpy as np
from guided_enrollment import GuidedEnrollment
from face_logic import POSES, NINE_POSES, GUIDED_POSES, PIPELINE_ID, validate_reference
from ai_worker import run_worker


class GuidedTests(unittest.TestCase):
    def setUp(self):
        self.vector = np.zeros(512)
        self.vector[0] = 1
        self.other = np.zeros(512)
        self.other[1] = 1

    def offer(self, e, angles, now, quality=80, vector=None):
        return e.offer(angles, self.vector if vector is None else vector, quality, now)

    def test_continuous_motion_without_stops_and_atomic_commit(self):
        e = GuidedEnrollment(active={p: self.other.copy() for p in POSES})
        e.start()
        self.assertFalse(self.offer(e, (0, 0, 0), 0))
        t = 1
        # No repeated angle and no three-frame stable pose anywhere.
        for p, y in [
            (0, -10),
            (0, -14),
            (0, -22),
            (0, 0),
            (0, 10),
            (0, 14),
            (0, 22),
            (0, 0),
            (-10, 0),
            (-14, 0),
            (-22, 0),
            (0, 0),
            (10, 0),
            (14, 0),
            (22, 0),
        ]:
            done = self.offer(e, (p, y, 0), t)
            t += 0.5
            if not done:
                self.assertEqual(set(e.active), set(POSES))
        self.assertTrue(done)
        self.assertIsNone(e.pending)
        self.assertEqual(set(e.active), set(GUIDED_POSES))
        self.assertTrue(e.compare(self.vector, 0.45)[2])
        self.assertFalse(e.compare(self.other, 0.45)[2])
        self.assertNotIn("#", e.compare(self.vector, 0.45)[1])

    def test_blur_excessive_pose_repeat_timing_other_face_cancel(self):
        e = GuidedEnrollment(active={p: self.vector.copy() for p in POSES})
        e.start()
        self.assertFalse(e.wants((0, -20, 0), 80, 0))
        self.assertFalse(e.wants((0, 0, 0), 20, 0))
        self.offer(e, (0, 0, 0), 0)
        for a in [(0, 40, 0), (33, 0, 0), (0, 0, 13), (math.nan, 0, 0)]:
            self.assertFalse(e.wants(a, 80, 1))
        self.assertFalse(e.wants((0, -10, 0), 80, 0.1))
        with self.assertRaises(ValueError):
            self.offer(e, (0, -10, 0), 1, vector=self.other)
        self.offer(e, (0, -10, 0), 1)
        self.assertFalse(e.wants((0, -10, 0), 80, 2))
        self.assertTrue(e.wants((0, -10, 0), 110, 2))
        self.offer(e, (0, -10, 0), 2, quality=110)
        self.assertNotIn("LEFT#2", e.pending)
        e.cancel()
        self.assertEqual(set(e.active), set(POSES))
        self.assertEqual(e.candidates, {})

    def test_bounded_pool_and_sharp_sample_selection(self):
        e = GuidedEnrollment()
        e.start()
        self.offer(e, (0, 0, 0), 0)
        for i in range(100):
            self.offer(e, ((i % 5) * 4 - 8, -12 - 4 * ((i // 5) % 2), 0), i + 1, quality=40 + i)
            self.assertLessEqual(len(e.candidates["LEFT"]), 8)
        self.assertGreater(e.accepted, 8)
        self.assertNotIn("LEFT#2", e.pending)
        self.offer(e, (0, -24, 0), 150, quality=180)
        self.assertIn("LEFT#2", e.pending)
        self.assertTrue(e.wants((0, 0, 0), 80, 200))

    def test_diagonal_motion_is_collected_without_exact_sectors(self):
        e = GuidedEnrollment()
        e.start()
        self.offer(e, (0, 0, 0), 0)
        # Pitch/yaw combined viewpoints do not require any fixed diagonal window.
        self.offer(e, (5, -10, 0), 1)
        self.offer(e, (13, -24, 0), 2)
        self.assertIn("LEFT#2", e.pending)
        self.assertEqual(e.expected, "FRONT")
        self.offer(e, (0, 0, 0), 3)
        self.assertEqual(e.expected, "RIGHT")

    def test_displayed_stage_only_and_front_return(self):
        e = GuidedEnrollment()
        e.start()
        self.offer(e, (0, 0, 0), 0)
        self.assertEqual(e.expected, "LEFT")
        # Earlier free-form collection could silently complete RIGHT/DOWN during a different instruction.
        for angles in [(0, 10, 0), (0, 24, 0), (-10, 0, 0), (-24, 0, 0), (10, 0, 0), (24, 0, 0)]:
            self.assertFalse(e.wants(angles, 90, 1))
            self.assertFalse(self.offer(e, angles, 1))
        self.assertEqual(set(e.pending), {"FRONT"})
        t = 2
        for key, angles in [
            ("LEFT", [(0, -10, 0), (0, -24, 0)]),
            ("RIGHT", [(0, 10, 0), (0, 24, 0)]),
            ("UP", [(-10, 0, 0), (-24, 0, 0)]),
            ("DOWN", [(10, 0, 0), (24, 0, 0)]),
        ]:
            self.assertEqual(e.expected, key)
            for angle in angles:
                self.offer(e, angle, t)
                t += 1
            if key != "DOWN":
                self.assertEqual(e.expected, "FRONT")
                self.assertFalse(e.wants(angles[-1], 90, t))
                before = e.accepted
                self.offer(e, (0, 0, 0), t)
                t += 1
                self.assertEqual(
                    e.accepted, before
                )  # Returning is a transition, not a duplicate feature sample.
        self.assertIsNone(e.pending)
        self.assertEqual(set(e.active), set(GUIDED_POSES))

    def test_contract_old_five_old_nine_new_samples_partial_rejection(self):
        for poses in [POSES, NINE_POSES, GUIDED_POSES]:
            reference = {
                "pipeline": PIPELINE_ID,
                "features": {p: self.vector.tolist() for p in poses},
            }
            self.assertEqual(set(validate_reference(reference)), set(poses))
        bad = {
            "pipeline": PIPELINE_ID,
            "features": {p: self.vector.tolist() for p in GUIDED_POSES[:-1]},
        }
        with self.assertRaises(ValueError):
            validate_reference(bad)

    def test_down_sweep_at_bridge_frame_interval_keeps_both_viewpoints(self):
        e = GuidedEnrollment()
        e.start()
        self.offer(e, (0, 0, 0), 0)
        # 이전 단계가 완료되고 정면으로 복귀한 뒤 5Hz 분석 주기로 아래를 봅니다.
        for direction in ("LEFT", "RIGHT", "UP"):
            e.pending[direction] = self.vector.copy()
            e.pending[direction + "#2"] = self.vector.copy()
        e.returning = True
        e.last_time = 1.8
        self.offer(e, (0, 0, 0), 2.0)
        self.offer(e, (10, 0, 0), 2.2)
        done = self.offer(e, (20, 0, 0), 2.4)
        self.assertTrue(done)
        self.assertEqual(set(e.active), set(GUIDED_POSES))

    def test_deep_first_sample_requests_shallower_view_without_relaxing_quality(self):
        e = GuidedEnrollment()
        e.start()
        self.offer(e, (0, 0, 0), 0)
        for direction in ("LEFT", "RIGHT", "UP"):
            e.pending[direction] = self.vector.copy()
            e.pending[direction + "#2"] = self.vector.copy()
        self.offer(e, (24, 0, 0), 1)
        self.assertIn("정면 쪽", e.hint((24, 0, 0), 80))
        self.assertFalse(e.wants((16, 0, 0), 20, 2))
        with self.assertRaises(ValueError):
            self.offer(e, (16, 0, 0), 2, vector=self.other)
        self.assertTrue(self.offer(e, (16, 0, 0), 2))

    def test_real_worker_moving_frames_no_stability_and_export(self):
        from face_engine import Observation

        # First command performs front calibration automatically; no key 0 or SPACE.
        angles = [
            (0, 0, 0),
            (0, -10, 0),
            (0, -22, 0),
            (0, 0, 0),
            (0, 10, 0),
            (0, 22, 0),
            (0, 0, 0),
            (-10, 0, 0),
            (-22, 0, 0),
            (0, 0, 0),
            (10, 0, 0),
            (22, 0, 0),
        ]
        vector = self.vector

        class Engine:
            def __enter__(self):
                return self

            def __exit__(self, *a):
                pass

            def observe(self, f):
                i = int(f[0, 0, 0])
                return Observation(
                    valid=i < len(angles),
                    reason="OK" if i < len(angles) else "MULTIPLE_FACES",
                    rotation=np.array([i]),
                    blur=90,
                    timings={},
                )

            def angles(self, r, b):
                return angles[int(r[0])]

            def embedding(self, o):
                return vector.copy()

        frames, commands, results = queue.Queue(1), queue.Queue(8), queue.Queue(1)
        stop = threading.Event()
        seq = 0

        def sample(index):
            nonlocal seq
            seq += 1
            frames.put(
                {
                    "sequence": seq,
                    "captured_at": time.monotonic(),
                    "frame": np.full((2, 2, 3), index, dtype=np.uint8),
                }
            )
            result = results.get(timeout=5)
            self.assertNotIn("fatal", result)
            return result

        config = {
            "pitch_sign": 1,
            "yaw_sign": 1,
            "distance_threshold": 0.45,
            "continuous_enrollment": True,
            "guided_enrollment": True,
            "extended_poses": True,
        }
        with patch("ai_worker.annotate", lambda f, o: f.copy()):
            worker = threading.Thread(
                target=run_worker, args=(frames, commands, results, stop, config, Engine)
            )
            worker.start()
            try:
                commands.put({"id": 1, "key": "e", "created_at": time.monotonic()})
                state = sample(0)
                self.assertTrue(state["calibrated"])
                self.assertEqual(state["pending_count"], 1)
                bad = sample(99)
                self.assertEqual(bad["pending_count"], 1)
                for i in range(1, len(angles)):
                    time.sleep(0.47)
                    state = sample(i)
                self.assertFalse(state["enrolling"])
                self.assertEqual(state["reference_count"], 9)
                self.assertLess(state["stable"], 3)
                commands.put({"id": seq + 1, "key": "export", "created_at": time.monotonic()})
                state = sample(0)
                self.assertEqual(
                    set(validate_reference(state["export_reference"])), set(GUIDED_POSES)
                )
                commands.put({"id": seq + 1, "key": "e", "created_at": time.monotonic()})
                sample(0)
                commands.put({"id": seq + 1, "key": "x", "created_at": time.monotonic()})
                state = sample(0)
                self.assertEqual(state["reference_count"], 9)
                self.assertFalse(state["enrolling"])
            finally:
                stop.set()
                worker.join(3)
                self.assertFalse(worker.is_alive())


if __name__ == "__main__":
    unittest.main()
