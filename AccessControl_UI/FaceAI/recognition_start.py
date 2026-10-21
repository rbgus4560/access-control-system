# 파일 역할: 정면에 가까운 자세가 안정적으로 유지됐는지 확인합니다. 이 보정은 신원 확인이나 생체 판정이 아닙니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

"""Single-face neutral-pose gate for automatic recognition startup.
This estimates pose only; identity and access permission are still decided separately.
"""

import numpy as np


class NeutralPoseGate:
    def __init__(self):
        self.reset()

    def reset(self):
        self.anchor = None
        self.since = self.last = None
        self.count = 0

    # 정면 범위·자세 변화·프레임 간격을 함께 확인합니다. 같은 분석 결과의 재표시는 안정화 횟수를 늘리지 못합니다.
    def offer(self, rotation, captured_at, angles):
        raw = np.asarray(angles(rotation, np.eye(3)), dtype=float)
        # Refuse sideways/downward/rolled startup; do not label any first face FRONT.
        if raw.shape != (3,) or not np.isfinite(raw).all() or np.any(np.abs(raw) > [20, 18, 12]):
            self.reset()
            return False
        if self.anchor is not None:
            drift = np.asarray(angles(rotation, self.anchor), dtype=float)
            if (
                self.last is None
                or not 0 < captured_at - self.last <= 1.5
                or not np.isfinite(drift).all()
                or np.max(np.abs(drift)) > 4
            ):
                self.reset()
        if self.anchor is None:
            self.anchor = rotation.copy()
            self.since = captured_at
        self.count += 1
        self.last = captured_at
        return self.count >= 3 and captured_at - self.since >= 0.8
