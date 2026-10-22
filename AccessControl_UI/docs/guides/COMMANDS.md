# 실행과 검사 스크립트

모든 경로는 AccessControl_UI 프로젝트 루트 기준이다. 일반 실행은 역할별 두 CMD를 사용한다.

| 파일 | 역할 |
|---|---|
| Start_Server.cmd | 서버·등록 앱을 빌드하고 서버 실행 |
| Start_Client.cmd | 인증 클라이언트 빌드·실행 |
| scripts/checks/Check_Enrollment_UI.cmd | 등록 UI 생성·설정 창 재열기 검사. 카메라·AI·DB 작업은 시작하지 않음 |
| scripts/checks/Run_Retention_Checks.cmd | 임시 파일을 사용하는 보관 정책 검사 |
| FaceAI/setup_environment.bat | 통합 AI의 Python 환경 준비 |

검사 CMD는 자신의 위치에서 두 단계 위의 프로젝트 루트를 계산하므로 실행 당시 탐색기·터미널 위치와 관계없이 프로젝트를 찾는다. 검사 실행에 .NET SDK와 의존성 복원이 필요할 수 있다. UI 검사는 실제 창을 연다.

2026-10-21에 Start_Net10.cmd와 Start_Stage7.cmd, Start_Stage8.cmd, Start_Stage9.cmd, Start_Stage10.cmd를 삭제했다. 다섯 파일은 해시까지 동일한 서버/클라이언트 선택 래퍼였으며 별도 기능은 없었다. 과거 개발·검증 문서에 나오는 해당 이름은 당시 기록으로 남긴다. 현재 실행에는 Start_Server.cmd 또는 Start_Client.cmd를 사용한다.

등록·보관 검사 파일은 삭제하지 않고 scripts/checks로 이동했다. 과거 문서의 루트 파일명 안내 대신 위 현재 위치를 사용한다. Word 보고서는 작성 당시 개발 기록이며 최신 실행 위치는 이 문서와 문서 목차를 기준으로 한다.

## 12.4 시작 설정 검사

AccessControl_UI에서 `dotnet run --project StartupChecks/StartupChecks.csproj`로 합성 데이터의 Windows 사용자 암호화 저장을 검사한다. 실제 Windows 사용자 컨텍스트가 필요하다. 운영 설정을 사용하지 않으며 임시 폴더만 생성·삭제한다. [12.4 사용법](STARTUP_UI_12_4_KO.md)을 참고한다.
