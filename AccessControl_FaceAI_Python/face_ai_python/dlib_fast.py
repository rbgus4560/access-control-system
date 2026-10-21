"""Small-image HOG detection; original-image landmarks and DeepFace alignment."""
import math
import time
import numpy as np


def restore_box(left, top, right, bottom, source_size, detection_size):
    """Inclusive detector rectangle -> clamped half-open original-image box."""
    sw, sh = source_size
    dw, dh = detection_size
    if min(sw, sh, dw, dh) <= 0:
        raise ValueError('Positive image dimensions required')
    if right < left or bottom < top:
        raise ValueError('Invalid detection rectangle')
    x1 = max(0, min(sw, math.floor(left * sw / dw)))
    y1 = max(0, min(sh, math.floor(top * sh / dh)))
    x2 = max(0, min(sw, math.ceil((right + 1) * sw / dw)))
    y2 = max(0, min(sh, math.ceil((bottom + 1) * sh / dh)))
    if x2 <= x1 or y2 <= y1:
        raise ValueError('Empty detection rectangle')
    return x1, y1, x2, y2


class DlibFastFaces:
    def __init__(self, detector_model, max_width=480):
        if not 320 <= max_width <= 1280:
            raise ValueError('detector-width must be between 320 and 1280')
        self.detector = detector_model['face_detector']
        self.predictor = detector_model['sp']
        self.max_width = max_width
        self.timings = {}

    def extract(self, bgr):
        import cv2
        import dlib
        from deepface.modules.detection import align_img_wrt_eyes, project_facial_area
        self.timings = {}
        started = time.perf_counter()
        height, width = bgr.shape[:2]
        dw = min(width, self.max_width)
        dh = max(1, round(height * dw / width))
        small = cv2.resize(bgr, (dw, dh), interpolation=cv2.INTER_AREA) if dw < width else bgr
        gray = cv2.cvtColor(small, cv2.COLOR_BGR2GRAY)
        detections, _, _ = self.detector.run(np.ascontiguousarray(gray), 0)
        self.timings = {'detect_ms': (time.perf_counter() - started) * 1000,
                        'detector_input_width': dw}
        if not detections:
            return []
        boxes = [restore_box(r.left(), r.top(), r.right(), r.bottom(),
                             (width, height), (dw, dh)) for r in detections]
        if len(boxes) != 1:
            # Reject multiple detections without extracting any identity feature.
            return [{'facial_area': {'x': x1, 'y': y1, 'w': x2-x1, 'h': y2-y1}}
                    for x1, y1, x2, y2 in boxes]
        started = time.perf_counter()
        x1, y1, x2, y2 = boxes[0]
        shape = self.predictor(np.ascontiguousarray(bgr), dlib.rectangle(x1, y1, x2 - 1, y2 - 1))
        # Subject-left eye is on the observer's right, matching DeepFace's dlib backend.
        left_eye = tuple(int((getattr(shape.part(0), axis) + getattr(shape.part(1), axis)) // 2) for axis in ('x', 'y'))
        right_eye = tuple(int((getattr(shape.part(2), axis) + getattr(shape.part(3), axis)) // 2) for axis in ('x', 'y'))
        hb, wb = height // 2, width // 2
        # Padding is only for rotation; HOG never scans the padded image.
        padded = cv2.copyMakeBorder(bgr, hb, hb, wb, wb, cv2.BORDER_CONSTANT, value=(0, 0, 0))
        shifted_left = (left_eye[0] + wb, left_eye[1] + hb)
        shifted_right = (right_eye[0] + wb, right_eye[1] + hb)
        aligned, angle = align_img_wrt_eyes(img=padded, left_eye=shifted_left, right_eye=shifted_right)
        ax1, ay1, ax2, ay2 = project_facial_area(
            facial_area=(x1 + wb, y1 + hb, x2 + wb, y2 + hb),
            angle=angle, size=padded.shape[:2])
        crop = np.ascontiguousarray(aligned[int(ay1):int(ay2), int(ax1):int(ax2)])
        if not crop.size:
            raise ValueError('Aligned face is empty')
        self.timings['align_ms'] = (time.perf_counter() - started) * 1000
        return [{'face': crop, 'facial_area': {'x': x1, 'y': y1, 'w': x2 - x1, 'h': y2 - y1,
                                              'left_eye': left_eye, 'right_eye': right_eye}}]
