# 파일 역할: AI 엔진과 얼굴 등록·식별 상태를 한 작업자에서 관리합니다. 유효한 최신 프레임만 처리합니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

"""Single-owner AI process. Only images/commands enter; snapshots leave."""

import time
import queue
import numpy as np
from face_logic import (
    Enrollment,
    ContinuousEnrollment,
    NINE_POSES,
    classify_pose,
    classify_coverage_pose,
    complete_reference,
    cosine_distance,
    validate_reference,
    PIPELINE_ID,
)

from guided_enrollment import GuidedEnrollment
from recognition_start import NeutralPoseGate
from face_gallery import FaceGallery

RESULT_MAX_AGE = 2.5
COMMAND_MAX_AGE = 5.0


def offer_latest(channel, value):
    """Bounded queue: replace pending work rather than accumulate camera delay."""
    try:
        channel.put_nowait(value)
        return True
    except queue.Full:
        try:
            channel.get_nowait()
        except queue.Empty:
            pass  # A multiprocessing feeder may not yet have published the item.
        try:
            channel.put_nowait(value)
            return True
        except queue.Full:
            return False


def current_snapshot(snapshot, now, required_command=0):
    return (
        snapshot is not None
        and snapshot.get("frame") is not None
        and snapshot.get("command_id", 0) >= required_command
        and 0 <= now - snapshot["captured_at"] <= RESULT_MAX_AGE
    )


def annotate(frame, obs):
    import cv2

    view = frame.copy()
    height, width = view.shape[:2]
    meshes = getattr(obs, "meshes", [])
    for mesh in meshes:
        xy = np.asarray([(p.x * width, p.y * height) for p in mesh])
        left, top = np.maximum(xy.min(axis=0), 0).astype(int)
        right, bottom = np.minimum(xy.max(axis=0), [width - 1, height - 1]).astype(int)
        color = (0, 160, 255) if len(meshes) > 1 else (0, 190, 0)
        cv2.rectangle(view, (left, top), (right, bottom), color, 2)
        if len(meshes) == 1:
            for x, y in xy.astype(int):
                cv2.circle(view, (x, y), 1, color, -1)
    if obs.box is not None:
        x, y, w, h = obs.box
        cv2.rectangle(view, (x, y), (x + w, y + h), (0, 255, 0) if obs.valid else (0, 160, 255), 2)
    return view


# 모델과 등록/검색 상태는 이 함수가 소유합니다. UI 명령은 촬영 시각과 명령 시각을 대조한 뒤 적용합니다.
def run_worker(frames, commands, results, stop, config, engine_factory=None):
    """Model/session and registration state are owned exclusively by this process."""
    continuous = bool(config.get("continuous_enrollment", False))
    guided = continuous and bool(config.get("guided_enrollment", False))

    def new_enrollment():
        return (
            GuidedEnrollment() if guided else ContinuousEnrollment() if continuous else Enrollment()
        )

    enrollment = new_enrollment()
    baseline = None
    gallery = FaceGallery()
    gallery_mode = False
    gallery_resume = (False, False)
    matching = False
    automatic = bool(config.get("automatic_recognition", False)) and not continuous
    auto_enabled = automatic
    neutral = NeutralPoseGate()
    stable_pose, stable_count, stable_since = None, 0, 0.0
    last_sample_time = None
    command_id = 0
    pending_commands = []
    exported = None
    reference_label = ""
    message = "Look FRONT, press 0 to calibrate; E starts enrollment"
    try:
        if config.get("diagnostics"):
            print("[AI worker 6.1] Entered; importing and loading face models", flush=True)
        if engine_factory is None:
            from face_engine import FaceEngine

            engine_factory = lambda: FaceEngine(
                detector_mode=config.get("detector_mode", "fast"),
                detector_width=config.get("detector_width", 480),
            )
        with engine_factory() as engine:
            if config.get("diagnostics"):
                print("[AI worker 6.1] Models ready; waiting for camera frames", flush=True)
            while not stop.is_set():
                while True:
                    try:
                        pending_commands.append(commands.get_nowait())
                    except queue.Empty:
                        break
                try:
                    packet = frames.get(timeout=0.05)
                except queue.Empty:
                    continue
                if stop.is_set():
                    break
                captured_at, frame = packet["captured_at"], packet["frame"]
                # Frames waiting too long are discarded before any identity processing.
                if time.monotonic() - captured_at > RESULT_MAX_AGE:
                    continue
                obs = None
                comparison = None
                identification = None
                embedding_ms = None
                angles = (0.0, 0.0, 0.0)
                pose = "UNKNOWN"
                started = time.perf_counter()
                observe_ms = None
                try:
                    observed_start = time.perf_counter()
                    obs = engine.observe(frame)
                    observe_ms = (time.perf_counter() - observed_start) * 1000
                    if obs.valid and baseline is not None:
                        angles = engine.angles(obs.rotation, baseline)
                        classifier = (
                            classify_coverage_pose
                            if config.get("extended_poses", continuous)
                            else classify_pose
                        )
                        pose = classifier(
                            *angles, pitch_sign=config["pitch_sign"], yaw_sign=config["yaw_sign"]
                        )
                    valid = obs.valid
                    # Count independent analyzed samples, never UI redraws of one result.
                    if (
                        valid
                        and pose != "UNKNOWN"
                        and pose == stable_pose
                        and last_sample_time is not None
                        and captured_at - last_sample_time <= 2.5
                    ):
                        stable_count += 1
                    elif valid and pose != "UNKNOWN":
                        stable_pose, stable_count, stable_since = pose, 1, captured_at
                    else:
                        stable_pose, stable_count, stable_since = None, 0, captured_at
                    last_sample_time = captured_at
                    stable = valid and stable_count >= 3 and captured_at - stable_since >= 0.6
                    # Keys may arrive while the worker waits for a frame or runs inference.
                    while True:
                        try:
                            pending_commands.append(commands.get_nowait())
                        except queue.Empty:
                            break
                    # Commands are applied to a frame captured AFTER the key press.
                    deferred = []
                    for command in pending_commands:
                        if captured_at < command["created_at"]:
                            deferred.append(command)
                            continue
                        command_id = max(command_id, command["id"])
                        key = command["key"]
                        age = time.monotonic() - command["created_at"]
                        # Clear/cancel/toggle are always honored; stale capture must not execute.
                        if key in ("0", "space") and age > COMMAND_MAX_AGE:
                            message = "Command expired. Hold your pose and press the key again."
                            continue
                        if key == "0":
                            if valid:
                                baseline = obs.rotation.copy()
                                enrollment.cancel()
                                matching = False
                                stable_pose, stable_count = None, 0
                                stable = False
                                pose = "FRONT"
                                angles = (0.0, 0.0, 0.0)
                                message = "FRONT calibrated. Check directions, then E to enroll."
                            else:
                                message = "Calibration rejected: one clear face required"
                        elif key == "e":
                            if guided and valid:
                                baseline = obs.rotation.copy()
                                angles = (0.0, 0.0, 0.0)
                                pose = "FRONT"
                            if baseline is None:
                                message = "First look FRONT and press 0"
                            else:
                                enrollment.start()
                                exported = None
                                matching = False
                                stable_pose, stable_count, stable_since = None, 0, captured_at
                                stable = False
                                message = (
                                    "Auto enrollment: look FRONT first, then turn slowly in any direction"
                                    if continuous
                                    else "Enrollment: FRONT UP DOWN LEFT RIGHT; hold then SPACE"
                                )
                        elif key == "x":
                            enrollment.cancel()
                            stable_pose, stable_count, stable_since = None, 0, captured_at
                            stable = False
                            message = "Enrollment cancelled; previous complete reference retained"
                        elif key == "space":
                            if continuous:
                                message = "Auto enrollment: no capture key needed; hold each direction briefly"
                            elif enrollment.pending is None:
                                message = "Press E to start enrollment"
                            elif not stable or pose != enrollment.expected:
                                message = (
                                    f"Capture rejected. Hold {enrollment.expected}, then SPACE"
                                )
                            else:
                                feature_started = time.perf_counter()
                                feature = engine.embedding(obs)
                                embedding_ms = (time.perf_counter() - feature_started) * 1000
                                front = enrollment.pending.get("FRONT")
                                if front is not None and cosine_distance(front, feature) > 0.60:
                                    message = (
                                        "Capture rejected: differs from FRONT; improve pose/light"
                                    )
                                else:
                                    done = enrollment.capture(pose, feature)
                                    message = (
                                        "Five directions ready. T starts comparison."
                                        if done
                                        else f"Saved {pose}; next {enrollment.expected}"
                                    )
                        elif key == "t":
                            auto_enabled = (
                                False  # An explicit pause stays paused until T or a new reference.
                            )
                            neutral.reset()
                            if (
                                not (
                                    bool(gallery.people)
                                    if gallery_mode
                                    else complete_reference(enrollment.active)
                                )
                                or enrollment.pending is not None
                                or gallery.updating
                            ):
                                message = "Complete all required directions first"
                            else:
                                matching = not matching
                                message = (
                                    "Comparison ON (identity candidate only)"
                                    if matching
                                    else "Comparison OFF"
                                )
                        elif key == "c":
                            enrollment = new_enrollment()
                            gallery = FaceGallery()
                            gallery_mode = False
                            exported, reference_label = None, ""
                            baseline, matching = None, False
                            auto_enabled = automatic
                            neutral.reset()
                            stable_pose, stable_count = None, 0
                            stable = False
                            pose, angles = "UNKNOWN", (0.0, 0.0, 0.0)
                            message = "All references cleared. Look FRONT and press 0."
                        elif key == "export":
                            if (
                                not complete_reference(enrollment.active)
                                or enrollment.pending is not None
                            ):
                                message = "Complete all required directions before transfer"
                            else:
                                exported = {
                                    "id": command["id"],
                                    "pipeline": PIPELINE_ID,
                                    "features": {
                                        k: v.tolist() for k, v in enrollment.active.items()
                                    },
                                }
                                message = "Reference ready for memory transfer (not saved to DB)"
                        elif key == "gallery_begin":
                            if not gallery.updating:
                                gallery_resume = (matching, auto_enabled)
                            matching = False
                            gallery.begin(command["reference"])
                            gallery_mode = True
                            message = "Gallery syncing"
                        elif key == "gallery_add":
                            gallery.add(command["reference"])
                            message = "Gallery syncing"
                        elif key == "gallery_commit":
                            first = not gallery.version
                            gallery.commit(command["reference"])
                            matching = gallery_resume[0] and bool(gallery.people)
                            auto_enabled = automatic if first else gallery_resume[1]
                            reference_label = f"자동 직원 기준 {len(gallery.people)}명"
                            message = "Gallery ready"
                        elif key == "load":
                            matching = False
                            auto_enabled = False  # Invalid replacement must never resume the previous identity.
                            imported = validate_reference(command.get("reference"))
                            enrollment.active = imported
                            enrollment.cancel()
                            matching, exported = False, None
                            auto_enabled = automatic
                            if automatic:
                                baseline = None
                                neutral.reset()
                                stable_pose, stable_count = None, 0
                                pose = "UNKNOWN"
                            reference_label = str(command.get("label", "Temporary reference"))[:180]
                            message = "Reference imported. Calibrate FRONT, then start comparison."
                    pending_commands = deferred
                    if (
                        automatic
                        and auto_enabled
                        and enrollment.pending is None
                        and not gallery.updating
                    ):
                        recent = 0 <= time.monotonic() - captured_at <= RESULT_MAX_AGE
                        if baseline is None:
                            if valid and recent:
                                if neutral.offer(obs.rotation, captured_at, engine.angles):
                                    baseline = obs.rotation.copy()
                                    pose, angles = "FRONT", (0.0, 0.0, 0.0)
                                    stable_pose, stable_count = None, 0
                                    message = "Auto FRONT calibrated"
                                else:
                                    message = "Auto start: look FRONT and hold still briefly"
                            else:
                                neutral.reset()
                        reference_ready = (
                            bool(gallery.people)
                            if gallery_mode
                            else complete_reference(enrollment.active)
                        )
                        if baseline is not None and reference_ready:
                            matching = True
                            message = "Auto comparison ON (identity candidate only)"
                        elif not reference_ready:
                            message = "Auto start: load an employee reference first"
                    if guided and enrollment.pending is not None and valid:
                        adjusted = (
                            angles[0] * config["pitch_sign"],
                            angles[1] * config["yaw_sign"],
                            angles[2],
                        )
                        if enrollment.wants(adjusted, float(getattr(obs, "blur", 0)), captured_at):
                            feature_started = time.perf_counter()
                            # 정면 복귀는 각도 확인만 필요합니다. 새 등록 특징은 다음 방향에서 추출합니다.
                            feature = None if enrollment.returning else engine.embedding(obs)
                            embedding_ms = (time.perf_counter() - feature_started) * 1000
                            if time.monotonic() - captured_at <= RESULT_MAX_AGE:
                                try:
                                    done = enrollment.offer(
                                        adjusted, feature, float(obs.blur), captured_at
                                    )
                                    message = (
                                        "Guided samples ready. Save this employee to DB."
                                        if done
                                        else "Guided collecting: follow the direction and move slowly"
                                    )
                                except ValueError:
                                    message = "Auto capture rejected: face differs from FRONT; improve pose/light"
                    elif continuous and enrollment.pending is not None and valid:
                        if not enrollment.pending and pose != "FRONT":
                            message = "Auto enrollment: look FRONT first"
                        elif pose in NINE_POSES and pose not in enrollment.pending and stable:
                            feature_started = time.perf_counter()
                            feature = engine.embedding(obs)
                            embedding_ms = (time.perf_counter() - feature_started) * 1000
                            if time.monotonic() - captured_at > RESULT_MAX_AGE:
                                message = "Auto capture waiting: analysis was too old; hold again"
                            else:
                                try:
                                    done = enrollment.capture(pose, feature)
                                    message = (
                                        "Nine directions ready. Save this employee to DB."
                                        if done
                                        else f"Auto saved {pose}; remaining {9-len(enrollment.pending)}"
                                    )
                                except ValueError:
                                    message = "Auto capture rejected: face differs from FRONT; improve pose/light"
                            stable_pose, stable_count, stable_since = None, 0, captured_at
                    if (
                        matching
                        and enrollment.pending is None
                        and valid
                        and pose != "UNKNOWN"
                        and not gallery.updating
                    ):
                        feature_started = time.perf_counter()
                        feature = engine.embedding(obs)
                        embedding_ms = (time.perf_counter() - feature_started) * 1000
                        if gallery_mode:
                            identification = gallery.compare(feature, config["distance_threshold"])
                            if identification is not None:
                                comparison = (
                                    identification["distance"],
                                    identification["pose"],
                                    identification["result"] == "MATCH",
                                )
                        else:
                            comparison = enrollment.compare(feature, config["distance_threshold"])
                except Exception as exc:
                    # Every failed inference invalidates previous scores; no stale fallback.
                    comparison = None
                    neutral.reset()
                    stable_pose, stable_count = None, 0
                    message = "AI ERROR: see terminal"
                    print(type(exc).__name__, str(exc), flush=True)
                    # Do not retry a capture command automatically after an exception.
                    for command in pending_commands:
                        command_id = max(command_id, command["id"])
                    pending_commands = []
                elapsed = (time.perf_counter() - started) * 1000
                reason = obs.reason if obs is not None else "ERROR"
                if reason in ("MULTIPLE_FACES", "MULTIPLE_DLIB_FACES"):
                    status = "MULTIPLE FACES - ONE PERSON AT A TIME (comparison paused)"
                    comparison = None
                elif reason != "OK":
                    status = f"Face: {reason} - comparison paused"
                    comparison = None
                else:
                    status = f"Face: OK | Pose: {pose} | stable samples: {stable_count}"
                snapshot = {
                    "sequence": packet["sequence"],
                    "captured_at": captured_at,
                    "finished_at": time.monotonic(),
                    "command_id": command_id,
                    "frame": annotate(frame, obs) if obs is not None else frame.copy(),
                    "status": status,
                    "reason": reason,
                    "pose": pose,
                    "angles": angles,
                    "stable": stable_count,
                    "reference_count": (
                        (
                            identification["sample_count"]
                            if identification
                            else 5 if gallery.people else 0
                        )
                        if gallery_mode
                        else len(enrollment.active)
                    ),
                    "gallery_mode": gallery_mode,
                    "gallery_id": gallery.version,
                    "gallery_count": len(gallery.people),
                    "identification": identification,
                    "expected": enrollment.expected,
                    "matching": matching,
                    "automatic_recognition": automatic,
                    "auto_start_enabled": auto_enabled,
                    "pending_count": (
                        len(enrollment.pending) if enrollment.pending is not None else 0
                    ),
                    "enrolling": enrollment.pending is not None,
                    "calibrated": baseline is not None,
                    "enrollment_target": 9 if continuous else 5,
                    "coverage": list(
                        enrollment.pending if enrollment.pending is not None else enrollment.active
                    ),
                    "auto_enrollment": continuous,
                    "guided_enrollment": guided,
                    "enrollment_hint": (
                        enrollment.hint(
                            (angles[0] * config["pitch_sign"], angles[1] * config["yaw_sign"], angles[2]),
                            float(getattr(obs, "blur", 0)),
                        )
                        if guided and valid and enrollment.pending is not None
                        else ""
                    ),
                    "accepted_candidates": enrollment.accepted if guided else 0,
                    "export_reference": exported,
                    "reference_label": reference_label,
                    "comparison": comparison,
                    "message": message,
                    "timings": {
                        **(obs.timings if obs is not None else {}),
                        "observe_ms": observe_ms,
                        "embedding_ms": embedding_ms,
                        "total_ms": elapsed,
                    },
                }
                offer_latest(results, snapshot)
    except Exception as exc:
        import traceback

        traceback.print_exc()
        offer_latest(results, {"frame": None, "fatal": f"{type(exc).__name__}: {exc}"})
