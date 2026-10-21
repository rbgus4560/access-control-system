# .NET 10 전환 기록 10.5-net10

작성일: 2026-10-19. 출발 버전은 10.5이며 이번 변경은 실행 대상과 경로 전환이다.

## 변경

- FaceRecognition, FaceEnrollment, AccessControlServer: net8.0-windows에서 net10.0-windows로 변경.
- Checks, FaceChecks, NetworkChecks: net10.0으로 변경해 같은 주요 런타임에서 검사.
- 기존 Start_Stage7/8/9/10.cmd의 실행 경로를 net10.0-windows로 수정.
- Start_Net10.cmd 추가. 기존 세 앱을 닫은 상태에서 빌드 후 서버·인증 앱을 실행한다.
- 인증 앱의 사용자 등록 버튼이 찾는 등록 EXE 경로를 net10.0-windows로 수정.
- 세 메인 창 제목에 .NET 10 표시.
- global.json: 10.0.100 이상 안정판 10.0 SDK 기능 밴드 허용(latestFeature). 사용자 SDK 10.0.401 사용 가능.
- README 상단에 최신 적용 절차 추가. 이전 단계 문서는 당시 검증 이력으로 유지.
- 10-5 클립 실제 재생, 녹화 없는 시각, LIVE 복귀에 대한 기존 사용자 확인을 VERIFIED_PROGRESS.md에 보완.

이번 변경에서 얼굴 모델·전처리·Python 의존성·DB 스키마·Npgsql 8.0.9는 유지했다. WPF UI 설치·연속 등록·자동 보관 정리는 아직 후속이다.

## 내부 확인

- Linux의 .NET SDK 10.0.402로 Windows 대상 세 WPF 프로젝트 솔루션 빌드 성공.
- 생성된 각 runtimeconfig.json의 tfm net10.0 및 Microsoft.NETCore.App / Microsoft.WindowsDesktop.App 주요 버전 10 확인.
- HandoffChecks: 메모리 기준 수신·소유권·재연결·모델 거부·교체·초기화 통과. 이 환경의 Unix 소켓 제한으로 Named Pipe 전송 부분은 건너뛰었으며 Windows 검증이 필요하다.
- FaceChecks: 5방향·512차원·모델 검증, 요청 상관관계·한글 JSON·크기 제한, 녹화 계약·경로, 타임라인 경계, 이벤트 재생 요청, 클립 범위·해시 메타데이터 검사 통과.
- NetworkChecks: 실제 loopback 상태·영상 TCP, ACK·heartbeat·재접속·잘못된 패킷 복구·취소, 권한 32조합, 시도·재무장·이벤트 ID·허가 응답 검증 통과.
- 소스 ZIP에는 bin/obj·사용자 특징·비밀번호·실제 녹화·모델 캐시를 넣지 않음.

이는 실제 Windows 카메라·DPAPI·PostgreSQL·MediaElement의 실행 검증을 대신하지 않는다. 학교 PC의 SDK는 10.0.401, 런타임은 10.0.12로 사용자가 확인했다. 내부 빌드와 학교 PC의 SDK 패치 버전이 다르므로 학교 빌드 결과도 확인한다.

## 학교 PC 적용

1. 기존 세 앱을 종료하고 AccessControl_UI 폴더를 AccessControl_UI_backup_10.5로 복사한다.
2. ZIP을 풀고 AccessControl_UI 폴더 안 내용을 기존 AccessControl_UI 안에 덮어쓴다. 폴더가 이중으로 중첩되지 않게 한다.
3. 기존 net8.0-windows EXE 대신 Start_Net10.cmd를 실행한다.
4. 빌드가 실패하면 오류를 확인한다. 성공하면 서버·인증 창 제목의 .NET 10을 확인한다.
5. 서버에서 기존 DB 연결·목록 조회·관리자 선택·TCP 서버 시작.
6. 인증 앱 서버 연결·직원 목록 조회·선택 직원 얼굴 불러오기.
7. 카메라·AI 연결 후 본인 비교·서버 판정·서버 영상 확인.
8. 사용자 등록 버튼으로 등록 창 열림 확인. 카메라 1대 환경에서는 인증 카메라를 먼저 종료한 뒤 등록 카메라를 연다.
9. 녹화 종료·READY·기존 영상 재생·LIVE 복귀·짧은 클립 추출을 확인한다.

처음 적용 확인은 4~7번까지 진행한 뒤 나머지를 이어서 한다. DB 생성 SQL이나 얼굴 모델 준비를 처음부터 다시 할 필요는 없다. 기존 로컬 저장 경로·DB를 사용하며 새 패키지가 실제 기록을 수정했다는 뜻은 아니다.

## 실행 경로

```text
AccessControlServer\bin\Debug\net10.0-windows\AccessControlServer.exe
FaceRecognition\bin\Debug\net10.0-windows\FaceRecognition.exe
FaceEnrollment\bin\Debug\net10.0-windows\FaceEnrollment.exe
```

서버 전체 종료 중 녹화 복구 검증에서는 인증 앱을 계속 켜 두므로 서버 EXE만 다시 연다. Start_Net10.cmd를 다시 실행해 인증 앱을 중복으로 띄우지 않는다.

## 다음

학교 PC 기존 기능 확인 후 서버 전체 종료 중 녹화·복구 동기화 검증, 자동 녹화 정리, 연속 등록·비교 검증, WPF UI 적용 순서로 진행한다. 이 문서 작성 시점에는 전환본의 사용자 PC 기능 확인을 아직 받지 않았다.

공식 참고: https://learn.microsoft.com/en-us/dotnet/core/versions/selection 및 https://learn.microsoft.com/en-us/dotnet/core/compatibility/10
