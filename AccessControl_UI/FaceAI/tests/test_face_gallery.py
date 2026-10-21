# 파일 역할: 합성 특징·합성 MP4 또는 모델 대역으로 동작을 확인하는 개발용 회귀 검사입니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

import unittest
import uuid
import numpy as np
from face_gallery import FaceGallery, AMBIGUITY_MARGIN
from face_logic import POSES, PIPELINE_ID


def reference(vector):
    return {"pipeline": PIPELINE_ID, "features": {p: vector.tolist() for p in POSES}}


def vec(index):
    v = np.zeros(512)
    v[index] = 1
    return v


class GalleryTests(unittest.TestCase):
    def gallery(self, people):
        g = FaceGallery()
        batch = str(uuid.uuid4())
        g.begin({"gallery_id": batch, "count": len(people)})
        for person, vector in people:
            g.add({"person_id": person, "reference": reference(vector)})
        g.commit({"gallery_id": batch})
        return g

    def test_multiple_people_match_and_unknown(self):
        ids = [str(uuid.uuid4()) for _ in range(3)]
        g = self.gallery([(p, vec(i)) for i, p in enumerate(ids)])
        for i, p in enumerate(ids):
            answer = g.compare(vec(i))
            self.assertEqual(answer["person_id"], p)
            self.assertEqual(answer["result"], "MATCH")
        unknown = g.compare(vec(4))
        self.assertEqual(unknown["result"], "NO_MATCH")
        self.assertGreater(unknown["distance"], 0.45)

    def test_close_people_are_ambiguous_but_poses_of_one_person_are_not(self):
        a, b = str(uuid.uuid4()), str(uuid.uuid4())
        self.assertEqual(self.gallery([(a, vec(0))]).compare(vec(0))["result"], "MATCH")
        similar = vec(0) * 0.98 + vec(1) * 0.2
        g = self.gallery([(a, vec(0)), (b, similar)])
        answer = g.compare(vec(0))
        self.assertEqual(answer["result"], "AMBIGUOUS")
        self.assertLess(answer["runner_up_distance"] - answer["distance"], AMBIGUITY_MARGIN)
        self.assertEqual(
            self.gallery([(a, vec(0)), (b, vec(0))]).compare(vec(0))["result"], "AMBIGUOUS"
        )

    def test_incomplete_duplicate_invalid_or_wrong_batch_does_not_replace_gallery(self):
        person = str(uuid.uuid4())
        g = self.gallery([(person, vec(0))])
        old = g.version
        batch = str(uuid.uuid4())
        g.begin({"gallery_id": batch, "count": 2})
        self.assertIsNone(g.compare(vec(0)))
        g.add({"person_id": person, "reference": reference(vec(0))})
        with self.assertRaises(ValueError):
            g.add({"person_id": person, "reference": reference(vec(1))})
        with self.assertRaises(ValueError):
            g.commit({"gallery_id": batch})
        self.assertEqual(g.version, old)
        with self.assertRaises(ValueError):
            g.add({"person_id": str(uuid.uuid4()), "reference": {"pipeline": "bad"}})
        g.add({"person_id": str(uuid.uuid4()), "reference": reference(vec(1))})
        with self.assertRaises(ValueError):
            g.commit({"gallery_id": str(uuid.uuid4())})
        g.commit({"gallery_id": batch})
        self.assertEqual(len(g.people), 2)

    def test_removal_empty_and_replacement_are_atomic(self):
        a, b = str(uuid.uuid4()), str(uuid.uuid4())
        g = self.gallery([(a, vec(0)), (b, vec(1))])
        batch = str(uuid.uuid4())
        g.begin({"gallery_id": batch, "count": 1})
        g.add({"person_id": b, "reference": reference(vec(1))})
        g.commit({"gallery_id": batch})
        self.assertEqual(g.compare(vec(0))["result"], "NO_MATCH")
        batch = str(uuid.uuid4())
        g.begin({"gallery_id": batch, "count": 0})
        g.commit({"gallery_id": batch})
        self.assertIsNone(g.compare(vec(1)))
        self.assertEqual(g.people, {})

    def test_bounds_and_bad_ids_fail_explicitly(self):
        g = FaceGallery()
        for count in [-1, 1001, True, 1.5]:
            with self.assertRaises(ValueError):
                g.begin({"gallery_id": str(uuid.uuid4()), "count": count})
        with self.assertRaises(ValueError):
            g.begin({"gallery_id": "invalid", "count": 1})


if __name__ == "__main__":
    unittest.main()
