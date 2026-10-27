-- 읽기 전용 확인입니다. 직원이나 얼굴 데이터를 변경하지 않습니다.
BEGIN READ ONLY;

-- 원래 사원번호가 남아 있는지 확인: 영구 삭제 후 0행이어야 합니다.
SELECT employee_no, name, is_active, access_enabled, deleted_at, purged_at
FROM access_control.persons WHERE employee_no = 'DEMO-001';

-- 최근 영구 삭제 대상: 이름/사원번호는 삭제 표식, 얼굴 특징/활성 등록은 0이어야 합니다.
SELECT p.employee_no, p.name, p.department, p.position, p.purged_at,
 (SELECT count(*) FROM access_control.face_enrollments e
   JOIN access_control.face_templates t ON t.enrollment_id=e.enrollment_id
   WHERE e.person_id=p.person_id) AS remaining_face_templates,
 (SELECT count(*) FROM access_control.face_enrollments e
   WHERE e.person_id=p.person_id AND e.is_active) AS active_enrollments,
 (SELECT count(*) FROM access_control.access_events a
   WHERE a.person_id=p.person_id) AS preserved_access_events
FROM access_control.persons p WHERE p.purged_at IS NOT NULL
ORDER BY p.purged_at DESC LIMIT 10;

COMMIT;
