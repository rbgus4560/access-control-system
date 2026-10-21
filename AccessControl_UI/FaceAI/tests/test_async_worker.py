# 파일 역할: 합성 특징·합성 MP4 또는 모델 대역으로 동작을 확인하는 개발용 회귀 검사입니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

import multiprocessing as mp
import queue
import time
import unittest
import numpy as np
from ai_worker import current_snapshot, offer_latest, run_worker


class FakeEngine:
    def __enter__(self):
        return self

    def __exit__(self, *args):
        pass

    def observe(self, frame):
        from face_engine import Observation

        time.sleep(0.08)
        if frame[0, 0, 0] == 7:
            raise RuntimeError("Simulated model error")
        reason = "MULTIPLE_FACES" if frame[0, 0, 0] == 5 else "OK"
        return Observation(
            valid=reason == "OK",
            reason=reason,
            rotation=np.eye(3),
            timings={"mesh_ms": 1.0, "dlib_ms": 2.0},
        )

    def angles(self, rotation, baseline):
        return 0.0, 0.0, 0.0

    def embedding(self, obs):
        return np.ones(512)


class WorkerTests(unittest.TestCase):
    def test_latest_frame_mailbox_is_bounded(self):
        channel = queue.Queue(maxsize=1)
        self.assertTrue(offer_latest(channel, "old"))
        self.assertTrue(offer_latest(channel, "new"))
        self.assertEqual(channel.qsize(), 1)
        self.assertEqual(channel.get_nowait(), "new")

    def test_expired_and_unacknowledged_results_hidden(self):
        sample = {"frame": np.zeros((2, 2, 3), np.uint8), "captured_at": 100, "command_id": 2}
        self.assertTrue(current_snapshot(sample, 101, 2))
        self.assertFalse(current_snapshot(sample, 103, 2))
        self.assertFalse(current_snapshot(sample, 101, 3))
        self.assertFalse(current_snapshot(sample, 99, 2))
        self.assertFalse(current_snapshot({"frame": None}, 101, 0))

    def test_spawn_commands_new_frame_multi_face_and_error(self):
        context = mp.get_context("spawn")
        frames, commands, results = context.Queue(1), context.Queue(8), context.Queue(1)
        stop = context.Event()
        process = context.Process(
            target=run_worker,
            args=(
                frames,
                commands,
                results,
                stop,
                {"pitch_sign": 1, "yaw_sign": 1, "distance_threshold": 0.45},
                FakeEngine,
            ),
        )
        process.start()

        def sample(sequence, value=0, captured_at=None):
            frame = np.full((120, 160, 3), value, np.uint8)
            frames.put(
                {
                    "sequence": sequence,
                    "captured_at": captured_at or time.monotonic(),
                    "frame": frame,
                },
                timeout=3,
            )
            item = results.get(timeout=15)
            self.assertNotIn("fatal", item)
            return item

        try:
            first = sample(1)
            self.assertEqual(first["reason"], "OK")
            created = time.monotonic()
            commands.put({"id": 1, "key": "0", "created_at": created})
            old = sample(2, captured_at=created - 0.01)
            self.assertEqual(old["command_id"], 0)
            current = sample(3)
            self.assertEqual(current["command_id"], 1)
            self.assertEqual(current["pose"], "FRONT")
            multiple = sample(4, 5)
            self.assertIn("ONE PERSON AT A TIME", multiple["status"])
            self.assertIsNone(multiple["comparison"])
            failed = sample(5, 7)
            self.assertEqual(failed["reason"], "ERROR")
            self.assertIsNone(failed["comparison"])
            commands.put({"id": 2, "key": "c", "created_at": time.monotonic()})
            cleared = sample(6)
            self.assertEqual(cleared["command_id"], 2)
            self.assertEqual(cleared["reference_count"], 0)
            self.assertFalse(cleared["matching"])
            from face_logic import POSES, PIPELINE_ID, validate_reference

            reference = {"pipeline": PIPELINE_ID, "features": {pose: [1.0] * 512 for pose in POSES}}
            commands.put(
                {
                    "id": 3,
                    "key": "load",
                    "created_at": time.monotonic(),
                    "reference": reference,
                    "label": "test reference",
                }
            )
            loaded = sample(7)
            self.assertEqual(loaded["reference_count"], 5)
            self.assertEqual(loaded["reference_label"], "test reference")
            commands.put({"id": 4, "key": "0", "created_at": time.monotonic()})
            sample(8)
            commands.put({"id": 5, "key": "t", "created_at": time.monotonic()})
            compared = sample(8)
            self.assertTrue(compared["comparison"][2])
            commands.put({"id": 6, "key": "export", "created_at": time.monotonic()})
            exported = sample(9)
            self.assertEqual(set(validate_reference(exported["export_reference"])), set(POSES))
            commands.put(
                {
                    "id": 7,
                    "key": "load",
                    "created_at": time.monotonic(),
                    "reference": {"pipeline": "wrong"},
                }
            )
            rejected = sample(10)
            self.assertIsNone(rejected["comparison"])
            self.assertFalse(rejected["matching"])
            self.assertEqual(rejected["reference_count"], 5)
        finally:
            stop.set()
            process.join(3)
            if process.is_alive():
                process.terminate()
                process.join(2)
            for channel in (frames, commands, results):
                channel.cancel_join_thread()
                channel.close()


if __name__ == "__main__":
    unittest.main()
