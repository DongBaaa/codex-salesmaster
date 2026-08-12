# 거래플랜 사용자 메뉴얼 PDF 생성

이 디렉터리는 `거래플랜 사용자 메뉴얼.pdf`를 다른 checkout에서도 다시 만들 수 있게 하는 추적 대상 생성 소스입니다. 생성 PDF와 검증 JSON은 산출물이므로 Git에 넣지 않습니다.

## 생성 기준

- Windows와 Python 3.13을 사용합니다.
- 한국어 본문은 Windows 기본 `맑은 고딕`(`malgun.ttf`, `malgunbd.ttf`)을 사용합니다. Microsoft 폰트 파일은 저장소에 복사하지 않습니다.
- Python 패키지는 `requirements.lock.txt`의 버전과 wheel SHA-256으로 고정합니다.
- 현재 버전은 다음 추적 파일에서 생성 시점에 읽습니다.
  - `Desktop\거래플랜.Desktop.App\거래플랜.Desktop.App.csproj`
  - `Mobile\GeoraePlan.Mobile.App\GeoraePlan.Mobile.App.csproj`
  - `배포\stable.json`
- `배포\업데이트\manifest\stable.json`은 로컬 게시 과정에서 만드는 무시 대상 복사본이므로 재현 입력으로 사용하지 않습니다.
- 화면 캡처 날짜·Desktop 버전·파일 SHA-256은 `assets\capture-manifest.json`에서 검증합니다.

## 실행

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  "D:\거래플랜\tools\manual\Build-GeoraePlanUserManualPdf.ps1" `
  -ProjectRoot "D:\거래플랜"
```

기본 실행은 `.tooling\manual-pdf`에 Python 3.13 가상환경을 만들고 hash-locked 의존성을 설치합니다. 이미 같은 의존성이 있는 Python 3.13을 사용할 때만 `-PythonPath`와 `-SkipDependencyInstall`을 함께 지정할 수 있습니다.

## 산출물과 검증

- `output\pdf\거래플랜 사용자 메뉴얼.pdf`
- `거래플랜 사용자 메뉴얼.pdf` 루트 사본
- `output\pdf\georaeplan-user-manual.verification.json`

생성기는 다음을 통과하지 못하면 실패합니다.

- 두 PDF 사본의 SHA-256 동일성
- A4, 암호화 없음, title metadata
- 모든 페이지의 추출 가능 텍스트
- csproj·stable manifest에서 읽은 버전
- Android 지원 범위, `.gpbackup`, `adb install -r` 핵심 문구
- 캡처 자산 15개의 SHA-256

이 작업은 문서 산출물만 만들며 live 배포, stable manifest 게시, 앱 버전 증가, 정식 설치 패키지 생성, Git commit/push를 수행하지 않습니다.
