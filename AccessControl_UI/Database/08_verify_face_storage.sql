-- 읽기 전용 검증. 현재 연결 DB가 access_control_system인지 먼저 확인한다.
SELECT current_database();

-- 직원별 등록 이력, 활성 여부 및 방향별 특징 개수. 특징 원문은 조회하지 않는다.
SELECT p.employee_no, p.name, e.enrollment_id, e.is_active,
       e.model_version, e.embedding_dimension, e.created_at,
       count(t.template_id) AS template_count,
       array_agg(t.pose ORDER BY t.pose) FILTER (WHERE t.pose IS NOT NULL) AS poses,
       min(octet_length(t.embedding_encrypted)) AS smallest_ciphertext_bytes
FROM access_control.persons p
JOIN access_control.face_enrollments e ON e.person_id=p.person_id
LEFT JOIN access_control.face_templates t ON t.enrollment_id=e.enrollment_id
GROUP BY p.employee_no,p.name,e.enrollment_id,e.is_active,e.model_version,e.embedding_dimension,e.created_at
ORDER BY p.employee_no,e.created_at DESC;

-- 정상 등록은 활성 묶음 1개와 방향 특징 5개다. 아래 결과는 0행이어야 한다.
SELECT e.enrollment_id,count(t.template_id) AS template_count
FROM access_control.face_enrollments e
LEFT JOIN access_control.face_templates t ON t.enrollment_id=e.enrollment_id
WHERE e.is_active
GROUP BY e.enrollment_id HAVING count(t.template_id)<>5;
