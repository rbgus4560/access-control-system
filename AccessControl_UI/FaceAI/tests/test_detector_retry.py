# 파일 역할: 합성 특징·합성 MP4 또는 모델 대역으로 동작을 확인하는 개발용 회귀 검사입니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

import unittest
import numpy as np
from dlib_fast import detect_with_retry


class DetectorRetryTests(unittest.TestCase):
    def run_detection(self, results):
        class Detector:
            def __init__(self):
                self.calls = []

            def run(self, frame, upsample):
                self.calls.append(upsample)
                return results[upsample], [], []

        detector = Detector()
        result = detect_with_retry(detector, np.zeros((12, 12), dtype=np.uint8))
        return result, detector.calls

    def test_normal_face_avoids_extra_work(self):
        result, calls = self.run_detection({0: ["face"]})
        self.assertEqual(result, (["face"], 0))
        self.assertEqual(calls, [0])

    def test_missed_face_can_be_recovered_once(self):
        result, calls = self.run_detection({0: [], 1: ["face"]})
        self.assertEqual(result, (["face"], 1))
        self.assertEqual(calls, [0, 1])

    def test_empty_or_multiple_faces_never_become_single_by_filtering(self):
        for results, count, calls in [
            ({0: [], 1: []}, 0, [0, 1]),
            ({0: ["a", "b"]}, 2, [0]),
            ({0: [], 1: ["a", "b"]}, 2, [0, 1]),
        ]:
            result, actual_calls = self.run_detection(results)
            self.assertEqual(len(result[0]), count)
            self.assertEqual(actual_calls, calls)


if __name__ == "__main__":
    unittest.main()
