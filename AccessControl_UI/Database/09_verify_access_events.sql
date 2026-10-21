-- 읽기 전용. 특징 원문이나 관리자 비밀번호를 조회하지 않는다.
SELECT current_database();
SELECT e.event_id,e.event_time,e.received_at,c.camera_code,p.employee_no,p.name,
       e.result,e.reason_code,e.similarity AS cosine_similarity,
       1-e.similarity AS cosine_distance,e.threshold AS similarity_threshold,
       1-e.threshold AS distance_threshold
FROM access_control.access_events e
JOIN access_control.cameras c ON c.camera_id=e.camera_id
LEFT JOIN access_control.persons p ON p.person_id=e.person_id
ORDER BY e.event_time DESC,e.event_id LIMIT 100;

-- 신원 연결이 있는 허가·거부와 미등록/불일치 기록의 구분.
SELECT result,reason_code,count(*) AS event_count
FROM access_control.access_events GROUP BY result,reason_code ORDER BY result,reason_code;
