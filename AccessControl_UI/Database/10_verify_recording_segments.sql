-- 읽기 전용: 기존 DDL 재실행이나 DB 초기화 불필요.
SELECT r.segment_id,c.camera_code,r.start_time,r.end_time,
       EXTRACT(EPOCH FROM (r.end_time-r.start_time)) AS duration_seconds,
       r.status,r.file_size_bytes,r.file_path
FROM access_control.recording_segments r
JOIN access_control.cameras c ON c.camera_id=r.camera_id
ORDER BY r.start_time DESC
LIMIT 100;

SELECT status,COUNT(*) AS segment_count,SUM(file_size_bytes) AS total_bytes
FROM access_control.recording_segments
GROUP BY status
ORDER BY status;
