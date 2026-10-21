# 녹화 동기화 실패 격리 (2026-10-20)

## 확인된 현상과 미확정 원인
- 사용자 PC에서 오늘 MP4와 동일 이름 JSON 생성 확인.
- 첨부 JSON은 CAM-01, 2026-10-20 15:40:16~15:40:50 KST, READY.
- DB에는 이전 날짜 READY 15건만 존재. client_metadata 칼럼은 존재.
- 기존 오류 문구만으로는 실제 실패 원인이 드러나지 않음. 이전 구간 경로 충돌 가능성은 있으나 아직 확정하지 않음.

## 수정 내용
- 한 구간 저장 실패가 나머지 구간의 동기화를 중단하지 않도록 구간별 오류 처리.
- 16건 배치보다 실패 구간이 많아도 새 구간이 계속 처리되도록 시도 순서 순환.
- 실패 구간은 성공 처리하지 않고 대기열에 유지해 자동 재시도.
- 클라이언트 녹화·보관 상태에 실패 구간과 오류 메시지 표시.
- 서버 요청 실패를 사용자별 로컬 server-api-errors.log에 기록. 요청 본문·인증 토큰·얼굴 벡터는 기록하지 않음. 2MB 초과 시 이전 로그 한 개 보관.
- 기존 TLS 개인 키 수정 유지. DB·원본 MP4·JSON·과거 경로를 변경하지 않음.

## 적용
1. 서버와 클라이언트 앱 모두 종료.
2. 최신 ZIP을 새 폴더에 완전히 풀기.
3. Start_Server.cmd 실행 후 DB 연결·TCP 서버 시작.
4. Start_Client.cmd 실행 후 서버 연결. 카메라를 연결하지 않아도 기존 녹화 JSON 동기화는 실행.
5. 클라이언트 설정 > 녹화·보관에서 대기·실패 상태 확인.
6. 서버 타임라인·클립에서 오늘 날짜 > 구간 조회.
7. 여전히 실패하면 서버 PC의 다음 파일을 확인:

`%LOCALAPPDATA%\AccessControlSystem\server-api-errors.log`

PowerShell:
```powershell
Get-Content "$env:LOCALAPPDATA\AccessControlSystem\server-api-errors.log" -Tail 60
```

## 검증 범위
실제 Windows DB 동기화 복구 여부는 수정본 실행 후 확인 필요. Linux에서의 회귀 시험은 실패 격리·공정한 재시도 동작을 검증하며 실제 PostgreSQL·카메라 동작을 대신하지 않음.

검증 결과: SDK 10.0.402 WPF 3개 빌드 PASS. TwoPcChecks --sync-only: 실패 20건 이후 신규 1건 처리 PASS, 실패 복구 후 21건 ACK 및 중복 전송 없음 PASS.
