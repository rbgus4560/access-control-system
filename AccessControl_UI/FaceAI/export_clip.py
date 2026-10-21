# 파일 역할: 지정된 로컬 녹화 프레임 범위를 MP4 클립으로 추출하고 길이·파일 크기·해시를 검증합니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

"""Independent OpenCV clip export; no AI models or camera are opened."""

import argparse
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import sys
import uuid


# 출력 위치와 원본 프레임 범위를 검증한 후 추출합니다. 생성된 MP4를 다시 읽어 프레임 수를 확인합니다.
def export(job, cv2):
    root = (
        Path(os.environ.get("LOCALAPPDATA", str(Path.home() / ".local/share")))
        / "AccessControlSystem"
    )
    clip = job["Clip"]
    clip_id = uuid.UUID(clip["ClipId"])
    output = Path(clip["FilePath"]).resolve()
    allowed = [
        root / "clips" / (clip_id.hex + ".mp4"),
        root / "clips" / "client_temp" / (clip_id.hex + ".mp4"),
    ]
    if output not in [p.resolve() for p in allowed] or output.exists():
        raise ValueError("Invalid or existing output")
    parts = job["Parts"]
    if (
        not 1 <= len(parts) <= 64
        or job["Fps"] != 10
        or not 2 <= job["Width"] <= 1280
        or not 2 <= job["Height"] <= 1280
    ):
        raise ValueError("Invalid clip plan")
    requested = (
        datetime.fromisoformat(clip["EndTime"].replace("Z", "+00:00"))
        - datetime.fromisoformat(clip["StartTime"].replace("Z", "+00:00"))
    ).total_seconds()
    total = sum(p["FrameCount"] for p in parts)
    if not 0 < requested <= 300 or not 1 <= total <= 3001 or abs(total / 10 - requested) > 1:
        raise ValueError("Invalid clip duration")
    sources = []
    for part in parts:
        path = Path(part["FilePath"]).resolve()
        if (
            path.parent.parent != (root / "recordings" / "CAM-01").resolve()
            or path.suffix != ".mp4"
        ):
            raise ValueError("Source outside recording folder")
        uuid.UUID(hex=path.stem)
        if not 0 <= part["StartFrame"] < 600 or not 1 <= part["FrameCount"] <= 600:
            raise ValueError("Invalid source frame range")
        sources.append(path)
    output.parent.mkdir(parents=True, exist_ok=True)
    writer = cv2.VideoWriter(
        str(output), cv2.VideoWriter_fourcc(*"mp4v"), 10, (job["Width"], job["Height"])
    )
    if not writer.isOpened():
        writer.release()
        raise RuntimeError("MP4 encoder could not open")
    count = 0
    try:
        for part, path in zip(parts, sources):
            video = cv2.VideoCapture(str(path))
            try:
                if not video.isOpened():
                    raise RuntimeError("Source could not open")
                # Sequential discard avoids backend keyframe-seek approximations.
                for _ in range(part["StartFrame"]):
                    if not video.grab():
                        raise RuntimeError("Source seek failed")
                for _ in range(part["FrameCount"]):
                    ok, frame = video.read()
                    if not ok or frame.shape[:2] != (job["Height"], job["Width"]):
                        raise RuntimeError("Source ended or dimensions changed")
                    writer.write(frame)
                    count += 1
            finally:
                video.release()
    finally:
        writer.release()
    verify = cv2.VideoCapture(str(output))
    try:
        ok, _ = verify.read()
        if (
            not verify.isOpened()
            or not ok
            or int(verify.get(cv2.CAP_PROP_FRAME_COUNT)) != count
            or count != total
        ):
            raise RuntimeError("Exported clip verification failed")
    finally:
        verify.release()
    result = dict(clip)
    result.update(
        Status="READY",
        FileSizeBytes=output.stat().st_size,
        FrameCount=count,
        DurationSeconds=count / 10,
    )
    with output.open("rb") as stream:
        result["FileHash"] = hashlib.file_digest(stream, "sha256").hexdigest()
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--job", required=True)
    args = parser.parse_args()
    try:
        with open(args.job, encoding="utf-8-sig") as stream:
            job = json.load(stream)
        import cv2

        cv2.setNumThreads(2)
        result = export(job, cv2)
        print(json.dumps(result, ensure_ascii=True, allow_nan=False), flush=True)
        return 0
    except Exception as exc:
        print(type(exc).__name__ + ": " + str(exc), file=sys.stderr, flush=True)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
