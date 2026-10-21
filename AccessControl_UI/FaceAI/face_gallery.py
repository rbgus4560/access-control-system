# 파일 역할: 전체 직원의 특징을 하나의 검색 행렬로 구성합니다. 직원별 점수와 2위와의 차이로 식별 모호성을 구분합니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

"""Atomic employee gallery and bounded, vectorized 1:N cosine matching."""

import uuid
import numpy as np
from face_logic import validate_reference, unit_embedding

MAX_PEOPLE = 1000
AMBIGUITY_MARGIN = 0.05


class FaceGallery:
    def __init__(self):
        self.people = {}
        self.version = ""
        self.pending = None
        self.expected = 0
        self.pending_version = ""
        self.matrix = None
        self.ranges = []

    @property
    def updating(self):
        return self.pending is not None

    def begin(self, value):
        version = str(uuid.UUID(value["gallery_id"]))
        count = value["count"]
        if isinstance(count, bool) or not isinstance(count, int) or not 0 <= count <= MAX_PEOPLE:
            raise ValueError("Invalid employee count")
        self.pending, self.expected, self.pending_version = {}, count, version

    def add(self, value):
        if self.pending is None:
            raise ValueError("Gallery batch not started")
        person = str(uuid.UUID(value["person_id"]))
        if person in self.pending or len(self.pending) >= self.expected:
            raise ValueError("Duplicate or excess employee")
        reference = validate_reference(value["reference"])
        self.pending[person] = reference

    # 직원 수와 배치 ID가 맞을 때만 완성된 행렬을 한 번에 교체합니다. 부분 수신 결과는 활성화하지 않습니다.
    def commit(self, value):
        if (
            self.pending is None
            or str(uuid.UUID(value["gallery_id"])) != self.pending_version
            or len(self.pending) != self.expected
        ):
            raise ValueError("Incomplete employee gallery")
        # Prepare everything before the atomic swap. Scores are grouped by person,
        # so two poses belonging to one employee do not create false ambiguity.
        vectors, ranges = [], []
        for person, features in self.pending.items():
            start = len(vectors)
            vectors.extend(features.values())
            ranges.append((person, list(features), start, len(vectors)))
        matrix = np.asarray(vectors, dtype=np.float64) if vectors else None
        self.people, self.version = self.pending, self.pending_version
        self.matrix, self.ranges, self.pending = matrix, ranges, None

    # 이미 추출한 특징 하나를 전체 샘플과 비교하고, 같은 직원의 자세 샘플들은 하나의 순위로 묶습니다.
    def compare(self, feature, threshold=0.45):
        if self.updating or self.matrix is None:
            return None
        feature = unit_embedding(feature)
        distances = np.clip(1 - self.matrix @ feature, 0, 2)
        ranked = []
        for person, poses, start, end in self.ranges:
            index = int(np.argmin(distances[start:end]))
            ranked.append((float(distances[start + index]), person, poses[index], end - start))
        ranked.sort(key=lambda row: (row[0], row[1]))
        best = ranked[0]
        runner_up = ranked[1][0] if len(ranked) > 1 else None
        ambiguous = (
            best[0] <= threshold
            and runner_up is not None
            and runner_up - best[0] < AMBIGUITY_MARGIN
        )
        return {
            "distance": best[0],
            "person_id": best[1],
            "pose": best[2].split("#")[0],
            "sample_count": best[3],
            "runner_up_distance": runner_up,
            "result": "AMBIGUOUS" if ambiguous else "MATCH" if best[0] <= threshold else "NO_MATCH",
        }
