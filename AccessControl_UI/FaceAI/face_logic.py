# 파일 역할: 특징 정규화·코사인 거리·자세 분류와 기존 방향별 등록 상태를 정의합니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

"""Pure numerical logic; no camera, model download, or database access."""

from dataclasses import dataclass, field
import numpy as np

POSES = ("FRONT", "UP", "DOWN", "LEFT", "RIGHT")
NINE_POSES = (
    "FRONT",
    "UP",
    "UP_RIGHT",
    "RIGHT",
    "DOWN_RIGHT",
    "DOWN",
    "DOWN_LEFT",
    "LEFT",
    "UP_LEFT",
)
GUIDED_POSES = ("FRONT", "LEFT", "LEFT#2", "RIGHT", "RIGHT#2", "UP", "UP#2", "DOWN", "DOWN#2")
PIPELINE_ID = "arcface512_df093_dlibfast480_eyealign_v1"


def complete_reference(features):
    return isinstance(features, dict) and set(features) in (
        set(POSES),
        set(NINE_POSES),
        set(GUIDED_POSES),
    )


def validate_reference(reference):
    if not isinstance(reference, dict) or reference.get("pipeline") != PIPELINE_ID:
        raise ValueError("Incompatible face preprocessing/model version")
    features = reference.get("features")
    if not complete_reference(features):
        raise ValueError("Complete legacy five, nine directions, or guided nine samples required")
    return {pose: unit_embedding(value).copy() for pose, value in features.items()}


def unit_embedding(value):
    a = np.asarray(value, dtype=np.float64)
    if a.shape != (512,) or not np.isfinite(a).all():
        raise ValueError("Expected 512 finite ArcFace values")
    norm = np.linalg.norm(a)
    if not np.isfinite(norm) or norm <= 1e-12:
        raise ValueError("Invalid embedding norm")
    return a / norm


def cosine_distance(a, b):
    return float(np.clip(1.0 - np.dot(unit_embedding(a), unit_embedding(b)), 0, 2))


def reference_distances(feature, references):
    """하나의 입력 특징을 정규화한 뒤 각 등록 샘플과의 거리를 반환합니다."""
    # 동일 입력을 샘플마다 다시 정규화하지 않습니다. 등록 샘플의 검증과
    # 딕셔너리 순서는 유지해 잘못된 특징 거부와 동점 선택 규칙을 보존합니다.
    query = unit_embedding(feature)
    return {
        pose: float(np.clip(1.0 - np.dot(query, unit_embedding(value)), 0, 2))
        for pose, value in references.items()
    }


def classify_pose(pitch, yaw, roll, pitch_sign=1, yaw_sign=1):
    if not np.isfinite([pitch, yaw, roll]).all():
        return "UNKNOWN"
    p, y = pitch * pitch_sign, yaw * yaw_sign
    if abs(roll) > 12:
        return "UNKNOWN"
    if abs(p) <= 12 and abs(y) <= 12:
        return "FRONT"
    if abs(y) <= 12 and 18 <= abs(p) <= 35:
        return "UP" if p < 0 else "DOWN"
    if abs(p) <= 12 and 18 <= abs(y) <= 40:
        return "LEFT" if y < 0 else "RIGHT"
    return "UNKNOWN"


def classify_coverage_pose(pitch, yaw, roll, pitch_sign=1, yaw_sign=1):
    """3x3 angle coverage with neutral bands; never force excessive rotation."""
    if not np.isfinite([pitch, yaw, roll]).all() or abs(roll) > 12:
        return "UNKNOWN"
    p, y = pitch * pitch_sign, yaw * yaw_sign
    row = "" if abs(p) <= 10 else ("UP" if p < 0 else "DOWN") if 12 <= abs(p) <= 30 else None
    column = "" if abs(y) <= 10 else ("LEFT" if y < 0 else "RIGHT") if 15 <= abs(y) <= 35 else None
    if row is None or column is None:
        return "UNKNOWN"
    return "_".join(value for value in (row, column) if value) or "FRONT"


@dataclass
class Enrollment:
    active: dict = field(default_factory=dict)
    pending: dict | None = None

    @property
    def expected(self):
        return POSES[len(self.pending)] if self.pending is not None else None

    def start(self):
        self.pending = {}

    def cancel(self):
        self.pending = None

    def capture(self, pose, feature):
        if self.pending is None or pose != self.expected:
            raise ValueError("Wrong registration direction")
        vector = unit_embedding(feature)
        self.pending[pose] = vector.copy()
        if len(self.pending) == len(POSES):
            self.active = {k: v.copy() for k, v in self.pending.items()}
            self.pending = None
            return True
        return False

    def compare(self, feature, threshold):
        if not complete_reference(self.active):
            raise ValueError("Complete reference required")
        if not np.isfinite(threshold) or not 0 < threshold < 2:
            raise ValueError("Invalid cosine distance threshold")
        distances = reference_distances(feature, self.active)
        nearest = min(distances, key=distances.get)
        distance = distances[nearest]
        return distance, nearest.split("#")[0], distance <= threshold


@dataclass
class ContinuousEnrollment(Enrollment):
    """Front anchor first, then eight sectors in any order; commit only all nine."""

    @property
    def expected(self):
        if self.pending is None:
            return None
        return next((pose for pose in NINE_POSES if pose not in self.pending), None)

    def capture(self, pose, feature):
        if self.pending is None or pose not in NINE_POSES or pose in self.pending:
            raise ValueError("Direction missing or already collected")
        if not self.pending and pose != "FRONT":
            raise ValueError("Look FRONT first")
        vector = unit_embedding(feature)
        front = self.pending.get("FRONT")
        if front is not None and cosine_distance(front, vector) > 0.60:
            raise ValueError("Face differs from initial FRONT; improve pose/light")
        self.pending[pose] = vector.copy()
        if set(self.pending) == set(NINE_POSES):
            self.active = {k: v.copy() for k, v in self.pending.items()}
            self.pending = None
            return True
        return False
