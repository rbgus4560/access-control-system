-- Run once in DBeaver against access_control_system before saving nine directions.
-- Extends only the original pose CHECK constraint; existing rows and history are retained.
BEGIN;
SET LOCAL lock_timeout = '5s';
ALTER TABLE access_control.face_templates
    DROP CONSTRAINT IF EXISTS face_templates_pose_check;
ALTER TABLE access_control.face_templates
    ADD CONSTRAINT face_templates_pose_check CHECK (pose IN
        ('FRONT','UP','DOWN','LEFT','RIGHT','UP_LEFT','UP_RIGHT','DOWN_LEFT','DOWN_RIGHT'));
COMMIT;

-- Verify the constraint and existing direction counts (read-only).
SELECT conname, pg_get_constraintdef(oid)
FROM pg_constraint
WHERE conrelid='access_control.face_templates'::regclass
  AND conname='face_templates_pose_check';

SELECT e.enrollment_id,e.is_active,count(t.template_id) AS directions
FROM access_control.face_enrollments e
LEFT JOIN access_control.face_templates t ON t.enrollment_id=e.enrollment_id
GROUP BY e.enrollment_id,e.is_active ORDER BY e.is_active DESC;
