> 보존된 개발 자료입니다. 본문의 실행 명령과 소스 경로는 AccessControl_UI 프로젝트 루트 기준입니다. 최신 실행 안내는 [문서 목차](../README.md)를 참고하세요.

# 12.0 두 PC 전환 검사 기록 · 2026-10-19

## 이번 변경

- 기존 학교 DB PC를 관리 서버로 유지. 직원·권한 수정/연속 얼굴 등록·최종 출입 정책/DB 로그·영상 관제/타임라인·클립 요청을 서버 역할로 배치.
- 카메라 클라이언트는 직접 DB 접속 없이 TLS API로 직원·등록 기준을 조회. 기존 1:1 비교와 Python 모델을 유지하고 서버 최종 판정을 표시.
- 판정·영상 채널에 TLS 추가. 기준·녹화 상태/메타데이터 API 및 클라이언트 발신 미디어 연결 추가. 연결 코드에 서버 IP·포트·인증서 SHA-256 지문·장치 공유 키를 포함하고 Windows DPAPI로 저장.
- 클라이언트가 녹화 원본을 유지·정리하고 메타데이터를 서버로 동기화. 서버는 가상 원본 키와 JSONB 메타데이터를 저장한다. 원격 READY는 장치가 보고한 상태이며 서버 로컬 원본 존재 검사가 아니다.
- 재생은 서버 DB 구간 선택 후 GUID로 요청·스트리밍 수신·크기/SHA-256 검증·서버 캐시 재생. 클립은 클라이언트에서 생성하고 서버 사본 수신 확인 후 그 임시 클립만 정리.
- 클립 접수 파일과 수신 결과를 남겨 서버 재시작 후 전송/DB 완료 기록을 재시도. 서버 캐시와 클라이언트 원본은 종료 후 72시간 보관 정리, 사용 중 파일 보호.
- Start_Server.cmd / Start_Client.cmd, IP 한정 서버 방화벽 스크립트, Database/15_recording_client_metadata.sql, ../guides/DEPLOY_TWO_PC.md 추가.

## 통과한 내부 검사

| 검사 | 근거와 결과 | 한계 |
|---|---|---|
| WPF 빌드 | .NET SDK 10.0.402, net10.0-windows 세 프로젝트 교차 빌드 성공 | Linux 컴파일이며 Windows UI·장치 실행 아님 |
| 기존 얼굴/녹화 계약 | FaceChecks: 5/9기준·대표 샘플 키·직렬화 크기/상관 ID·타임라인·클립 범위 검사 PASS | 카메라/실제 얼굴 모델 미사용 |
| 기존 판정/영상 | NetworkChecks: 노후 분석·다중 얼굴·재시작·ACK·권한 조합·불일치/미기록 허가 거절·JPEG·순번/용량·통신 중단 검사 PASS | 루프백 소켓, 실제 학교 LAN 아님 |
| 로컬 기준 이전 검사 | Checks/HandoffChecks: 기준 메모리 계약 PASS | 이 환경의 Unix socket 제한으로 Windows named pipe OS 전송 검증 제외 |
| 원본 보관 | RetentionChecks: 72시간 경계·메타데이터·사용 중 보호·정리 경쟁·중단 복구·링크/범위 거절 PASS | 임시 파일만 사용, 실제 사용자 영상/DB 미변경 |
| TLS/신규 API | TwoPcChecks: 실제 SslStream 기반 판정·JPEG ACK·재접속, 잘못된 키/인증서 지문/채널 거절, 원격 save 차단, 9개 대표 특징·한글·lease 응답 PASS | 테스트용 인증서/장치 키·가짜 DB 응답 사용 |
| 파일 전송 | 실제 스트림에서 크기/해시·잘린 전송·상한·기존 정상 파일 보존·미완성 part 정리 PASS | MP4 코덱 재생 검사 아님 |
| 프로세스 분리 | 서로 다른 사용자 데이터 폴더의 두 프로세스에서 실제 MediaAgent/MediaCoordinator/API를 연결. 원본 요청·클립 전송·서버 수신 후 임시 클립 삭제·원본 유지 PASS | 클립 Python 실행은 결정적 파일 생성 대역, DB는 검사 대역 |
| 서버 재시작 | 클립 수신 후 DB 반영 실패를 주입. 서버 API·coordinator 종료/재생성 후 미디어 재접속·디스크 job/결과 복원·DB 완료 재시도 PASS | 실제 Npgsql 네트워크 장애/Windows 앱 종료 실험 아님 |
| PostgreSQL SQL | 임시 PGlite PostgreSQL 엔진에 원본 스키마·장치 seed·13/14/15 반복 적용. 실제 코드의 녹화 UPSERT를 추출해 RECORDING/READY/DELETED 전이·중복·경로 불변·상태 역행 거절·기존 NULL 메타데이터 보존 PASS | 실제 학교 PostgreSQL에 접속한 검증 아님 |

FaceAI Python 알고리즘과 테스트 파일은 11.1과 동일하게 보존했다. 이전 39개 Python 검사 통과 이력을 이번에 새 모델을 실행한 결과로 표현하지 않는다.

## 주요 코드 위치

| 경로 | 역할 |
|---|---|
| Network/SecureLink.cs | 길이 제한 JSON 프레임, 인증서 고정 확인 TLS, 채널·키 인증 |
| Shared/PairingStore.cs | 연결 코드와 사용자별 DPAPI 설정 저장 |
| FaceService/RemoteTransport.cs | 읽기/녹화 API와 장치 발신 미디어 세션 |
| FaceService/MediaTransfer.cs | 스트리밍 해시 검증과 임시 파일·완료 파일 전환 |
| FaceRecognition/MediaAgent.cs | 서버 요청 원본 선택, 로컬 클립 생성·전송·수신 확인 후 정리 |
| FaceRecognition/ClipExporter.cs | 기존 Python export_clip.py 프로세스 실행 |
| FaceRecognition/RecordingSync.cs | 클라이언트 녹화 JSON outbox·자동 정리·서버 DB 재동기화 |
| AccessControlServer/MediaCoordinator.cs | 영상 요청 큐, 서버 사본 검증·저장, 영속 클립 요청/DB 재시도 |
| AccessControlServer/RecordingDatabase.cs | 가상 원본 키·client_metadata 저장과 서버 타임라인 조회 |
| AccessControlServer/PlaybackPanel.cs | 서버의 원격 원본 준비 후 Windows 재생 |
| AccessControlServer/ServerPlaybackWindow.cs | 서버 타임라인·시간 범위 클립 요청 화면 |
| TwoPcChecks | 새 통신 통합 검사. OS 데이터 폴더를 분리하는 후반 프로세스 검사는 Linux 대상 |

## 남은 Windows 실측

../guides/DEPLOY_TWO_PC.md의 마지막 순서대로 실제 두 PC를 연결한다. 학교 LAN 세 포트, Windows 인증서/DPAPI, 서버 등록용 카메라, 클라이언트 새 Python 환경/모델, 실제 얼굴 판정·녹화·서버 수신 MP4 재생·클립 생성, 서버 전체 종료 중 촬영/복귀를 확인해야 한다.

이번 상태를 정확도 수치·최대 카메라 수·실제 LAN 지연·생체 위조 방지 성능으로 해석하지 않는다. 현재 한 클라이언트(CAM-01), 한 명 선택 비교, 신뢰한 클라이언트가 전달한 특징 비교 결과에 서버 정책을 적용하는 데모다.

원본 만료/설정 오류로 실패하는 영속 클립 요청의 취소 전용 화면은 없다. DB 접수와 디스크 job 생성 사이의 프로세스 강제 종료는 DB EXPORTING 행만 남길 수 있으며, 이 극단적 구간까지 DB 작업 큐로 복구하는 기능은 포함하지 않았다. 정상 기준 폴더를 유지한 채 Windows 점검 후 시연 기준 버전을 결정한다.
