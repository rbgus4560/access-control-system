BEGIN;
SET LOCAL lock_timeout='5s';
ALTER TABLE access_control.recording_segments ADD COLUMN IF NOT EXISTS client_metadata JSONB;
COMMIT;
-- Client original file keys use client://CAM-01/<segment UUID>; they are not server paths.
SELECT column_name,data_type FROM information_schema.columns
WHERE table_schema='access_control' AND table_name='recording_segments' AND column_name='client_metadata';
