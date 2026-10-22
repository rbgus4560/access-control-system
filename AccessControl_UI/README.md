# 얼굴 인식 출입 통제 통합 프로젝트

.NET 10 WPF 관리 서버·얼굴 등록 앱·인증 클라이언트와 Python 얼굴 AI를 연결한다. 현재 기준은 12.4 연결·화면 개선과 12.3 전체 직원 자동 식별이다.

## 실행

- 서버 PC: `Start_Server.cmd`로 빌드하고 서버를 실행한다.
- 카메라 클라이언트 PC: `Start_Client.cmd`로 빌드하고 인증 앱을 실행한다.
- PostgreSQL과 Python 3.11 환경 및 모델 준비가 필요하다. 기존 DB·AppData·모델 캐시는 보존한다.
- [최신 자동 식별 안내](docs/guides/AUTO_IDENTIFICATION_12_3_KO.md)를 우선 읽는다. 과거 1:1 직원 선택 절차와 구분한다.

## 문서

- [실행·검사 스크립트 안내](docs/guides/COMMANDS.md)
- [전체 문서 목차](docs/README.md)
- [시스템 구성과 핵심 기능](docs/project-summary.md)
- [전체 개발 과정](docs/development-history.md)
- [트러블슈팅](docs/troubleshooting.md)
- [개발 과정 Word 보고서](docs/AccessControl_개발과정_및_리팩토링_보고서.docx)
- [변경 기록](CHANGELOG.md)

단계별 검증은 `docs/validation`, 버전 이력은 `docs/history`, 사용 안내는 `docs/guides`, 장애 수정 원문은 `docs/fixes`에 보관한다. 원문 기록의 PASS를 최신 버전의 전체 현장 검증으로 확대하지 않는다.

## 2026-10-22 최신 변경 · 12.4

DB 세션 재사용·선택형 암호화 자동 연결·TCP 자동 시작·저장된 페어링 자동 연결과 UI를 개선했다. [현재 시작 안내](docs/guides/STARTUP_UI_12_4_KO.md)와 [이번 검증 및 현장 확인 항목](docs/validation/VALIDATION_STAGE12_4_WINDOWS.md)을 우선한다. 얼굴 식별은 기존 12.3 흐름을 유지한다.

[아래 방향 등록 정체 개선 및 재시험](docs/fixes/GUIDED_DOWN_20261022_KO.md): 앱 재시작 후 새 안내에 따라 실제 카메라 확인 필요.

[등록 방향 안내 애니메이션](docs/guides/ENROLLMENT_ANIMATION_KO.md)은 시험 적용이다. 불편하면 등록 화면의 ‘움직이는 방향 안내’를 해제한다.
