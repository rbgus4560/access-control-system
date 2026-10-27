# 파일 역할: WPF와 JSON 명령·상태를 교환합니다. 카메라 수신과 녹화, AI 작업자 실행을 연결합니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

"""WPF child process: JSON lines over redirected standard input/output.
Camera belongs to this process; a dedicated worker owns all AI state.
Thread mode keeps WPF's redirected process startup to one Python interpreter.
"""

import argparse
import contextlib
import json
import multiprocessing as mp
import queue
import sys
import threading
import time
import traceback
from ai_worker import run_worker, offer_latest, RESULT_MAX_AGE
from startup_diagnostics import worker_stack, watch_startup


def worker_logs_to_stderr(*args):
    with contextlib.redirect_stdout(sys.stderr):
        run_worker(*args)


# 명령 수신·카메라·AI 상태 출력을 분리합니다. 표준 출력은 WPF가 읽을 JSON 메시지 전용입니다.
def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--camera", type=int, default=0)
    parser.add_argument("--backend", choices=["dshow", "msmf", "any"], default="dshow")
    parser.add_argument("--role", choices=["enrollment", "recognition"], required=True)
    parser.add_argument("--yaw-sign", type=int, choices=[-1, 1], default=1)
    parser.add_argument("--pitch-sign", type=int, choices=[-1, 1], default=1)
    parser.add_argument("--worker-mode", choices=["thread", "process"], default="thread")
    parser.add_argument("--record-root", default=None)
    args = parser.parse_args()
    protocol = sys.stdout
    sys.stdout = sys.stderr

    def emit(packet):
        protocol.write(
            json.dumps(packet, ensure_ascii=True, allow_nan=False, separators=(",", ":")) + "\n"
        )
        protocol.flush()

    print(
        f"[bridge 6.4] Starting {args.role}; worker={args.worker_mode}; HOG empty-result retry enabled",
        file=sys.stderr,
        flush=True,
    )
    emit({"type": "progress", "message": "AI 연결 6.4 · 라이브러리 준비 후 카메라 연결"})
    context = mp.get_context("spawn")
    channels = context.Queue if args.worker_mode == "process" else queue.Queue
    frames, commands, results = channels(1), channels(8), channels(1)
    stop = context.Event() if args.worker_mode == "process" else threading.Event()
    closing = threading.Event()
    local_commands = queue.Queue(8)

    def read_commands():
        try:
            while not closing.is_set():
                line = sys.stdin.readline(150001)
                if not line:
                    break
                if len(line) > 150000 or not line.endswith("\n"):
                    raise ValueError("Command exceeds protocol size limit")
                command = json.loads(line)
                if command.get("key") == "quit":
                    break
                if command.get("key") not in (
                    "0",
                    "e",
                    "space",
                    "x",
                    "t",
                    "c",
                    "export",
                    "load",
                    "gallery_begin",
                    "gallery_add",
                    "gallery_commit",
                ):
                    raise ValueError("Unknown AI command")
                if args.role == "enrollment" and command["key"] in (
                    "t",
                    "load",
                    "gallery_begin",
                    "gallery_add",
                    "gallery_commit",
                ):
                    raise ValueError("Command not available in enrollment")
                if args.role == "recognition" and command["key"] in ("e", "space", "x", "export"):
                    raise ValueError("Command not available in recognition")
                local_commands.put_nowait(command)
        except Exception as exc:
            print(type(exc).__name__, str(exc), file=sys.stderr, flush=True)
        finally:
            closing.set()

    camera = None
    recorder = None
    worker_type = context.Process if args.worker_mode == "process" else threading.Thread
    process = worker_type(
        target=worker_logs_to_stderr,
        args=(
            frames,
            commands,
            results,
            stop,
            {
                "pitch_sign": args.pitch_sign,
                "yaw_sign": args.yaw_sign,
                "distance_threshold": 0.45,
                "detector_mode": "fast",
                "detector_width": 480,
                "diagnostics": True,
                "continuous_enrollment": args.role == "enrollment",
                "guided_enrollment": args.role == "enrollment",
                "extended_poses": True,
                "automatic_recognition": args.role == "recognition",
            },
        ),
        name="AccessFaceAI",
    )
    worker_started = False
    try:
        # The Windows report stalled loading scipy.linalg._fblas while the
        # camera was active and MediaPipe was importing in the AI thread.
        # Match the successful check_setup order: imports on the main thread,
        # before opening DirectShow. The worker still owns all inference state.
        with watch_startup("main-thread native AI imports before camera"):
            from face_engine import FaceEngine  # establishes TF legacy/cache environment

            print(
                "[startup 6.3] Main thread: importing SciPy linear algebra",
                file=sys.stderr,
                flush=True,
            )
            emit({"type": "progress", "message": "AI 연결 6.3 · SciPy 라이브러리 준비 중"})
            import scipy.linalg

            print(
                "[startup 6.3] Main thread: SciPy ready; importing MediaPipe",
                file=sys.stderr,
                flush=True,
            )
            emit({"type": "progress", "message": "AI 연결 6.3 · MediaPipe 라이브러리 준비 중"})
            import mediapipe

            print(
                "[startup 6.3] Main thread: MediaPipe ready; importing DeepFace",
                file=sys.stderr,
                flush=True,
            )
            emit({"type": "progress", "message": "AI 연결 6.3 · DeepFace 라이브러리 준비 중"})
            from deepface import DeepFace
            import cv2

            cv2.setNumThreads(2)
            print("[startup 6.3] Native imports ready; opening camera", file=sys.stderr, flush=True)
            emit(
                {
                    "type": "progress",
                    "message": "AI 연결 6.3 · 라이브러리 준비 완료 · 카메라 여는 중",
                }
            )
        camera = cv2.VideoCapture(
            args.camera,
            {"dshow": cv2.CAP_DSHOW, "msmf": cv2.CAP_MSMF, "any": cv2.CAP_ANY}[args.backend],
        )
        if not camera.isOpened():
            raise RuntimeError(
                "Cannot open camera. Close other camera apps or select another index/backend."
            )
        camera.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
        camera.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
        # 장치가 지원할 때만 적용됩니다. 실측 FPS 보장값은 아닙니다.
        camera.set(cv2.CAP_PROP_FPS, 30)
        if args.role == "recognition" and args.record_root:
            try:
                from segment_recorder import SegmentRecorder

                recorder = SegmentRecorder(args.record_root, cv2)
                recorder.start()
            except Exception as exc:
                emit(
                    {
                        "type": "recording_status",
                        "message": "녹화 시작 오류 · 카메라 재연결 필요: " + str(exc)[:160],
                    }
                )
                recorder = None
        process.start()
        worker_started = True
        print("[bridge 6.1] Camera open; AI worker started", file=sys.stderr, flush=True)
        threading.Thread(target=read_commands, daemon=True).start()
        emit({"type": "ready"})
        sequence, command_id, exported_id = 0, 0, 0
        first_state = False
        started_at, last_wait_notice = time.monotonic(), time.monotonic()
        next_stack_at = started_at + 45
        next_video, next_analysis, next_record = 0.0, 0.0, 0.0
        failures = 0
        while not closing.is_set():
            ok, frame = camera.read()
            if not ok:
                failures += 1
                if failures >= 5:
                    raise RuntimeError("Camera stopped returning frames")
                continue
            failures = 0
            now = time.monotonic()
            if frame.shape[1] > 640:
                frame = cv2.resize(frame, (640, round(frame.shape[0] * 640 / frame.shape[1])))
            if recorder is not None:
                if now >= next_record:
                    recorder.offer(frame, now)
                    next_record = now + 0.1
                while True:
                    try:
                        emit({"type": "recording_status", "message": recorder.notices.get_nowait()})
                    except queue.Empty:
                        break
            while True:
                try:
                    command = local_commands.get_nowait()
                except queue.Empty:
                    break
                command_id += 1
                # The bridge assigns trusted capture timing and sequence numbers.
                command["id"], command["created_at"] = command_id, time.monotonic()
                try:
                    commands.put_nowait(command)
                    emit(
                        {
                            "type": "queued",
                            "key": command["key"],
                            "id": command_id,
                            "request_id": command.get("request_id", 0),
                        }
                    )
                except queue.Full:
                    emit(
                        {
                            "type": "error",
                            "error": "AI command queue is busy; retry once it settles.",
                        }
                    )
            if now >= next_analysis:
                sequence += 1
                offer_latest(
                    frames, {"sequence": sequence, "captured_at": now, "frame": frame.copy()}
                )
                next_analysis = now + 0.2
            if now >= next_video:
                ok, jpg = cv2.imencode(".jpg", frame, [cv2.IMWRITE_JPEG_QUALITY, 75])
                if ok:
                    import base64

                    emit({"type": "live", "jpeg": base64.b64encode(jpg).decode("ascii")})
                next_video = now + 1 / 30
            while True:
                try:
                    item = results.get_nowait()
                except queue.Empty:
                    break
                if item.get("fatal"):
                    raise RuntimeError(item["fatal"])
                if not first_state:
                    first_state = True
                    print("[bridge 6.1] First AI analysis received", file=sys.stderr, flush=True)
                analyzed = item.pop("frame")
                exported = item.pop("export_reference", None)
                item["age_ms"] = max(0, (time.monotonic() - item["captured_at"]) * 1000)
                if item["age_ms"] > RESULT_MAX_AGE * 1000:
                    item["comparison"] = None
                ok, jpg = cv2.imencode(".jpg", analyzed, [cv2.IMWRITE_JPEG_QUALITY, 75])
                import base64

                emit(
                    {
                        "type": "state",
                        "state": item,
                        "jpeg": base64.b64encode(jpg).decode("ascii") if ok else None,
                    }
                )
                if exported and exported["id"] > exported_id:
                    exported_id = exported["id"]
                    emit({"type": "reference", "reference": exported})
            if not first_state and now - last_wait_notice >= 20:
                last_wait_notice = now
                notice = f"AI 첫 분석 대기 {int(now - started_at)}초 · 작업 실행 여부: {process.is_alive()}"
                print("[bridge 6.1] " + notice, file=sys.stderr, flush=True)
                emit({"type": "progress", "message": notice})
            if not first_state and now >= next_stack_at:
                next_stack_at = now + 60
                worker_stack(process, now - started_at)
            if not process.is_alive():
                raise RuntimeError("AI process stopped unexpectedly")
    except (BrokenPipeError, KeyboardInterrupt):
        pass
    except Exception as exc:
        traceback.print_exc(file=sys.stderr)
        try:
            emit({"type": "fatal", "error": f"{type(exc).__name__}: {exc}"})
        except BrokenPipeError:
            pass
        return 1
    finally:
        closing.set()
        stop.set()
        if camera is not None:
            camera.release()
        if recorder is not None:
            recorder.close()
        if worker_started:
            process.join(2)
            if process.is_alive() and args.worker_mode == "process":
                process.terminate()
                process.join(1)
        if args.worker_mode == "process":
            for channel in (frames, commands, results):
                channel.cancel_join_thread()
                channel.close()
    return 0


if __name__ == "__main__":
    mp.freeze_support()
    raise SystemExit(main())
