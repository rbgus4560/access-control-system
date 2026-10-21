# 파일 역할: 모델 초기화가 지연될 때 작업 스택과 시작 단계를 출력해 대기 원인을 찾도록 돕습니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

"""Bounded, value-free stack snapshots for a stalled AI startup thread."""

import sys
import traceback
import contextlib
import threading
import time


def worker_stack(worker, elapsed, stream=None):
    """Log code locations only: never frames, feature values, or frame locals."""
    stream = sys.stderr if stream is None else stream
    ident = getattr(worker, "ident", None)
    frame = sys._current_frames().get(ident) if ident is not None else None
    print(f"[startup diagnostic 6.2] AI waiting {elapsed:.0f}s", file=stream, flush=True)
    if frame is None:
        print(
            "[startup diagnostic 6.2] No in-process worker frame available", file=stream, flush=True
        )
        return False
    try:
        # The innermost calls identify the active import, lock or native call.
        for line in traceback.format_stack(frame, limit=10):
            print(line.rstrip(), file=stream, flush=True)
        print("[startup diagnostic 6.2] End of AI worker stack", file=stream, flush=True)
    finally:
        del frame
    return True


@contextlib.contextmanager
def watch_startup(label, interval=45, stream=None):
    """Monitor the thread doing native imports, even before the camera opens."""
    output = sys.stderr if stream is None else stream
    owner = threading.current_thread()
    finished = threading.Event()
    started = time.monotonic()

    def monitor():
        while not finished.wait(interval):
            print("[startup 6.3] Still preparing: " + label, file=output, flush=True)
            worker_stack(owner, time.monotonic() - started, output)

    watcher = threading.Thread(target=monitor, name="StartupDiagnostic", daemon=True)
    watcher.start()
    try:
        yield
    finally:
        finished.set()
        watcher.join(1)
