# 파일 역할: 합성 특징·합성 MP4 또는 모델 대역으로 동작을 확인하는 개발용 회귀 검사입니다.
# 기능 기준: 12.3. 실행 로직을 유지하면서 설명과 서식을 정리했습니다.

import io
import threading
import unittest
from startup_diagnostics import worker_stack, watch_startup


class StartupDiagnosticsTests(unittest.TestCase):
    def test_import_watchdog_reports_owner_and_stops_on_exit(self):
        class Output(io.StringIO):
            def __init__(self):
                super().__init__()
                self.reported = threading.Event()

            def write(self, text):
                result = super().write(text)
                if "End of AI worker stack" in text:
                    self.reported.set()
                return result

        output = Output()
        with watch_startup("simulated native import", interval=0.01, stream=output):
            self.assertTrue(output.reported.wait(2))
        report = output.getvalue()
        self.assertIn("simulated native import", report)
        self.assertIn("test_import_watchdog_reports_owner", report)
        self.assertFalse(any(t.name == "StartupDiagnostic" for t in threading.enumerate()))

    def test_import_watchdog_cleans_up_when_import_fails(self):
        with self.assertRaises(ImportError):
            with watch_startup("failed import", stream=io.StringIO()):
                raise ImportError("simulated missing library")
        self.assertFalse(any(t.name == "StartupDiagnostic" for t in threading.enumerate()))

    def test_waiting_worker_reports_location_without_local_values(self):
        stop = threading.Event()
        entered = threading.Event()

        def blocked_model_import():
            private_feature = "PRIVATE_FEATURE_VALUE_MUST_NOT_BE_LOGGED"
            entered.set()
            stop.wait()
            return private_feature

        worker = threading.Thread(target=blocked_model_import)
        worker.start()
        try:
            self.assertTrue(entered.wait(2))
            output = io.StringIO()
            self.assertTrue(worker_stack(worker, 45, output))
            report = output.getvalue()
            self.assertIn("blocked_model_import", report)
            self.assertIn("stop.wait()", report)
            self.assertIn("AI waiting 45s", report)
            self.assertNotIn("PRIVATE_FEATURE_VALUE_MUST_NOT_BE_LOGGED", report)
        finally:
            stop.set()
            worker.join(2)

    def test_finished_worker_has_no_stale_stack(self):
        worker = threading.Thread(target=lambda: None)
        worker.start()
        worker.join(2)
        output = io.StringIO()
        self.assertFalse(worker_stack(worker, 45, output))
        self.assertIn("No in-process worker frame", output.getvalue())


if __name__ == "__main__":
    unittest.main()
