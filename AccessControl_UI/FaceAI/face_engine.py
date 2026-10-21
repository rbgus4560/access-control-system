# 파일 역할: dlib 얼굴 검출·눈 정렬, MediaPipe 자세 추정, ArcFace 특징 추출을 순서대로 수행합니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

"""dlib detection/alignment, MediaPipe mesh, DeepFace ArcFace inference."""

import os
from pathlib import Path
from dataclasses import dataclass, field
import time
import hashlib
import numpy as np
from face_logic import unit_embedding

ROOT = Path(__file__).resolve().parent
os.environ.setdefault("TF_USE_LEGACY_KERAS", "1")
os.environ.setdefault("TF_CPP_MIN_LOG_LEVEL", "2")
os.environ.setdefault("CUDA_VISIBLE_DEVICES", "-1")
os.environ.setdefault("TF_NUM_INTRAOP_THREADS", "2")
os.environ.setdefault("TF_NUM_INTEROP_THREADS", "1")
os.environ.setdefault("DEEPFACE_HOME", str(ROOT / "model_cache"))


@dataclass
class Observation:
    valid: bool = False
    reason: str = "NO_FACE"
    mesh: object = None
    rotation: object = None
    face: object = None
    box: object = None
    blur: float = 0.0
    faces: int = 0
    meshes: list = field(default_factory=list)
    timings: dict = field(default_factory=dict)


class FaceEngine:
    def __init__(self, detector_mode="fast", detector_width=480):
        if detector_mode not in ("fast", "standard"):
            raise ValueError("Unknown detector mode")
        print("[startup 6.2] Importing OpenCV", flush=True)
        import cv2

        print("[startup 6.2] Importing MediaPipe", flush=True)
        import mediapipe as mp

        print("[startup 6.2] Importing DeepFace / TensorFlow", flush=True)
        from deepface import DeepFace

        print("[startup 6.2] AI imports complete; checking bundled models", flush=True)
        self.cv2, self.mp, self.df = cv2, mp, DeepFace
        cv2.setNumThreads(2)
        model = ROOT / "models" / "face_landmarker.task"
        manifest = (ROOT / "models" / "face_landmarker.sha256").read_text().strip().split()[0]
        if hashlib.sha256(model.read_bytes()).hexdigest() != manifest:
            raise RuntimeError("MediaPipe model hash mismatch")
        # v0.0.93 otherwise downloads this file over HTTP. Use the bundled,
        # hash-checked HTTPS download instead, without touching other environments.
        import shutil

        predictor = ROOT / "models" / "shape_predictor_5_face_landmarks.dat"
        predictor_hash = (
            (ROOT / "models" / "shape_predictor_5_face_landmarks.sha256").read_text().split()[0]
        )
        if hashlib.sha256(predictor.read_bytes()).hexdigest() != predictor_hash:
            raise RuntimeError("dlib landmark model hash mismatch")
        weights = Path(os.environ["DEEPFACE_HOME"]) / ".deepface" / "weights"
        weights.mkdir(parents=True, exist_ok=True)
        target = weights / predictor.name
        if not target.exists() or hashlib.sha256(target.read_bytes()).hexdigest() != predictor_hash:
            shutil.copyfile(predictor, target)
        print("Loading dlib detector and five-point eye alignment model...", flush=True)
        detector = self.df.build_model(model_name="dlib", task="face_detector")
        from dlib_fast import DlibFastFaces

        self.fast_faces = DlibFastFaces(detector.model, max_width=detector_width)
        self.detector_mode = detector_mode
        print("Loading ArcFace (first use downloads weights)...", flush=True)
        self.df.build_model(model_name="ArcFace", task="facial_recognition")
        options = mp.tasks.vision.FaceLandmarkerOptions(
            base_options=mp.tasks.BaseOptions(model_asset_path=str(model)),
            running_mode=mp.tasks.vision.RunningMode.IMAGE,
            num_faces=2,
            output_facial_transformation_matrixes=True,
        )
        self.mesh_model = mp.tasks.vision.FaceLandmarker.create_from_options(options)

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.mesh_model.close()

    # 한 얼굴·한 메시인지 확인한 뒤 위치·회전·정렬된 얼굴을 반환합니다. 품질 조건을 통과해야 특징을 추출합니다.
    def observe(self, bgr):
        if bgr is None or bgr.ndim != 3 or bgr.shape[2] != 3:
            raise ValueError("BGR frame required")
        started = time.perf_counter()
        rgb = self.cv2.cvtColor(bgr, self.cv2.COLOR_BGR2RGB)
        result = self.mesh_model.detect(
            self.mp.Image(image_format=self.mp.ImageFormat.SRGB, data=np.ascontiguousarray(rgb))
        )
        obs = Observation(faces=len(result.face_landmarks))
        obs.meshes = result.face_landmarks
        obs.timings["mesh_ms"] = (time.perf_counter() - started) * 1000
        if obs.faces != 1:
            obs.reason = "NO_MESH" if obs.faces == 0 else "MULTIPLE_FACES"
            return obs
        obs.mesh = result.face_landmarks[0]
        started = time.perf_counter()
        try:
            if self.detector_mode == "fast":
                faces = self.fast_faces.extract(bgr)
                obs.timings.update(self.fast_faces.timings)
            else:
                faces = self.df.extract_faces(
                    img_path=bgr,
                    detector_backend="dlib",
                    enforce_detection=True,
                    align=True,
                    color_face="bgr",
                    normalize_face=False,
                )
        except ValueError as exc:
            # Only expected no-detection errors are treated as absent face.
            if "Face could not be detected" not in str(exc):
                raise
            obs.reason = "DLIB_NO_FACE"
            return obs
        finally:
            obs.timings["dlib_ms"] = (time.perf_counter() - started) * 1000
        if len(faces) != 1:
            obs.reason = "DLIB_NO_FACE" if not faces else "MULTIPLE_DLIB_FACES"
            return obs
        item = faces[0]
        region = item["facial_area"]
        x, y, w, h = [int(region[k]) for k in ("x", "y", "w", "h")]
        obs.box = (x, y, w, h)
        if min(w, h) < 100:
            obs.reason = "MOVE_CLOSER"
            return obs
        # A single mesh must belong to the detected face, rather than a different person.
        height, width = bgr.shape[:2]
        nose = obs.mesh[1]
        if not (x <= nose.x * width <= x + w and y <= nose.y * height <= y + h):
            obs.reason = "MESH_FACE_MISMATCH"
            return obs
        if len(result.facial_transformation_matrixes) != 1:
            obs.reason = "NO_POSE_MATRIX"
            return obs
        matrix = np.asarray(result.facial_transformation_matrixes[0], dtype=float)
        if matrix.shape != (4, 4) or not np.isfinite(matrix).all():
            obs.reason = "INVALID_POSE"
            return obs
        # Remove residual scale through the closest proper orthogonal rotation.
        u, _, vt = np.linalg.svd(matrix[:3, :3])
        correction = np.eye(3)
        correction[2, 2] = np.linalg.det(u @ vt)
        obs.rotation = u @ correction @ vt
        face = np.asarray(item["face"])
        if face.dtype != np.uint8 or not face.size:
            raise ValueError("Expected unnormalized uint8 BGR crop from DeepFace")
        obs.blur = float(
            self.cv2.Laplacian(
                self.cv2.cvtColor(face, self.cv2.COLOR_BGR2GRAY), self.cv2.CV_64F
            ).var()
        )
        if obs.blur < 35:
            obs.reason = "BLUR_OR_LOW_DETAIL"
            return obs
        if region.get("left_eye") is None or region.get("right_eye") is None:
            obs.reason = "NO_EYE_ALIGNMENT"
            return obs
        obs.face = face.copy()
        obs.valid, obs.reason = True, "OK"
        return obs

    def angles(self, rotation, baseline):
        relative = rotation @ baseline.T
        return tuple(float(x) for x in self.cv2.RQDecomp3x3(relative)[0])

    # 검출·눈 정렬된 얼굴을 ArcFace에 한 번 전달해 정규화된 512차원 특징을 만듭니다.
    def embedding(self, obs):
        if not obs.valid or obs.face is None:
            raise ValueError("Valid single face required")
        result = self.df.represent(
            img_path=obs.face,
            model_name="ArcFace",
            detector_backend="skip",
            enforce_detection=True,
            align=False,
            normalization="base",
        )
        if len(result) != 1:
            raise ValueError("Exactly one embedding required")
        return unit_embedding(result[0]["embedding"])
