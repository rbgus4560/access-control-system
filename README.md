# 얼굴 인식 출입 통제 시스템

Python 얼굴 AI 검증 도구와 C# WPF 기반 출입 통제 통합 프로젝트를 함께 보관한다. 사전학습 dlib·MediaPipe·ArcFace를 UI·PostgreSQL·통신·녹화·재생에 연결하는 개발 포트폴리오다.

| 경로 | 역할 |
|---|---|
| AccessControl_UI | .NET 10 WPF 서버·얼굴 등록·인증 클라이언트와 통합 FaceAI |
| AccessControl_FaceAI_Python | 5방향 등록·동일인 비교를 독립 실행하는 초기 검증 도구 |

## 문서

- [전체 개발 과정](AccessControl_UI/docs/development-history.md)
- [개발 과정과 트러블슈팅 Word 보고서](AccessControl_UI/docs/AccessControl_개발과정_및_리팩토링_보고서.docx)
- [시스템 구성과 핵심 기능](AccessControl_UI/docs/project-summary.md)
- [트러블슈팅 전체 기록](AccessControl_UI/docs/troubleshooting.md)
- [2026년 10월 21일 리팩토링과 파일 정리](AccessControl_UI/docs/refactor-20261021.md)

## 시작

통합 앱은 AccessControl_UI 안의 Start_Server.cmd와 Start_Client.cmd를 사용한다. 최초 의존성 복원과 빌드를 수행한다. 기존 PostgreSQL과 대상 Python 3.11 access_face_ai 환경, 모델 준비가 필요하며 DB 초기 생성·사용자 계정·설정은 별도로 확인한다.

최신 12.3은 전체 직원 기준을 자동 동기화한다. [최신 안내](AccessControl_UI/AUTO_IDENTIFICATION_12_3_KO.md)를 우선하고 [기존 LAN 배치 문서](AccessControl_UI/DEPLOY_TWO_PC.md)의 수동 1:1 선택 부분은 과거 설명으로 읽는다. 독립 도구 사용법은 [Python README](AccessControl_FaceAI_Python/face_ai_python/README.md)를 참고한다.

## 이번 검증

Python 3.11에서 통합본 49개와 독립 도구 9개 검사, 전후 수치 비교, C# 갤러리 동기화·정책 검사와 WPF 빌드를 통과했다. 실제 카메라·운영 DB·학교 LAN·인식 정확도를 새로 검증한 결과는 아니다. 모델 임계값과 인원 상한은 성능 보장값이 아니다.

모델 캐시, 로컬 인증 설정, 운영 DB와 영상 원본 및 빌드 결과는 포함하지 않는다. 배포용 models 파일은 기존 출처·해시 안내와 함께 보존하며, ArcFace 캐시는 실행 환경에서 준비한다. 학습 모델과 각 라이브러리는 원래의 라이선스를 따른다. 생체 위조 방지와 실제 문 구동 장치 연동은 확인된 구현 범위가 아니다.
