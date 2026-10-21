# Windows TLS 개인 키 로딩 수정 (2026-10-20)

## 관찰된 문제
- TCP 5050 접속 검사 성공, 앱 연결은 재접속 반복.
- 직원 조회: unexpected EOF.
- 서버 Windows Schannel 이벤트 36869: TLS 인증서의 개인 키 정보 누락. 프로세스 AccessControlServer.

## 수정
- Shared/PairingStore.cs에서 Windows 인증서 PFX 로딩을 EphemeralKeySet에서 UserKeySet으로 변경.
- Windows Schannel이 사용할 수 있는 사용자 키 컨테이너로 로드. PersistKeySet은 지정하지 않아 임포트된 키는 인증서 수명에 따라 정리.
- 다른 운영체제는 기존 EphemeralKeySet 유지.
- 개인 키 존재 여부 검사 추가.
- 기존 server.bin, 인증서, 연결 토큰을 그대로 사용. DB와 등록 얼굴 데이터 변경 없음.

## 적용 및 검증
1. 실행 중인 서버와 클라이언트 앱을 모두 종료.
2. 압축을 새 폴더에 완전히 풀고 Start_Server.cmd 실행. 기존 Windows 사용자 계정 유지.
3. DB 연결, TCP 서버 시작. 새 폴더의 Start_Client.cmd 실행.
4. 서버 연결 코드를 적용하고 서버 연결. 수신 확인 순번 증가와 직원 조회 확인.
5. 카메라 연결 후 영상 수신과 인증 이벤트 확인.

이 수정은 사용자가 제공한 Schannel 로그에 근거한다. Linux 빌드와 테스트는 Windows Schannel 실기 검증을 대신하지 않는다. 최종 연결 성공은 사용자 PC에서 확인해야 한다.

관련 공식 문서: https://learn.microsoft.com/en-us/dotnet/core/extensions/sslstream-troubleshooting

## 빌드 확인
Linux .NET SDK 10.0.402에서 EnableWindowsTargeting=true로 3개 WPF 프로젝트 빌드 성공. Windows Schannel 연결 시험은 아직 수행하지 않음.
