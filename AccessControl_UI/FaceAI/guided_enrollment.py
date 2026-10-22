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
        if self.returning:
            # 정면 복귀는 시점 전환입니다. 직전 특징 추출의 대기 시간으로 놓치지 않습니다.
            return True
        if bucket == "FRONT" and "FRONT" in self.pending and not self.returning:
            return False
        # Repeated redraws/near-identical viewpoints do not trigger ArcFace work.
        samples = self.candidates.get(bucket, [])
        axis = 1 if bucket in ("LEFT", "RIGHT") else 0
        diverse = bucket != "FRONT" and (
            not samples or any(abs(angles[axis] - c[0][axis]) >= 7 for c in samples)
        )
        # 브리지는 0.2초마다 분석합니다. 새 각도까지 0.45초 기다리면 얕은 각도를
        # 놓쳐 깊은 각도 하나만 남습니다. 새 시점은 다음 분석에서, 품질 갱신은 기존 간격으로 받습니다.
        if now - self.last_time < (0.18 if diverse else 0.45):
            return False
        return not any(math.dist(angles[:2], c[0]) < 3 and quality < c[1] * 1.2 for c in samples)

    def hint(self, angles, quality):
        """수집이 멈춘 이유와 다음 동작을 안내합니다. 품질·동일인 기준은 완화하지 않습니다."""
        expected = self.expected
        if expected is None:
            return ""
        if expected == "FRONT":
            return "정면으로 돌아와 주세요." if self.returning else "정면을 바라보고 잠시 멈춰 주세요."
        name = {"LEFT": "왼쪽", "RIGHT": "오른쪽", "UP": "위", "DOWN": "아래"}[expected]
        if not np.isfinite(angles).all():
            return "각도 분석 대기 · 얼굴이 카메라에 보이도록 해 주세요."
        p, y, r = angles
        if abs(r) > 12:
            return "고개가 옆으로 기울었습니다. 수평을 유지해 주세요."
        if abs(p) > 32 or abs(y) > 38:
            return "너무 많이 움직였습니다. 정면 쪽으로 조금 돌아와 주세요."
        if not math.isfinite(quality) or quality < 35:
            return "선명도 부족 · 천천히 움직이고 조명을 확인해 주세요."
        samples = self.candidates.get(expected, [])
        if not samples:
            return f"{name} 방향 첫 샘플 대기 · 작게 움직인 뒤 잠시 멈춰 주세요."
        axis = 1 if expected in ("LEFT", "RIGHT") else 0
        reach = max(abs(c[0][axis]) for c in samples)
        current = abs(angles[axis])
        if reach >= (18 if axis == 1 else 14) and current > 8:
            return f"{name} 방향 첫 샘플 확보 · 정면 쪽으로 조금 돌아오며 다른 각도를 보여 주세요."
        return f"{name} 방향 첫 샘플 확보 · 같은 방향으로 조금 더 움직여 주세요."

    def offer(self, angles, feature, quality, now):
        if not self.wants(angles, quality, now):
            return False
        if self.returning:
            self.returning = False
            # 복귀 확인은 특징 추출이 아니므로 다음 방향의 첫 샘플을 지연시키지 않습니다.
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
