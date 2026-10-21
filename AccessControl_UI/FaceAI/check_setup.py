# 파일 역할: 가상환경의 라이브러리와 필수 API·모델 준비 상태를 확인하는 독립 점검 스크립트입니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

"""Check imports, exact API and optional first-time model downloads."""

import argparse
import importlib.metadata as md
import os
import sys
from pathlib import Path

os.environ.setdefault("TF_USE_LEGACY_KERAS", "1")
os.environ.setdefault("TF_CPP_MIN_LOG_LEVEL", "2")
os.environ.setdefault("CUDA_VISIBLE_DEVICES", "-1")
os.environ.setdefault("DEEPFACE_HOME", str(Path(__file__).resolve().parent / "model_cache"))


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--prepare", action="store_true")
    args = p.parse_args()
    print("Python:", sys.version, flush=True)
    if sys.version_info[:2] != (3, 11):
        print("Use the separate Python 3.11 access_face_ai environment.")
        return 1
    for name in [
        "numpy",
        "dlib",
        "opencv-python",
        "opencv-contrib-python",
        "deepface",
        "tensorflow",
        "tf-keras",
        "mediapipe",
        "jax",
        "jaxlib",
    ]:
        try:
            version = md.version(name)
        except md.PackageNotFoundError:
            if name != "dlib":
                raise
            import dlib

            version = dlib.__version__
        print(name, version, flush=True)
    import cv2
    import dlib
    import mediapipe as mp
    from deepface import DeepFace
    import inspect

    required = {"color_face", "normalize_face"}
    assert required.issubset(inspect.signature(DeepFace.extract_faces).parameters)
    assert "task" in inspect.signature(DeepFace.build_model).parameters
    dlib.get_frontal_face_detector()
    print("Imports and required APIs: PASS", flush=True)
    if args.prepare:
        from face_engine import FaceEngine

        with FaceEngine() as engine:
            import numpy as np

            result = engine.observe(np.zeros((480, 640, 3), dtype=np.uint8))
            assert not result.valid
            print("Models loaded; blank frame rejected: PASS", flush=True)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception:
        import traceback

        traceback.print_exc()
        raise SystemExit(1)
