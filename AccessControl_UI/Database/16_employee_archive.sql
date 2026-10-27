-- 직원 삭제 표시를 추가합니다. 출입 이력과 직원 행은 보존합니다.
-- 기존 DB를 백업하고 관제 서버의 대상 DB인지 확인한 후 한 번 적용합니다.
BEGIN;
ALTER TABLE access_control.persons ADD COLUMN IF NOT EXISTS deleted_at timestamptz;
COMMIT;
