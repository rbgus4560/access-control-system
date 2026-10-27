-- 복원 가능한 삭제 대기 상태를 위한 필드만 추가합니다.
-- 이 SQL은 직원 개인정보나 얼굴 특징을 삭제하지 않습니다.
BEGIN;
ALTER TABLE access_control.persons ADD COLUMN IF NOT EXISTS deleted_at timestamptz;
ALTER TABLE access_control.persons ADD COLUMN IF NOT EXISTS delete_after timestamptz;
COMMIT;
