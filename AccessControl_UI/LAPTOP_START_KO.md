# 노트북 클라이언트 시작 (12.2.2)

## 옮길 파일
이 ZIP 전체를 노트북으로 옮겨 새 폴더에 완전히 압축 해제합니다. FaceAI 폴더에 Python 소스·requirements.txt·setup_environment.bat·준비 검사·기본 모델 파일이 포함됩니다. 별도 Python 소스 ZIP은 필요 없습니다.

학교 PC의 Anaconda envs 폴더를 통째로 복사하는 방법은 사용하지 않습니다. 노트북에서 가상환경을 새로 생성합니다. ArcFace 등의 모델 가중치 캐시는 이 ZIP에 포함되지 않으므로 준비 검사 중 다운로드하거나 학교 PC에서 사용하는 모델 캐시 폴더를 별도로 복사합니다. 두 PC에서 서로 다른 코드 버전을 실행하지 않도록 학교 PC도 최신 ZIP을 사용합니다.

## 설치·확인
1. Windows용 .NET 10 SDK 설치. 런타임만으로는 Start_Client.cmd 빌드를 할 수 없습니다. Visual Studio 전체 설치는 필수가 아닙니다.
2. Anaconda 설치 후 Anaconda Prompt 실행.
3. 압축 해제한 프로젝트의 FaceAI 폴더로 이동합니다. 실제 노트북 경로에 맞춰 입력합니다.
```bat
cd /d "C:\실제경로\AccessControl_UI\FaceAI"
setup_environment.bat
```
4. 설치 성공 후 환경 활성화와 모델 준비 검사:
```bat
conda activate access_face_ai
python check_setup.py --prepare
where python
```
5. Python·모델 준비 PASS 확인 후 프로젝트 루트의 Start_Client.cmd 실행.
6. 클라이언트 설정에서 where python 결과 중 access_face_ai 환경의 python.exe, 이번 FaceAI 폴더, 노트북의 모델 캐시 폴더, USB 카메라 번호 지정. 학교 PC의 사용자 경로를 그대로 사용하지 않습니다.
7. 학교 PC는 서버·PostgreSQL 실행. 노트북에는 PostgreSQL·DBeaver 설치 불필요.
8. 학교 PC 서버의 현재 LAN IPv4 선택·TCP 시작·연결 코드 복사. 노트북 클라이언트에 전달 후 코드 적용·연결.
9. 직원 조회·얼굴 기준 불러오기·카메라 연결 후 서버 영상과 인증 판정 확인.

## 모델 캐시 복사 시
학교 PC에서 설정한 모델 캐시 폴더의 내부 디렉터리 구조를 유지해 노트북으로 복사하고, 노트북 설정에서 그 폴더를 선택합니다. .deepface 등 숨김 하위 폴더가 누락되지 않도록 폴더 전체를 복사합니다. 등록 얼굴·직원 DB는 서버에서 조회하므로 노트북에 복사할 필요가 없습니다.

## 12.2.2 화면 변경
- 등록 화면: LIVE CAMERA 제목과 등록 안내를 세로로 배치한 별도 헤더. 카메라 이미지와 겹치지 않도록 구획 분리.
- 직원·연속 등록 패널: 내용 오른쪽 여백과 줄바꿈·가로 늘이기 적용. 스크롤바와 문구 간격 확보.
- 클립 요청 창: 폴더 열기 버튼을 아래 고정 영역에 배치. 입력·결과 영역은 세로 스크롤 가능.
- TLS 개인 키 및 녹화 동기화 실패 격리 수정 포함.

아래 방향 얼굴 등록 인식 개선, 이전 녹화 15건의 충돌 정리, 잔여 클립 작업의 완료 여부 확인은 이 UI 수정으로 해결됐다고 볼 수 없습니다. 두 PC 실제 통신과 Windows 배율별 화면 확인도 사용자 환경에서 진행해야 합니다.

빌드 검증: Linux SDK 10.0.402, Windows targeting으로 등록·인증·서버 WPF 3개 빌드 성공. 실제 Windows UI 실행·배율 검증은 미수행.
