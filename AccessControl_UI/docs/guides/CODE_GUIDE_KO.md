> 보존된 개발 자료입니다. 본문의 실행 명령과 소스 경로는 AccessControl_UI 프로젝트 루트 기준입니다. 최신 실행 안내는 [문서 목차](../README.md)를 참고하세요.

# AccessControl_UI 코드 읽기 안내

## 기준과 목적

이 소스는 정상 동작을 확인한 12.3의 기능을 유지하고, 들여쓰기와 설명 주석을 정리한 버전이다. 얼굴 인식 모델을 직접 학습하는 프로젝트가 아니라, 기존 얼굴 분석 모델과 C# 관리 앱·통신·DB·영상 처리를 연결한 출입 통제 데모다.

클라이언트는 카메라가 있는 구역의 영상 분석과 녹화를 담당한다. 서버는 직원·얼굴 등록 정보를 관리하고 현재 DB 권한으로 최종 출입 여부를 판정한다. 같은 PC에서 두 앱을 실행해도 TCP 통신 구조는 유지된다. 실제 두 PC 통신은 별도 네트워크 검증이 필요하다.

## 폴더별 역할

| 폴더 | 담당 기능 | 읽기 시작할 파일 |
| --- | --- | --- |
| AccessControlServer | DB 연결, 중앙 권한 판정, 관제·영상 조회 | MainWindow.xaml.cs, FaceDatabase.cs, AccessEventDatabase.cs |
| FaceRecognition | 구역 카메라, 전체 직원 식별, 녹화·클립 작업 | MainWindow.xaml.cs, GallerySynchronizer.cs, ObservationSource.cs |
| FaceEnrollment | 직원 얼굴 등록 화면과 AI 등록 제어 | MainWindow.xaml.cs |
| FaceService / Shared | 앱 사이의 얼굴·DB 요청 계약 / Python 연동과 공통 기능 | FaceService/RemoteTransport.cs, Shared/AiClient.cs |
| Network | TCP 통신, 관측 정보와 판정 전달 | 폴더의 프로토콜·클라이언트·서버 클래스 |
| FaceAI | 얼굴 검출·정렬·자세 분석·특징 추출·식별 | ai_worker.py, face_gallery.py, face_engine.py |
| Database | 테이블 생성과 변경 이력 SQL | 각 파일 상단의 적용 대상과 순서 |
| TwoPcChecks / DbChecks / FaceAI/tests | 통신·동기화·SQL·AI 로직 검사 | 각 검사 실행 파일과 테스트 함수 |

파일 이름과 실제 역할은 각 소스 상단의 `파일 역할` 주석에서도 확인할 수 있다. 모든 줄을 설명하기보다, 상태 변경·저장·취소·재시도처럼 흐름을 이해하는 데 필요한 부분에 설명을 추가했다.

## 기능을 따라 읽는 순서

1. 각 앱의 `MainWindow.xaml`에서 화면 구성을 보고 `MainWindow.xaml.cs`에서 버튼과 상태 변경의 연결을 확인한다.
2. `GallerySynchronizer.cs`에서 서버 직원 목록을 받고, 변경된 얼굴 기준을 모아 완성된 목록만 공개하는 흐름을 읽는다.
3. `AiClient.cs`와 `FaceAI/ai_worker.py`에서 C#과 Python 사이의 명령·결과 전달을 확인한다.
4. `FaceAI/face_gallery.py`에서 한 얼굴의 특징을 등록 직원 전체와 비교하고, 비슷한 후보가 겹칠 때 판정을 보류하는 과정을 읽는다.
5. `ObservationSource.cs`와 서버 `AccessEventDatabase.cs`에서 식별 후보가 최종 권한 판정으로 이어지는 경로를 확인한다.
6. `RecordingSync.cs`, `MediaAgent.cs`, `ClipExporter.cs`와 서버 `RecordingDatabase.cs`에서 녹화 메타데이터 동기화와 파일·클립 처리를 확인한다.

## 주요 용어와 코드 표현

| 표현 | 이 프로젝트에서의 의미 |
| --- | --- |
| XAML / code-behind | 화면 배치 파일 / 그 화면의 이벤트와 상태를 처리하는 C# 파일 |
| partial class | 같은 클래스를 여러 파일로 나누는 방식. DB 기능도 담당 기능별 파일로 나뉜다. |
| async / await / Task | DB·통신 같은 대기 작업을 비동기로 연결하는 표현. 모든 작업이 자동으로 다른 스레드에서 실행되는 것은 아니다. |
| CancellationToken | 앱 종료·연결 해제·시간 초과 시 진행 중인 작업에 중단을 전달하는 값 |
| Dispatcher | 카메라·통신 작업의 결과를 WPF UI 스레드에 전달할 때 사용하는 기능 |
| DTO / record | 요청·응답·관측 정보를 전달하기 위한 데이터 구조 |
| embedding | 얼굴 비교에 사용하는 수치 특징 벡터. 이 프로젝트의 ArcFace 특징은 512차원이다. |
| gallery / snapshot | 등록 직원의 비교 기준 목록 / 검증이 끝난 시점의 완성된 목록 |
| lease | 서버가 발급한 직원·등록 기준 연결 토큰. 클라이언트가 보낸 이름만으로 권한을 주지 않기 위한 확인 정보 |
| ACK | 요청이 실제 적용되거나 저장되었다는 응답. 전송 성공과 처리 완료를 구분한다. |
| outbox | 서버에 아직 반영되지 않은 녹화 메타데이터를 보관하고 다시 보내는 목록 |
| UPSERT / 멱등성 | 신규 데이터는 추가하고 기존 데이터는 조건에 따라 갱신하는 SQL / 같은 요청을 반복해도 중복 처리가 생기지 않는 성질 |
| Npgsql / PostgreSQL | C#에서 PostgreSQL에 접속하는 드라이버 / 데이터를 저장하는 DB 서버 |
| SslStream / DPAPI | TCP 위의 TLS 암호화 통신 / Windows 사용자·장치에 연결된 설정·특징 보호 기능 |

## 유지해야 할 처리 원칙

- 클라이언트는 식별 후보를 보내고 서버는 최신 DB 권한으로 최종 판정한다.
- 직원 기준은 일부만 받은 상태에서 교체하지 않는다. 새 목록 검증이 끝난 뒤 교체한다.
- 등록 취소나 오류가 기존 정상 얼굴 기준을 덮어쓰지 않도록 한다.
- 오래된 분석 결과, 불완전한 기준, 모호한 후보는 인증 성공으로 처리하지 않는다.
- 녹화 원본은 클라이언트 파일로 보관하고 DB에는 구간 정보와 위치를 저장한다.
- 서버 재연결 시 미전송 메타데이터를 재시도하되 한 구간의 실패가 다른 구간을 막지 않도록 한다.
- 정면 보정과 자세 확인은 생체 위조 방지 검증을 의미하지 않는다.

## 서식 유지

`.editorconfig`에 공백 4칸, 파일 인코딩, 줄 끝과 마지막 줄 규칙을 넣었다. C#·Python·XAML의 압축된 코드를 펼치고 불필요한 줄 끝 공백을 제거했다. Windows 실행 스크립트의 CRLF와 PowerShell의 UTF-8 BOM은 유지한다.

VS Code나 Visual Studio에서 폴더 전체를 열어 편집한다. 파일을 메신저에서 다시 복사하기보다 압축을 풀어 원본 파일로 사용하는 편이 안전하다. 에디터가 EditorConfig를 지원하더라도 자동 서식 적용 범위는 사용하는 확장과 설정에 따라 달라질 수 있다.
