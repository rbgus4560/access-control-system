-- 명시적 직원 정보 영구 삭제 상태. 이 SQL 자체는 데이터를 삭제하지 않습니다.
BEGIN;
ALTER TABLE access_control.persons ADD COLUMN IF NOT EXISTS deleted_at timestamptz;
ALTER TABLE access_control.persons ADD COLUMN IF NOT EXISTS delete_after timestamptz;
ALTER TABLE access_control.persons ADD COLUMN IF NOT EXISTS purged_at timestamptz;
COMMIT;
