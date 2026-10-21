-- Existing access_control_system only. Retains all existing enrollment rows.
BEGIN;
SET LOCAL lock_timeout = '5s';
ALTER TABLE access_control.face_templates
    DROP CONSTRAINT IF EXISTS face_templates_sample_no_check;
ALTER TABLE access_control.face_templates
    ADD CONSTRAINT face_templates_sample_no_check CHECK (sample_no BETWEEN 1 AND 2);
ALTER TABLE access_control.face_templates
    DROP CONSTRAINT IF EXISTS face_templates_enrollment_id_pose_key;
ALTER TABLE access_control.face_templates
    DROP CONSTRAINT IF EXISTS face_templates_enrollment_id_pose_sample_no_key;
ALTER TABLE access_control.face_templates
    ADD CONSTRAINT face_templates_enrollment_id_pose_sample_no_key
    UNIQUE (enrollment_id,pose,sample_no);
-- Also supports applying this migration directly from the legacy five-direction schema.
ALTER TABLE access_control.face_templates
    DROP CONSTRAINT IF EXISTS face_templates_pose_check;
ALTER TABLE access_control.face_templates
    ADD CONSTRAINT face_templates_pose_check CHECK (pose IN
        ('FRONT','UP','DOWN','LEFT','RIGHT','UP_LEFT','UP_RIGHT','DOWN_LEFT','DOWN_RIGHT'));
COMMIT;

SELECT conname,pg_get_constraintdef(oid)
FROM pg_constraint
WHERE conrelid='access_control.face_templates'::regclass AND contype IN ('c','u');
