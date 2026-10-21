-- 읽기 전용. 기존 DDL 재실행 불필요.
SELECT x.clip_id,c.camera_code,a.login_id AS exported_by,x.event_id,
       x.start_time,x.end_time,x.status,x.file_size_bytes,x.file_hash,
       x.file_path,x.completed_at
FROM access_control.exported_clips x
JOIN access_control.cameras c ON c.camera_id=x.camera_id
JOIN access_control.admin_accounts a ON a.admin_id=x.exported_by
ORDER BY x.created_at DESC
LIMIT 100;
