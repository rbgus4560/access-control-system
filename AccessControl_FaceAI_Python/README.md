# 독립 얼굴 AI 검증 프로젝트

face_ai_python 폴더는 카메라·자세 보정·5방향 얼굴 등록·동일인 비교를 검증하는 독립 도구다. C#과 DB·녹화·전체 직원 식별이 연결된 확장본은 형제 폴더 AccessControl_UI에 있다. 두 버전은 역할이 달라 소스를 서로 덮어쓰지 않는다.

설치·실행은 [도구 안내](face_ai_python/README.md), 과거 검사 범위는 [검증 기록](docs/validation.md)을 참고한다. 대상 Python 환경은 access_face_ai의 Python 3.11이다.

2026-10-21에는 face_logic.py의 반복 입력 정규화를 정리하고 기존 9개 검사와 전후 수치 비교를 통과했다. 생성 Python 캐시는 삭제했고 모델과 캐시는 보존했다. 통합 개발 과정과 이번 정리 기록은 [개발 과정](../AccessControl_UI/docs/development-history.md), [리팩토링 기록](../AccessControl_UI/docs/refactor-20261021.md), [전체 트러블슈팅](../AccessControl_UI/docs/troubleshooting.md)에 정리했다.
