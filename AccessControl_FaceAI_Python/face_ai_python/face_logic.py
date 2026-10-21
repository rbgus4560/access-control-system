"""Pure numerical logic; no camera, model download, or database access."""
from dataclasses import dataclass, field
import numpy as np

POSES = ('FRONT', 'UP', 'DOWN', 'LEFT', 'RIGHT')

def unit_embedding(value):
    a = np.asarray(value, dtype=np.float64)
    if a.shape != (512,) or not np.isfinite(a).all():
        raise ValueError('Expected 512 finite ArcFace values')
    norm = np.linalg.norm(a)
    if not np.isfinite(norm) or norm <= 1e-12:
        raise ValueError('Invalid embedding norm')
    return a / norm

def cosine_distance(a, b):
    return float(np.clip(1.0 - np.dot(unit_embedding(a), unit_embedding(b)), 0, 2))


def reference_distances(feature, references):
    """하나의 입력 특징을 정규화한 뒤 각 등록 샘플과의 거리를 반환합니다."""
    # 같은 입력을 5방향마다 다시 정규화하지 않습니다. 샘플 검증과 순서를
    # 유지하므로 잘못된 특징 거부 및 동점일 때의 방향 선택은 그대로입니다.
    query = unit_embedding(feature)
    return {
        pose: float(np.clip(1.0 - np.dot(query, unit_embedding(value)), 0, 2))
        for pose, value in references.items()
    }

def classify_pose(pitch, yaw, roll, pitch_sign=1, yaw_sign=1):
    if not np.isfinite([pitch, yaw, roll]).all():
        return 'UNKNOWN'
    p, y = pitch * pitch_sign, yaw * yaw_sign
    if abs(roll) > 12:
        return 'UNKNOWN'
    if abs(p) <= 12 and abs(y) <= 12:
        return 'FRONT'
    if abs(y) <= 12 and 18 <= abs(p) <= 35:
        return 'UP' if p < 0 else 'DOWN'
    if abs(p) <= 12 and 18 <= abs(y) <= 40:
        return 'LEFT' if y < 0 else 'RIGHT'
    return 'UNKNOWN'

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
            raise ValueError('Wrong registration direction')
        vector = unit_embedding(feature)
        self.pending[pose] = vector.copy()
        if len(self.pending) == len(POSES):
            self.active = {k: v.copy() for k, v in self.pending.items()}
            self.pending = None
            return True
        return False

    def compare(self, feature, threshold):
        if set(self.active) != set(POSES):
            raise ValueError('Complete five-direction reference required')
        if not np.isfinite(threshold) or not 0 < threshold < 2:
            raise ValueError('Invalid cosine distance threshold')
        distances = reference_distances(feature, self.active)
        nearest = min(distances, key=distances.get)
        distance = distances[nearest]
        return distance, nearest, distance <= threshold
