# 파일 역할: 고개를 움직이는 동안 각 방향에서 선명도와 각도 차이를 고려해 대표 특징 샘플을 선택합니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

"""Bounded quality/diversity selection from motion, without fixed diagonal stops."""

import math
from dataclasses import dataclass, field
import numpy as np
from face_logic import Enrollment, GUIDED_POSES, unit_embedding, cosine_distance

DIRECTIONS = ("LEFT", "RIGHT", "UP", "DOWN")


@dataclass
class GuidedEnrollment(Enrollment):
    candidates: dict = field(default_factory=dict)
    returning: bool = False
    accepted: int = 0
    last_time: float = -math.inf
    last_angles: tuple | None = None

    def start(self):
        self.pending = {}
        self.candidates = {direction: [] for direction in DIRECTIONS}
        self.returning = False
        self.accepted = 0
        self.last_time = -math.inf
        self.last_angles = None

    def cancel(self):
        self.pending = None
        self.returning = False
        self.candidates = {}
        self.last_angles = None

    @property
    def expected(self):
        if self.pending is None:
            return None
        if self.returning or "FRONT" not in self.pending:
            return "FRONT"
        return next((p for p in DIRECTIONS if p + "#2" not in self.pending), None)

    @staticmethod
    def bucket(angles):
        p, y, r = angles
        if not np.isfinite(angles).all() or abs(r) > 12 or abs(p) > 32 or abs(y) > 38:
            return None
        if abs(p) <= 8 and abs(y) <= 8:
            return "FRONT"
        if abs(y) / 25 >= abs(p) / 20 and abs(y) >= 7:
            return "LEFT" if y < 0 else "RIGHT"
        if abs(p) >= 6:
            return "UP" if p < 0 else "DOWN"
        return None

    def wants(self, angles, quality, now):
        if self.pending is None or not math.isfinite(quality) or quality < 35:
            return False
        bucket = self.bucket(angles)
        # Only collect the displayed stage. Other directions must never silently pre-complete.
        if bucket is None or bucket != self.expected:
            return False
        if bucket == "FRONT" and "FRONT" in self.pending and not self.returning:
            return False
        if now - self.last_time < 0.45:
            return False
        # Repeated redraws/near-identical viewpoints do not trigger ArcFace work.
        samples = self.candidates.get(bucket, [])
        return not any(math.dist(angles[:2], c[0]) < 3 and quality < c[1] * 1.2 for c in samples)

    def offer(self, angles, feature, quality, now):
        if not self.wants(angles, quality, now):
            return False
        if self.returning:
            self.returning = False
            self.last_time = now
            self.last_angles = tuple(angles)
            return False
        vector = unit_embedding(feature)
        front = self.pending.get("FRONT")
        if front is not None and cosine_distance(front, vector) > 0.60:
            raise ValueError("Face differs from FRONT")
        bucket = self.bucket(angles)
        self.last_time = now
        self.last_angles = tuple(angles)
        self.accepted += 1
        if bucket == "FRONT":
            self.pending["FRONT"] = vector.copy()
            return False
        pool = self.candidates[bucket]
        pool.append((tuple(angles[:2]), float(quality), vector.copy()))
        # Retain angle extremes and strong images in a small in-memory pool.
        axis = 1 if bucket in ("LEFT", "RIGHT") else 0
        if len(pool) > 8:
            extremes = {
                min(range(len(pool)), key=lambda i: abs(pool[i][0][axis])),
                max(range(len(pool)), key=lambda i: abs(pool[i][0][axis])),
            }
            drop = min((i for i in range(len(pool)) if i not in extremes), key=lambda i: pool[i][1])
            pool.pop(drop)
        best = None
        score = -math.inf
        for i, a in enumerate(pool):
            for b in pool[i + 1 :]:
                # Different dominant-axis viewpoints; two sharp copies at one angle are insufficient.
                separation = abs(a[0][axis] - b[0][axis])
                reach = max(abs(a[0][axis]), abs(b[0][axis]))
                if separation < 7 or reach < (18 if axis == 1 else 14):
                    continue
                value = (
                    separation
                    + 3 * (min(a[1], b[1]) / max(a[1], b[1]))
                    + min(math.log1p(a[1]), math.log1p(b[1]))
                )
                if value > score:
                    best = (a, b)
                    score = value
        if best is not None:
            first, second = sorted(best, key=lambda c: abs(c[0][axis]))
            self.pending[bucket] = first[2].copy()
            self.pending[bucket + "#2"] = second[2].copy()
            self.returning = bucket != DIRECTIONS[-1]
        if set(self.pending) == set(GUIDED_POSES):
            self.active = {k: v.copy() for k, v in self.pending.items()}
            self.pending = None
            self.candidates = {}
            return True
        return False
