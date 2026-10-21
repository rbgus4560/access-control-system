# 파일 역할: 합성 특징·합성 MP4 또는 모델 대역으로 동작을 확인하는 개발용 회귀 검사입니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

import unittest
import numpy as np
from face_logic import Enrollment, POSES, classify_pose, cosine_distance, unit_embedding


class LogicTests(unittest.TestCase):
    def setUp(self):
        self.a = np.zeros(512)
        self.a[0] = 1
        self.b = np.zeros(512)
        self.b[1] = 1

    def test_invalid_features_fail_closed(self):
        for value in [[], np.zeros(512), np.ones(128), [float("nan")] * 512, [float("inf")] * 512]:
            with self.subTest(value=str(value)[:20]), self.assertRaises(ValueError):
                unit_embedding(value)

    def test_distances(self):
        self.assertAlmostEqual(cosine_distance(self.a, self.a * 3), 0)
        self.assertAlmostEqual(cosine_distance(self.a, self.b), 1)
        self.assertAlmostEqual(cosine_distance(self.a, -self.a), 2)

    def test_pose_rejection_and_signs(self):
        self.assertEqual(classify_pose(0, 0, 0), "FRONT")
        self.assertEqual(classify_pose(-25, 0, 0), "UP")
        self.assertEqual(classify_pose(25, 0, 0), "DOWN")
        self.assertEqual(classify_pose(0, -25, 0), "LEFT")
        self.assertEqual(classify_pose(0, 25, 0), "RIGHT")
        self.assertEqual(classify_pose(0, 25, 0, yaw_sign=-1), "LEFT")
        for angles in [(15, 0, 0), (25, 25, 0), (0, 0, 30), (0, 80, 0), (float("nan"), 0, 0)]:
            self.assertEqual(classify_pose(*angles), "UNKNOWN")

    def test_registration_order_atomic_replacement_and_cancel(self):
        state = Enrollment()
        with self.assertRaises(ValueError):
            state.compare(self.a, 0.45)
        state.start()
        with self.assertRaises(ValueError):
            state.capture("UP", self.a)
        for pose in POSES[:-1]:
            self.assertFalse(state.capture(pose, self.a))
            self.assertFalse(state.active)
        self.assertTrue(state.capture(POSES[-1], self.a))
        self.assertTrue(state.compare(self.a, 0.45)[2])
        self.assertFalse(state.compare(self.b, 0.45)[2])
        state.start()
        state.capture("FRONT", self.b)
        self.assertTrue(state.compare(self.a, 0.45)[2])
        state.cancel()
        self.assertIsNone(state.pending)
        self.assertTrue(state.compare(self.a, 0.45)[2])
        state.start()
        for pose in POSES:
            state.capture(pose, self.b)
        self.assertTrue(state.compare(self.b, 0.45)[2])
        self.assertFalse(state.compare(self.a, 0.45)[2])
        for threshold in [-1, 0, 2, float("nan")]:
            with self.assertRaises(ValueError):
                state.compare(self.b, threshold)


if __name__ == "__main__":
    unittest.main()
