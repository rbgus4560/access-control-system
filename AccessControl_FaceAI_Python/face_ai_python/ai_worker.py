"""Single-owner AI process. Only images/commands enter; snapshots leave."""
import time
import queue
import numpy as np
from face_logic import Enrollment, classify_pose, cosine_distance

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
    return (snapshot is not None and snapshot.get('frame') is not None
            and snapshot.get('command_id', 0) >= required_command
            and 0 <= now - snapshot['captured_at'] <= RESULT_MAX_AGE)


def annotate(frame, obs):
    import cv2
    view = frame.copy()
    height, width = view.shape[:2]
    meshes = getattr(obs, 'meshes', [])
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
        cv2.rectangle(view, (x, y), (x + w, y + h),
                      (0, 255, 0) if obs.valid else (0, 160, 255), 2)
    return view


def run_worker(frames, commands, results, stop, config, engine_factory=None):
    """Model/session and registration state are owned exclusively by this process."""
    enrollment = Enrollment()
    baseline = None
    matching = False
    stable_pose, stable_count, stable_since = None, 0, 0.0
    last_sample_time = None
    command_id = 0
    pending_commands = []
    message = 'Look FRONT, press 0 to calibrate; E starts enrollment'
    try:
        if engine_factory is None:
            from face_engine import FaceEngine
            engine_factory = lambda: FaceEngine(detector_mode=config.get('detector_mode', 'fast'),
                                                detector_width=config.get('detector_width', 480))
        with engine_factory() as engine:
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
                captured_at, frame = packet['captured_at'], packet['frame']
                # Frames waiting too long are discarded before any identity processing.
                if time.monotonic() - captured_at > RESULT_MAX_AGE:
                    continue
                obs = None
                comparison = None
                embedding_ms = None
                angles = (0.0, 0.0, 0.0)
                pose = 'UNKNOWN'
                started = time.perf_counter()
                observe_ms = None
                try:
                    observed_start = time.perf_counter()
                    obs = engine.observe(frame)
                    observe_ms = (time.perf_counter() - observed_start) * 1000
                    if obs.valid and baseline is not None:
                        angles = engine.angles(obs.rotation, baseline)
                        pose = classify_pose(*angles, pitch_sign=config['pitch_sign'], yaw_sign=config['yaw_sign'])
                    valid = obs.valid
                    # Count independent analyzed samples, never UI redraws of one result.
                    if (valid and pose != 'UNKNOWN' and pose == stable_pose
                            and last_sample_time is not None and captured_at - last_sample_time <= 2.5):
                        stable_count += 1
                    elif valid and pose != 'UNKNOWN':
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
                        if captured_at < command['created_at']:
                            deferred.append(command)
                            continue
                        command_id = max(command_id, command['id'])
                        key = command['key']
                        age = time.monotonic() - command['created_at']
                        # Clear/cancel/toggle are always honored; stale capture must not execute.
                        if key in ('0', 'space') and age > COMMAND_MAX_AGE:
                            message = 'Command expired. Hold your pose and press the key again.'
                            continue
                        if key == '0':
                            if valid:
                                baseline = obs.rotation.copy()
                                enrollment.cancel()
                                matching = False
                                stable_pose, stable_count = None, 0
                                stable = False
                                pose = 'FRONT'
                                angles = (0.0, 0.0, 0.0)
                                message = 'FRONT calibrated. Check directions, then E to enroll.'
                            else:
                                message = 'Calibration rejected: one clear face required'
                        elif key == 'e':
                            if baseline is None:
                                message = 'First look FRONT and press 0'
                            else:
                                enrollment.start()
                                matching = False
                                stable_pose, stable_count, stable_since = None, 0, captured_at
                                stable = False
                                message = 'Enrollment: FRONT UP DOWN LEFT RIGHT; hold then SPACE'
                        elif key == 'space':
                            if enrollment.pending is None:
                                message = 'Press E to start enrollment'
                            elif not stable or pose != enrollment.expected:
                                message = f'Capture rejected. Hold {enrollment.expected}, then SPACE'
                            else:
                                feature_started = time.perf_counter()
                                feature = engine.embedding(obs)
                                embedding_ms = (time.perf_counter() - feature_started) * 1000
                                front = enrollment.pending.get('FRONT')
                                if front is not None and cosine_distance(front, feature) > 0.60:
                                    message = 'Capture rejected: differs from FRONT; improve pose/light'
                                else:
                                    done = enrollment.capture(pose, feature)
                                    message = 'Five directions ready. T starts comparison.' if done else f'Saved {pose}; next {enrollment.expected}'
                        elif key == 't':
                            if len(enrollment.active) != 5 or enrollment.pending is not None:
                                message = 'Complete all five directions first'
                            else:
                                matching = not matching
                                message = 'Comparison ON (identity candidate only)' if matching else 'Comparison OFF'
                        elif key == 'c':
                            enrollment = Enrollment()
                            baseline, matching = None, False
                            stable_pose, stable_count = None, 0
                            stable = False
                            pose, angles = 'UNKNOWN', (0.0, 0.0, 0.0)
                            message = 'All references cleared. Look FRONT and press 0.'
                    pending_commands = deferred
                    if matching and enrollment.pending is None and valid and pose != 'UNKNOWN':
                        feature_started = time.perf_counter()
                        feature = engine.embedding(obs)
                        embedding_ms = (time.perf_counter() - feature_started) * 1000
                        comparison = enrollment.compare(feature, config['distance_threshold'])
                except Exception as exc:
                    # Every failed inference invalidates previous scores; no stale fallback.
                    comparison = None
                    stable_pose, stable_count = None, 0
                    message = 'AI ERROR: see terminal'
                    print(type(exc).__name__, str(exc), flush=True)
                    # Do not retry a capture command automatically after an exception.
                    for command in pending_commands:
                        command_id = max(command_id, command['id'])
                    pending_commands = []
                elapsed = (time.perf_counter() - started) * 1000
                reason = obs.reason if obs is not None else 'ERROR'
                if reason in ('MULTIPLE_FACES', 'MULTIPLE_DLIB_FACES'):
                    status = 'MULTIPLE FACES - ONE PERSON AT A TIME (comparison paused)'
                    comparison = None
                elif reason != 'OK':
                    status = f'Face: {reason} - comparison paused'
                    comparison = None
                else:
                    status = f'Face: OK | Pose: {pose} | stable samples: {stable_count}'
                snapshot = {
                    'sequence': packet['sequence'], 'captured_at': captured_at,
                    'finished_at': time.monotonic(), 'command_id': command_id,
                    'frame': annotate(frame, obs) if obs is not None else frame.copy(),
                    'status': status, 'reason': reason, 'pose': pose, 'angles': angles,
                    'stable': stable_count, 'reference_count': len(enrollment.active),
                    'expected': enrollment.expected, 'matching': matching,
                    'comparison': comparison, 'message': message,
                    'timings': {**(obs.timings if obs is not None else {}),
                                'observe_ms': observe_ms, 'embedding_ms': embedding_ms, 'total_ms': elapsed},
                }
                offer_latest(results, snapshot)
    except Exception as exc:
        import traceback
        traceback.print_exc()
        offer_latest(results, {'frame': None, 'fatal': f'{type(exc).__name__}: {exc}'})
