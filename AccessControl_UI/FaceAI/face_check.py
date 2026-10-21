# 파일 역할: WPF 없이 카메라·얼굴 등록·비교 동작을 확인하는 개발용 실행 화면입니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

"""Live camera UI and a separate AI process. No TCP, DB or recording."""

import argparse
import multiprocessing as mp
import queue
import time
import traceback
import numpy as np
from ai_worker import offer_latest, current_snapshot


def fit_panel(image, width=640, height=480):
    import cv2

    out = np.zeros((height, width, 3), dtype=np.uint8)
    if image is not None:
        scale = min(width / image.shape[1], height / image.shape[0])
        resized = cv2.resize(
            image, (max(1, round(image.shape[1] * scale)), max(1, round(image.shape[0] * scale)))
        )
        y, x = (height - resized.shape[0]) // 2, (width - resized.shape[1]) // 2
        out[y : y + resized.shape[0], x : x + resized.shape[1]] = resized
    return out


def fmt_ms(value):
    return "--" if value is None else f"{value:.0f}"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--camera", type=int, default=0)
    parser.add_argument("--backend", choices=["dshow", "msmf", "any"], default="dshow")
    parser.add_argument("--yaw-sign", type=int, choices=[-1, 1], default=1)
    parser.add_argument("--pitch-sign", type=int, choices=[-1, 1], default=1)
    parser.add_argument("--distance-threshold", type=float, default=0.45)
    parser.add_argument(
        "--ai-fps",
        type=float,
        default=5.0,
        help="Maximum frame submission rate, NOT achieved AI FPS",
    )
    parser.add_argument("--detector-mode", choices=["fast", "standard"], default="fast")
    parser.add_argument("--detector-width", type=int, default=480)
    args = parser.parse_args()
    if not np.isfinite(args.distance_threshold) or not 0 < args.distance_threshold < 2:
        parser.error("distance-threshold must be finite and between 0 and 2")
    if not np.isfinite(args.ai_fps) or not 0.5 <= args.ai_fps <= 30:
        parser.error("ai-fps must be between 0.5 and 30")
    if not 320 <= args.detector_width <= 1280:
        parser.error("detector-width must be between 320 and 1280")
    import cv2
    from ai_worker import run_worker

    cv2.setNumThreads(2)
    api = {"dshow": cv2.CAP_DSHOW, "msmf": cv2.CAP_MSMF, "any": cv2.CAP_ANY}[args.backend]
    cap = cv2.VideoCapture(args.camera, api)
    if not cap.isOpened():
        cap.release()
        raise RuntimeError(
            "Cannot open camera. Close other camera apps; try --camera 1 or --backend msmf"
        )
    context = mp.get_context("spawn")
    frames, commands, results = (
        context.Queue(maxsize=1),
        context.Queue(maxsize=8),
        context.Queue(maxsize=1),
    )
    stop = context.Event()
    config = {
        "yaw_sign": args.yaw_sign,
        "pitch_sign": args.pitch_sign,
        "distance_threshold": args.distance_threshold,
        "detector_mode": args.detector_mode,
        "detector_width": args.detector_width,
    }
    worker = context.Process(
        target=run_worker, args=(frames, commands, results, stop, config), name="FaceAI"
    )
    title = "Access Face AI Check v3 - LIVE / ANALYZED"
    snapshot = None
    ui_message = "AI is loading. Live video continues; wait for Face status."
    fatal = None
    sequence, command_id, required_command = 0, 0, 0
    next_submit = 0.0
    fps_started, fps_count, ui_fps = time.monotonic(), 0, 0.0
    ai_fps = 0.0
    last_finished = None
    read_failures = 0
    last_key_time, last_key = 0.0, None
    try:
        cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
        cv2.namedWindow(title, cv2.WINDOW_NORMAL)
        cv2.resizeWindow(title, 1280, 810)
        worker.start()
        print(
            "v3: camera/UI and AI run in separate processes. Features stay in memory.", flush=True
        )
        print(
            "Left = LIVE camera; right = last ANALYZED frame. Scores belong to the right frame.",
            flush=True,
        )
        while True:
            ok, frame = cap.read()
            if not ok:
                read_failures += 1
                snapshot = None
                if read_failures >= 5:
                    raise RuntimeError("Camera stopped returning frames")
                if cv2.waitKey(20) & 0xFF in (ord("q"), ord("Q")):
                    break
                continue
            read_failures = 0
            now = time.monotonic()
            if frame.shape[1] > 640:
                frame = cv2.resize(frame, (640, round(frame.shape[0] * 640 / frame.shape[1])))
            while True:
                try:
                    item = results.get_nowait()
                except queue.Empty:
                    break
                if item.get("fatal"):
                    fatal = item["fatal"]
                    snapshot = None
                else:
                    if last_finished is not None:
                        ai_fps = 1.0 / max(item["finished_at"] - last_finished, 1e-6)
                    last_finished = item["finished_at"]
                    snapshot = item
            if not worker.is_alive() and worker.exitcode is not None:
                fatal = fatal or f"AI process stopped (exit {worker.exitcode}); see terminal"
                snapshot = None
            if fatal is None and now >= next_submit:
                sequence += 1
                offer_latest(
                    frames, {"sequence": sequence, "captured_at": now, "frame": frame.copy()}
                )
                next_submit = now + 1.0 / args.ai_fps
            fps_count += 1
            if now - fps_started >= 1:
                ui_fps = fps_count / (now - fps_started)
                fps_count, fps_started = 0, now
            fresh = fatal is None and current_snapshot(snapshot, now, required_command)
            waiting = snapshot is not None and snapshot.get("command_id", 0) < required_command
            left = fit_panel(frame)
            right = fit_panel(snapshot.get("frame") if snapshot else None)
            header = np.zeros((30, 1280, 3), dtype=np.uint8)
            cv2.putText(
                header,
                f"LIVE CAMERA / UI {ui_fps:.1f} fps",
                (10, 21),
                cv2.FONT_HERSHEY_SIMPLEX,
                0.55,
                (70, 240, 70),
                1,
                cv2.LINE_AA,
            )
            age = max(0, now - snapshot["captured_at"]) if snapshot else None
            age_label = f"{age:.2f}s old" if age is not None else "loading"
            cv2.putText(
                header,
                f"LAST ANALYZED FRAME / {age_label}",
                (650, 21),
                cv2.FONT_HERSHEY_SIMPLEX,
                0.55,
                (0, 200, 255),
                1,
                cv2.LINE_AA,
            )
            rows = [
                f"v3 | dlib ({args.detector_mode}) + MediaPipe Face Landmarker + DeepFace ArcFace / CPU"
            ]
            if fresh:
                rows.append(snapshot["status"])
            elif fatal:
                rows.append("AI STOPPED: " + fatal[:150])
            elif waiting:
                rows.append("Command queued - hold pose while AI processes a NEW frame")
            elif snapshot:
                rows.append("AI RESULT EXPIRED - comparison hidden; waiting for a fresh analysis")
            else:
                rows.append("AI loading / waiting for the first analyzed frame")
            if snapshot:
                pitch, yaw, roll = snapshot["angles"]
                rows.append(
                    f'Last analyzed pose: {snapshot["pose"]} | Pitch {pitch:.1f} Yaw {yaw:.1f} Roll {roll:.1f}'
                )
                rows.append(
                    f'Reference: {snapshot["reference_count"]}/5 | Next: {snapshot["expected"] or "-"} | Comparison: {"ON" if snapshot["matching"] else "OFF"}'
                )
                timings = snapshot["timings"]
                rows.append(
                    "AI timings (ms): mesh "
                    + fmt_ms(timings.get("mesh_ms"))
                    + " | dlib+align "
                    + fmt_ms(timings.get("dlib_ms"))
                    + " | ArcFace "
                    + fmt_ms(timings.get("embedding_ms"))
                    + " | total "
                    + fmt_ms(timings.get("total_ms"))
                )
                rows.append(
                    "dlib detail (ms): detect "
                    + fmt_ms(timings.get("detect_ms"))
                    + " | original-image align "
                    + fmt_ms(timings.get("align_ms"))
                    + " | detector input width "
                    + str(timings.get("detector_input_width", "--"))
                )
                rows.append(
                    f"UI {ui_fps:.1f} fps | AI output {ai_fps:.1f} fps | analyzed frame age {age_label} | -- means skipped"
                )
                comparison = snapshot.get("comparison") if fresh else None
                if comparison is not None:
                    distance, nearest, candidate = comparison
                    rows.append(
                        f'{"MATCH CANDIDATE" if candidate else "NO MATCH"} distance={distance:.3f} cutoff={args.distance_threshold:.3f} ({nearest}) - RIGHT FRAME ONLY'
                    )
                else:
                    rows.append("Comparison: no current valid result")
                rows.append(snapshot["message"] if not waiting else ui_message)
            else:
                rows.extend(["", "", "", "", "Comparison: no current valid result", ui_message])
            rows.append(
                "0 calibrate | E enroll | SPACE capture | T compare | C clear | Q quit (upper/lower case accepted)"
            )
            panel = np.zeros((len(rows) * 26 + 12, 1280, 3), dtype=np.uint8)
            for i, row in enumerate(rows):
                color = (0, 185, 255) if "MULTIPLE FACES" in row else (230, 230, 230)
                cv2.putText(
                    panel,
                    row,
                    (10, 23 + 26 * i),
                    cv2.FONT_HERSHEY_SIMPLEX,
                    0.48,
                    color,
                    1,
                    cv2.LINE_AA,
                )
            cv2.imshow(title, np.vstack([header, np.hstack([left, right]), panel]))
            key = cv2.waitKey(1) & 0xFF
            if ord("A") <= key <= ord("Z"):
                key += 32
            if key == ord("q") or cv2.getWindowProperty(title, cv2.WND_PROP_VISIBLE) < 1:
                break
            if key in (ord("0"), ord("e"), 32, ord("t"), ord("c")) and fatal is None:
                pressed_at = time.monotonic()
                if key == last_key and pressed_at - last_key_time < 0.25:
                    continue
                last_key, last_key_time = key, pressed_at
                command_id += 1
                command_key = "space" if key == 32 else chr(key)
                try:
                    commands.put_nowait(
                        {"id": command_id, "key": command_key, "created_at": pressed_at}
                    )
                    required_command = command_id
                    ui_message = f"Command {command_key} queued. Hold pose until the analyzed result updates."
                except queue.Full:
                    ui_message = "Command queue busy. Wait, then press once again."
    finally:
        stop.set()
        cap.release()
        cv2.destroyAllWindows()
        if worker.pid is not None:
            worker.join(timeout=3)
            if worker.is_alive():
                worker.terminate()
                worker.join(timeout=2)
        for channel in (frames, commands, results):
            channel.cancel_join_thread()
            channel.close()
    return 0


if __name__ == "__main__":
    mp.freeze_support()
    try:
        raise SystemExit(main())
    except Exception:
        traceback.print_exc()
        raise SystemExit(1)
