# 파일 역할: 합성 특징·합성 MP4 또는 모델 대역으로 동작을 확인하는 개발용 회귀 검사입니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

import unittest
import numpy as np
from face_logic import POSES, PIPELINE_ID, validate_reference


class ReferenceHandoffTests(unittest.TestCase):
    def test_roundtrip_and_invalid_reference(self):
        source = {"pipeline": PIPELINE_ID, "features": {pose: [1.0] * 512 for pose in POSES}}
        result = validate_reference(source)
        self.assertEqual(set(result), set(POSES))
        self.assertTrue(all(abs(np.linalg.norm(v) - 1) < 1e-12 for v in result.values()))
        invalid = [
            None,
            {"pipeline": "other", "features": source["features"]},
            {"pipeline": PIPELINE_ID, "features": {}},
            {"pipeline": PIPELINE_ID, "features": {pose: [0] * 512 for pose in POSES}},
            {"pipeline": PIPELINE_ID, "features": {pose: [float("nan")] * 512 for pose in POSES}},
        ]
        for reference in invalid:
            with self.subTest(reference=str(reference)[:80]), self.assertRaises(ValueError):
                validate_reference(reference)


if __name__ == "__main__":
    unittest.main()
