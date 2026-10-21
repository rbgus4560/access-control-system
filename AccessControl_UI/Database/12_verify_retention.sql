-- Read-only: existing schema, no CREATE/ALTER/DELETE.
SELECT status, count(*) AS segments,
       count(*) FILTER (WHERE status='READY' AND end_time <= now()-interval '72 hours') AS expired_ready,
       count(*) FILTER (WHERE status='DELETED' AND deleted_at IS NOT NULL
                        AND deletion_reason='RETENTION_72H') AS retention_deleted
FROM access_control.recording_segments
GROUP BY status ORDER BY status;

SELECT segment_id, start_time, end_time, status, file_size_bytes,
       deleted_at, deletion_reason, file_path
FROM access_control.recording_segments
WHERE status='DELETED'
ORDER BY deleted_at DESC LIMIT 50;
