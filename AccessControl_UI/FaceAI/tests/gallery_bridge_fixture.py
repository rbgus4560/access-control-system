# 파일 역할: C#·Python 연결 검사용 합성 카메라 대역입니다. 실제 카메라·얼굴 모델은 사용하지 않습니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

"""Test-only stdin/stdout fixture: real worker, synthetic faces, no camera/models."""

import os

if os.environ.get("ACCESS_GALLERY_BRIDGE_TEST") != "1":
    raise SystemExit("Integration test fixture only")
import contextlib
import json
import queue
import sys
import threading
import time
import numpy as np
import ai_worker
from face_engine import Observation

protocol = sys.stdout
stop = threading.Event()
frames, commands, results = queue.Queue(1), queue.Queue(8), queue.Queue(1)
marker = 0
lock = threading.Lock()


def emit(value):
    with lock:
        protocol.write(json.dumps(value) + "\n")
        protocol.flush()


class Engine:
    def __enter__(self):
        return self

    def __exit__(self, *args):
        pass

    def observe(self, frame):
        return Observation(valid=True, reason="OK", rotation=np.eye(3), face=int(frame[0, 0, 0]))

    def angles(self, rotation, baseline):
        return (0.0, 0.0, 0.0)

    def embedding(self, obs):
        vector = np.zeros(512)
        vector[obs.face] = 1
        return vector


def worker():
    ai_worker.annotate = lambda frame, obs: frame
    with contextlib.redirect_stdout(sys.stderr):
        ai_worker.run_worker(
            frames,
            commands,
            results,
            stop,
            {
                "pitch_sign": 1,
                "yaw_sign": 1,
                "distance_threshold": 0.45,
                "automatic_recognition": True,
            },
            Engine,
        )


def produce():
    sequence = 0
    while not stop.is_set():
        sequence += 1
        ai_worker.offer_latest(
            frames,
            {
                "sequence": sequence,
                "captured_at": time.monotonic(),
                "frame": np.full((4, 4, 3), marker, np.uint8),
            },
        )
        time.sleep(0.05)


def consume():
    while not stop.is_set():
        try:
            state = results.get(timeout=0.1)
        except queue.Empty:
            continue
        if "fatal" in state:
            emit({"type": "fatal", "error": state["fatal"]})
            return
        state.pop("frame", None)
        state.pop("export_reference", None)
        state["age_ms"] = max(0, (time.monotonic() - state["captured_at"]) * 1000)
        emit({"type": "state", "state": state})


for target in [worker, produce, consume]:
    threading.Thread(target=target, daemon=True).start()
try:
    command_id = 0
    for line in sys.stdin:
        value = json.loads(line)
        if value["key"] == "quit":
            break
        if value["key"] == "test_person":
            marker = int(value["reference"]["person"])
        command_id += 1
        value.update(id=command_id, created_at=time.monotonic())
        commands.put(value, timeout=1)
        emit({"type": "queued", "id": command_id, "request_id": value.get("request_id", 0)})
finally:
    stop.set()
