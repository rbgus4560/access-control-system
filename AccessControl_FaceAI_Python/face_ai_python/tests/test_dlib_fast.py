import unittest
from dlib_fast import restore_box


class CoordinateTests(unittest.TestCase):
    def test_inclusive_box_back_to_original(self):
        self.assertEqual(restore_box(60, 90, 179, 209, (640, 480), (480, 360)),
                         (80, 120, 240, 280))
        self.assertEqual(restore_box(0, 0, 479, 359, (641, 481), (480, 360)),
                         (0, 0, 641, 481))

    def test_clipping_and_empty_box_rejection(self):
        self.assertEqual(restore_box(-10, -2, 12, 50, (640, 480), (480, 360)),
                         (0, 0, 18, 68))
        for args in [(100, 100, 99, 200, (640, 480), (480, 360)),
                     (900, 900, 999, 999, (640, 480), (480, 360)),
                     (0, 0, 10, 10, (0, 480), (480, 360))]:
            with self.subTest(args=args), self.assertRaises(ValueError):
                restore_box(*args)

if __name__ == '__main__':
    unittest.main()
