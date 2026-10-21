# 파일 역할: 카메라 프레임을 구간별 MP4로 저장하고 JSON 메타데이터를 갱신합니다. 비정상 종료한 파일의 복구를 지원합니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

"""Camera-owned recording. One latest frame, independent encoder thread, atomic metadata.

OpenCV is injected so timing/recovery can be tested without loading the AI stack.
Completed media and JSON stay on the recognition PC; only metadata is synced to DB.
"""

from datetime import datetime, timezone, timedelta
from pathlib import Path
import json
import os
import queue
import shutil
import threading
import time
import uuid


def utc_now():
    return datetime.now(timezone.utc)


def stamp(value):
    return value.isoformat().replace("+00:00", "Z")


def atomic_json(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    with temporary.open("w", encoding="utf-8") as stream:
        json.dump(value, stream, ensure_ascii=True, allow_nan=False)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


class SegmentRecorder:
    FPS = 10
    SECONDS = 60
    MAX_FRAME_AGE = 0.5
    MIN_FREE_BYTES = 20 * 1024**3

    def __init__(self, root, cv2, *, fps=10, seconds=60, min_free_bytes=None):
        self.root, self.cv2 = Path(root).resolve(), cv2
        self.fps, self.seconds = fps, seconds
        self.min_free = self.MIN_FREE_BYTES if min_free_bytes is None else min_free_bytes
        self.frames, self.notices = queue.Queue(1), queue.Queue(8)
        self.stop = threading.Event()
        self.writer = self.entry = self.manifest = None
        self.count = 0
        self.not_before = None
        self.thread = None
        self.lock = None

    def notice(self, message):
        try:
            self.notices.put_nowait(message)
        except queue.Full:
            pass

    def start(self):
        self.root.mkdir(parents=True, exist_ok=True)
        self.lock = (self.root / ".recording.lock").open("a+b")
        try:
            if os.name == "nt":
                import msvcrt

                self.lock.seek(0)
                if not self.lock.read(1):
                    self.lock.write(b"0")
                    self.lock.flush()
                self.lock.seek(0)
                msvcrt.locking(self.lock.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl

                fcntl.flock(self.lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except Exception:
            self.lock.close()
            self.lock = None
            raise RuntimeError("Another recognition recorder is active")
        self.thread = threading.Thread(target=self.run, name="AccessRecording", daemon=True)
        self.thread.start()

    def offer(self, frame, captured_at=None):
        if self.stop.is_set():
            return
        item = (frame.copy(), time.monotonic() if captured_at is None else captured_at)
        try:
            self.frames.put_nowait(item)
        except queue.Full:
            try:
                self.frames.get_nowait()
            except queue.Empty:
                pass
            try:
                self.frames.put_nowait(item)
            except queue.Full:
                pass

    def open_segment(self, frame):
        if shutil.disk_usage(self.root).free < self.min_free:
            raise RuntimeError("Less than 20GB free; recording stopped")
        start = max(utc_now(), self.not_before) if self.not_before else utc_now()
        folder = self.root / start.strftime("%Y%m%d")
        folder.mkdir(exist_ok=True)
        segment_id = uuid.uuid4()
        height, width = frame.shape[:2]
        if width < 2 or height < 2 or width > 1280 or height > 1280:
            raise ValueError("Unsupported recording dimensions")
        path = folder / (segment_id.hex + ".mp4")
        self.manifest = folder / (segment_id.hex + ".json")
        self.entry = dict(
            SegmentId=str(segment_id),
            CameraCode="CAM-01",
            StartTime=stamp(start),
            EndTime=None,
            FilePath=str(path),
            Status="RECORDING",
            FileSizeBytes=None,
            FrameCount=0,
            Fps=self.fps,
            Format="mp4v",
            Width=width,
            Height=height,
        )
        atomic_json(self.manifest, self.entry)
        self.count = 0
        self.writer = self.cv2.VideoWriter(
            str(path), self.cv2.VideoWriter_fourcc(*"mp4v"), self.fps, (width, height)
        )
        if not self.writer.isOpened():
            raise RuntimeError("OpenCV mp4v encoder could not open")
        self.notice("녹화 중 · 60초 구간 · MP4 / 10fps")

    def close_segment(self, failed=False):
        if self.entry is None:
            return
        if self.writer is not None:
            self.writer.release()
            self.writer = None
        entry = self.entry
        path = Path(entry["FilePath"])
        size = path.stat().st_size if path.exists() else 0
        entry["FrameCount"] = self.count
        if self.count > 0:
            start = datetime.fromisoformat(entry["StartTime"].replace("Z", "+00:00"))
            entry["EndTime"] = stamp(start + timedelta(seconds=self.count / self.fps))
            self.not_before = start + timedelta(seconds=self.count / self.fps)
        entry["FileSizeBytes"] = size
        usable = False
        if not failed and self.count > 0 and size > 0:
            video = self.cv2.VideoCapture(str(path))
            try:
                ok, _ = video.read()
                frames = int(video.get(self.cv2.CAP_PROP_FRAME_COUNT))
                usable = bool(video.isOpened() and ok and frames == self.count)
            finally:
                video.release()
        entry["Status"] = "READY" if usable else "FAILED"
        atomic_json(self.manifest, entry)
        self.entry = self.manifest = None
        self.notice(
            "녹화 구간 저장 완료 · DB 동기화 대기"
            if usable
            else "녹화 파일 오류 · 구간 조회에서 FAILED 확인"
        )
        if not usable and not failed:
            raise RuntimeError("Recorded MP4 could not be verified")

    def recover(self):
        # Exclusive lock proves no other recorder is writing these manifests.
        # Incomplete MP4s are FAILED unless the decoder can actually read them.
        for manifest in sorted(self.root.glob("*/*.json")):
            try:
                entry = json.loads(manifest.read_text(encoding="utf-8"))
                if entry.get("Status") != "RECORDING":
                    continue
                sid = uuid.UUID(entry["SegmentId"])
                start = datetime.fromisoformat(entry["StartTime"].replace("Z", "+00:00"))
                expected = self.root / start.strftime("%Y%m%d") / (sid.hex + ".mp4")
                if (
                    manifest != expected.with_suffix(".json")
                    or Path(entry["FilePath"]).resolve() != expected
                ):
                    continue
                video = self.cv2.VideoCapture(str(expected))
                try:
                    count = int(video.get(self.cv2.CAP_PROP_FRAME_COUNT))
                    fps = float(video.get(self.cv2.CAP_PROP_FPS))
                    ok, _ = video.read()
                    usable = video.isOpened() and ok and 0 < count <= 600 and fps == self.fps
                finally:
                    video.release()
                entry["Status"] = "READY" if usable else "FAILED"
                entry["FrameCount"] = count if usable else 0
                entry["EndTime"] = stamp(start + timedelta(seconds=count / fps)) if usable else None
                entry["FileSizeBytes"] = expected.stat().st_size if expected.exists() else 0
                atomic_json(manifest, entry)
                self.notice("이전 미완료 녹화 복구 · 구간 목록 확인")
            except (OSError, ValueError, KeyError, TypeError):
                continue

    def run(self):
        latest = None
        next_tick = 0
        try:
            self.recover()
            self.notice("녹화 준비 완료 · 카메라 프레임 대기")
            while not self.stop.is_set():
                try:
                    latest = self.frames.get(timeout=0.01)
                except queue.Empty:
                    pass
                now = time.monotonic()
                if latest is None or now - latest[1] > self.MAX_FRAME_AGE:
                    if self.entry is not None:
                        self.close_segment()
                        self.notice("녹화 대기 · 새 카메라 프레임 없음")
                    latest = None
                    continue
                frame = latest[0]
                if self.writer is not None and frame.shape[:2] != (
                    self.entry["Height"],
                    self.entry["Width"],
                ):
                    self.close_segment()
                if self.writer is None:
                    if now < next_tick:
                        continue
                    self.open_segment(frame)
                    next_tick = now
                if now - next_tick > self.MAX_FRAME_AGE:
                    self.close_segment()
                    latest = None
                    self.notice("녹화 지연 · 새 구간에서 재개")
                    continue
                if now >= next_tick:
                    self.writer.write(frame)
                    self.count += 1
                    next_tick += 1 / self.fps
                    if self.count >= self.fps * self.seconds:
                        self.close_segment()
        except Exception as exc:
            self.notice(
                "녹화 오류 · 카메라 재연결 필요: " + type(exc).__name__ + " · " + str(exc)[:160]
            )
            try:
                self.close_segment(failed=True)
            except Exception:
                pass
        finally:
            try:
                self.close_segment()
            except Exception:
                self.notice("녹화 종료 오류 · 구간 파일 확인 필요")
            self.stop.set()
            if self.lock is not None:
                self.lock.close()
                self.lock = None

    def close(self):
        self.stop.set()
        if self.thread is not None:
            self.thread.join(4)
            if self.thread.is_alive():
                self.notice("녹화 종료 지연 · 재실행 시 미완료 구간 확인")
