# ImageViewer

크로스 플랫폼 이미지 뷰어. 일반 폴더와 ZIP 아카이브 내부의 이미지를 압축 해제 없이 빠르게 탐색한다.

- 플랫폼: Windows 10 / 11, macOS, Linux
- 기술 스택: C# / .NET 10 / Avalonia UI (MVVM) / Magick.NET (Q8, win-x64 OpenMP · 타 플랫폼 AnyCPU) / SkiaSharp (GIF · WebP 애니메이션) / SharpCompress

## 주요 기능

- 폴더 · ZIP(CBZ) · 이미지 파일 열기 (명령행, 메뉴, 드래그 앤 드롭, 탐색기 우클릭 메뉴)
- 파일명 자연 정렬(image2 < image10), 처음/끝 순환 탐색
- 표시 모드: 원본 / 폭 맞춤 / 높이 맞춤 / 꽉 차게(비율을 유지한 채 이미지 전체를 창에 맞춤, 기본값), 확대·축소
  (맞춤 모드는 작은 이미지도 표시 영역에 맞춰 확대)
- 필터: 없음(기본값) / 부드럽게 / 부드럽게 + 선명하게 (화면 표시용 이미지에만 적용). 강도는 설정 > 필터에서 조절하며 화면 픽셀 기준이라 작은 이미지를 확대해 봐도 흐림이 같이 커지지 않는다 (부드럽게 기본 0.5)
- 슬라이드쇼: 5~90초, 순차 / 랜덤(직전 이미지 연속 방지), 반복 옵션
- 이미지 위에 떠 있는 반투명 하단 바(버블): 축소·배율 초기화·확대 | 이전 · 위치 · 다음 | 슬라이드쇼 · 고정 핀, 썸네일 목록
  - 설정 > 썸네일에서 썸네일 목록 아래 가로 스크롤바를 켤 수 있다 (기본 꺼짐, 켜도 썸네일이 한 줄에 다 들어가면 숨김)
  - 상단 바 · 하단 바 각각 핀(Pin)으로 고정하면 ` 키 · 전체 화면 · 마우스 위치와 관계없이 항상 표시,
    해제하면 마우스를 위쪽 / 아래쪽 가장자리에 올렸을 때만 표시
- ` 키로 상단 · 하단 바 고정(Pin) 일괄 전환 (하나라도 고정이면 모두 해제, 모두 해제면 모두 고정)
- 이미지 목록 대화 상자: 상단 썸네일 줄 + 파일명·날짜·크기 정렬 목록
- 회전(90° 단위) · 좌우 / 상하 반전: 화면 표시에만 적용, 다른 이미지로 이동하면 초기화
- GIF / WebP 애니메이션 재생 (보기 메뉴에서 끄면 첫 프레임만 표시)
  - 왼쪽 아래에 반투명 정보 표시: 프레임 n / m · 재생 상태, 재생 위치 / 전체 길이 · 현재 프레임 지연, 크기 · 형식
    (다중 페이지 TIFF 는 페이지 n / m, 크기 · 형식)
- 다중 페이지 TIFF: PageUp / PageDown 으로 페이지 이동, 하단 바에 "n/m쪽" 표시
- 이미지 정보(EXIF) 패널: Tab 으로 여닫음 (파일 · 해상도 · 형식 · 카메라 · 렌즈 · 촬영 일시 · 노출 · 조리개 · ISO · 초점 거리 · GPS 등)
- 폴더 감시: 이미지 추가 · 삭제 · 이름 변경 · 수정을 자동으로 목록에 반영 (설정에서 끌 수 있음, ZIP 은 제외)
- 기본 제목 표시줄 없는 창: 직접 그린 타이틀바(파일명 · 앱 이름 · 창 버튼)와 아이콘 메뉴바를 이미지 위에 겹쳐 표시
- 창 / 테두리 없는 전체 화면 전환(Alt+Enter), 이미지 영역 드래그로 창 이동, 창 위치·설정 저장
- 설정 > 정보: 프로그램 버전, 사용한 외부 라이브러리(버전 · 용도 · 라이선스 · 홈페이지 링크, .NET · Microsoft 제공 라이브러리 제외), 실행 환경(.NET · Windows).
  버전은 실행 중 실제로 불러온 라이브러리에서 읽는다. '정보 복사'로 클립보드에 텍스트로 복사

## 단축키

| 키                    | 기능                                                                |
| --------------------- | ------------------------------------------------------------------- |
| Alt + Enter           | 전체 화면 전환                                                      |
| Esc                   | 프로그램 종료 (대화 상자에서는 대화 상자 닫기)                      |
| `                     | 상단 · 하단 바 고정(Pin) 일괄 전환                                  |
| ← / ↑ , → / ↓         | 이전 / 다음 이미지                                                  |
| Home / End            | 처음 / 마지막 이미지                                                |
| [ / ]                 | 이전 / 다음 폴더 또는 ZIP 파일 (열린 파일이 없으면 열기 대화상자)   |
| PgUp / PgDn           | 이전 / 다음 페이지 (다중 페이지 TIFF, 그 밖에는 이전 / 다음 이미지) |
| R / L                 | 시계 / 반시계 방향 90° 회전                                         |
| H / V                 | 좌우 / 상하 반전                                                    |
| Tab                   | 이미지 정보(EXIF) 패널 표시 / 숨김                                  |
| Enter                 | 이미지 목록                                                         |
| 0 / 7 / 8 / 9         | 원본 / 폭 맞춤 / 높이 맞춤 / 꽉 차게                                |
| U / I / S             | 필터 없음 / 부드럽게 / 부드럽게 + 선명하게                          |
| + 또는 = / -          | 확대 / 축소 (Ctrl+0: 배율 초기화)                                   |
| Space                 | 슬라이드쇼 재생 / 일시정지                                          |
| Ctrl+O / Ctrl+Shift+O | 파일·ZIP 열기 / 폴더 열기                                           |

마우스: 휠 위 = 이전 이미지, 휠 아래 = 다음 이미지(설정에서 확대·축소로 변경 가능, Ctrl+휠은 반대 동작), Shift+휠 = 상하 이동,
드래그 = 창 이동(전체 화면에서는 이미지 이동), 가운데 버튼 드래그 = 이미지 이동, 더블클릭 = 최대화 / 복원,
뒤로/앞으로 버튼 = 이전/다음.

## 빌드 · 실행 · 테스트

```bash
dotnet build ImageViewer.sln
dotnet run --project src/ImageViewer -- "D:\Images"
dotnet test ImageViewer.sln
```

## 배포

### Windows (설치 프로그램 · 포터블)

```powershell
powershell -ExecutionPolicy Bypass -File build\publish.ps1
```

- `artifacts\publish\win-x64` 에 Self-contained 실행 파일을 만든다 (.NET 설치 불필요).
- [NSIS 3](https://nsis.sourceforge.io) 이 설치되어 있으면 `artifacts\installer\ImageViewer-<버전>-setup.exe` 설치 프로그램도 만든다
  (`src/ImageViewer.Installer/ImageViewer.nsi`).
- 설치 프로그램은 관리자 권한 없이 현재 사용자로 설치한다 (`%LOCALAPPDATA%\Programs\ImageViewer`, 64비트 Windows 전용).
  탐색기 메뉴 등록은 구성 요소 페이지의 선택 항목으로 동의를 받는다.
- 제거 시 탐색기 메뉴 키(이미지 확장자 포함), 시작 메뉴 · 바탕 화면 바로 가기, 설치 폴더, 프로그램 추가/제거 정보를 삭제한다.
  사용자 설정(`%LOCALAPPDATA%\ImageViewer`)은 남긴다.
- 포터블 단일 실행 파일 `ImageViewer-<버전>-portable.exe`, Windows Installer 패키지 `ImageViewer-<버전>.msi` (`src/ImageViewer.Installer/ImageViewer.wxs`, WiX Toolset v5), 그리고 배포 파일들의 확인값 `SHA256SUMS.txt` 도 함께 만든다.
- GitHub Actions (`.github/workflows/release.yml`): `v*.*.*` 태그를 푸시하거나 Actions 탭에서 수동 실행(`workflow_dispatch`)하면 테스트를 실행한 뒤 무설치(`-portable.exe`), 설치 파일(`-setup.exe`), MSI(`.msi`), `SHA256SUMS.txt` 를 자동으로 빌드해 GitHub Release 에 배포한다.

### 크로스 플랫폼 빌드 · 배포 (Windows / macOS / Linux)

`net10.0` 타겟과 `Magick.NET-Q8-AnyCPU`를 사용하므로 Windows에서도 macOS / Linux용 실행 파일을 크로스 컴파일할 수 있다 (`win-x64` 빌드 시에는 자동으로 OpenMP x64 빌드가 선택된다).

```bash
# Windows (x64)
dotnet publish src/ImageViewer/ImageViewer.csproj -c Release -r win-x64 --self-contained -o artifacts/publish/win-x64

# macOS (Apple Silicon)
dotnet publish src/ImageViewer/ImageViewer.csproj -c Release -r osx-arm64 --self-contained -o artifacts/publish/osx-arm64

# macOS (Intel)
dotnet publish src/ImageViewer/ImageViewer.csproj -c Release -r osx-x64 --self-contained -o artifacts/publish/osx-x64

# Linux (x64)
dotnet publish src/ImageViewer/ImageViewer.csproj -c Release -r linux-x64 --self-contained -o artifacts/publish/linux-x64

# Linux (ARM64)
dotnet publish src/ImageViewer/ImageViewer.csproj -c Release -r linux-arm64 --self-contained -o artifacts/publish/linux-arm64
```

- macOS에서 실행 권한이 필요하면 `chmod +x artifacts/publish/osx-arm64/ImageViewer` 후 실행한다.
- 단일 실행 파일로 묶으려면 위 명령에 `-p:PublishSingleFile=true` 를 추가한다.
- 탐색기 우클릭 메뉴 · 기본 앱 등록 · 자동 설치 업데이트는 Windows 전용 기능으로, macOS / Linux 에서는 자동으로 비활성화된다.

## 자동 업데이트

GitHub 저장소(`ImageViewer.csproj` 의 `UpdateRepository`, 현재 `edith2580/ImageViewer`)의 **최신 릴리스**로 업데이트한다.
저장소가 **공개(public)** 여야 한다 (비공개면 릴리스 정보를 읽지 못해 "최신 버전"으로 처리된다).

- 시작 5초 뒤, 하루 한 번 최신 릴리스를 확인한다 (설정 > 업데이트에서 끌 수 있음, 설정 > 정보 > **업데이트 확인** 으로 직접 확인).
  새 버전이 있으면 메인 창 오른쪽 위에 알림: **지금 업데이트** / **이 버전 건너뛰기** / 변경 내용 / 닫기.
- 받은 파일은 SHA-256 이 맞을 때만 쓴다. 값은 GitHub 가 계산한 파일 digest, 없으면 릴리스의 `SHA256SUMS.txt`.
  둘 다 없으면 설치하지 않고 다운로드 페이지를 안내한다.
- **설치 버전** (설치 폴더가 프로그램 추가/제거 정보의 InstallLocation 과 같음): `ImageViewer-x.y.z-setup.exe` 를 `%TEMP%` 에 받고,
  앱을 종료한 뒤 `setup.exe /S /RELAUNCH=<보던 경로>` 로 조용히 설치한다. 설치 프로그램은 앱이 끝나기를 기다렸다가 덮어쓰고
  다시 실행한다. 관리자 권한 창은 뜨지 않는다. 탐색기 메뉴는 이미 등록된 경우에만 다시 등록한다.
- **포터블** (단일 실행 파일): `ImageViewer-x.y.z-portable.exe` 를 exe 옆에 `ImageViewer.exe.new` 로 받고,
  `ImageViewer.exe` → `.old`, `.new` → `ImageViewer.exe` 로 바꿔 끼운 뒤 보던 경로로 다시 실행한다. 새 버전이 시작하면서 `.old` 를 지운다.
  파일 이름 · 경로가 그대로라 바로 가기와 탐색기 메뉴가 그대로 동작한다. 폴더에 쓸 수 없으면 다운로드 페이지를 안내한다.
- 개발 빌드(exe 옆에 ImageViewer.dll 이 있는 빌드)는 자동 설치하지 않고 다운로드 페이지만 연다.
  Debug 빌드는 환경 변수 `IMAGEVIEWER_UPDATE_FEED=http://127.0.0.1:8765/` 로 로컬 테스트 서버를 가리킬 수 있다 (`releases/latest` 를 읽는다).

릴리스 절차:

1. `ImageViewer.csproj` 의 `<Version>` 을 올리고 `build\publish.ps1` 실행.
2. GitHub 에서 태그 `v<버전>` (예: `v1.0.6`) 으로 릴리스를 만들고 (Pre-release 체크 해제),
   `artifacts\installer` 의 `ImageViewer-<버전>-setup.exe`, `ImageViewer-<버전>-portable.exe`, `SHA256SUMS.txt` 를 올린다.
   파일 이름은 바꾸지 않는다 (`-setup.exe` / `-portable.exe` 로 끝나는 파일을 찾는다).

## Windows 탐색기 연동

- 설치 프로그램의 "탐색기 우클릭 메뉴" 구성 요소, 또는 앱의 **설정 → Windows 탐색기 연동 → 등록** (HKCU, 관리자 권한 불필요)
- 명령행: `ImageViewer.exe --register-shell` / `--unregister-shell`
- 대상: 폴더, 폴더 빈 공간, ZIP / CBZ, 지원하는 모든 이미지 파일 (이미지 파일은 그 폴더를 열고 해당 이미지를 선택)
- 등록 위치: `Directory\shell`, `Directory\Background\shell`, `SystemFileAssociations\<확장자>\shell` (.zip, .cbz, .jpg, .png, .webp ...)
- 여러 항목을 선택했을 때는 메뉴가 나타나지 않는다 (`MultiSelectModel=Single`).
- 설치 프로그램은 설치한 `ImageViewer.exe --register-shell` 을 실행해 등록하고, 제거 시 `--unregister-shell` 로 지운다.
- Windows 11 에서는 클래식 메뉴(**추가 옵션 표시**, Shift+F10)에 표시된다.
- 메뉴 이름은 `ImageViewer에서 보기(3)`: Shift+우클릭으로 메뉴를 연 뒤 **3** 을 누르면 바로 실행된다.
  (이전 버전으로 등록했다면 설정에서 미등록으로 보이므로 **등록**을 한 번 더 누르거나 설치 프로그램으로 다시 설치)

### Windows 기본 앱

- 설정 창 왼쪽 아래 **기본 앱 설정...**: ImageViewer 를 Windows 기본 앱 후보로 등록하고 **설정 > 앱 > 기본 앱** 의 ImageViewer 화면을 연다.
  Windows 10 / 11 은 앱이 스스로 기본 앱이 되는 것을 막으므로, 그 화면에서 "기본값으로 설정" 또는 형식별로 직접 고른다.
- 등록 (HKCU, 관리자 권한 불필요): ProgID `ImageViewer.Image` · `ImageViewer.Archive`, 확장자별 `OpenWithProgids`,
  `Software\ImageViewer\Capabilities`, `Software\RegisteredApplications\ImageViewer`. 대상은 지원하는 모든 이미지 확장자와 .zip / .cbz.
- 제거 프로그램(`--unregister-shell`)이 탐색기 메뉴와 함께 지운다.

## 설정 파일

`%LOCALAPPDATA%\ImageViewer\settings.json` (없거나 손상되면 기본값 사용, 종료 시 저장).
로그는 `%LOCALAPPDATA%\ImageViewer\logs` 에 7일간 보관하며, 전체 경로 대신 파일명만 기록한다.

## 1차 버전 정책 (계획서 14장)

| 항목                    | 결정                                                                                                                     |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| 작은 이미지             | 보기 모드를 따름 (맞춤 모드는 확대, 원본 크기는 1:1)                                                                     |
| Fill 모드               | 비율을 유지한 채 이미지 전체가 창 안에 들어오는 가장 큰 크기 (잘리지 않음)                                               |
| 이미지 순환             | 기본 사용 (설정 가능)                                                                                                    |
| ZIP 암호                | 미지원 — 해당 항목에 안내 메시지 표시                                                                                    |
| GIF / WebP 애니메이션   | 프레임 수 · 길이와 관계없이 모든 프레임 재생, 필터 미적용. GIF 는 지연 0~10ms 프레임을 100ms 로 표시 (브라우저와 같음), WebP 는 파일의 지연을 그대로 사용 (동영상 변환 파일의 1ms 프레임 포함, 모든 프레임이 0~10ms 일 때만 100ms) |
| 다중 페이지 TIFF        | 첫 페이지부터 표시, PageUp / PageDown 으로 이동 (회전 · 반전 유지)                                                       |
| ICO                     | 가장 큰 프레임 표시                                                                                                      |
| 회전 · 반전             | 화면 표시에만 적용, 파일은 변경하지 않음, 다른 이미지로 이동하면 초기화                                                  |
| 폴더 감시               | 0.7초 동안 변경을 모아 한 번에 반영, 현재 이미지가 지워지면 같은 위치의 이미지 표시                                      |
| SVG · PDF · PS 등       | 미지원 (확장자를 바꿔도 헤더·포맷 검사로 거부)                                                                           |
| 마우스 휠               | 기본 이전/다음 이미지 (Ctrl+휠 확대·축소), 설정으로 바꿀 수 있음                                                         |
| 슬라이드쇼 중 수동 이동 | 타이머 재시작                                                                                                            |
| 중복 실행               | 다중 실행 허용 (탐색기에서 연 경로마다 새 창)                                                                            |
| 투명 이미지             | 배경색 위에 표시 (설정에서 체크무늬 배경 선택 가능)                                                                      |

## 표시 속도

- JPEG 는 화면에 필요한 크기 이상이 되는 가장 작은 비율(n/8)로 디코딩한다 (libjpeg 축소 디코딩).
  원본 크기 보기 · 확대 · 창 확대로 더 큰 해상도가 필요해지면 원본을 백그라운드에서 다시 읽어 바꿔 그린다.
- 화면보다 조금(60% 이상) 큰 이미지는 CPU 로 다시 줄이지 않고 화면 표시 단계(WPF 고품질 스케일링)에서 줄인다.
- Magick.NET 은 멀티코어(OpenMP) x64 빌드를 쓴다. 8000×6000 → 1280×960 축소가 단일 스레드 빌드의 1,027ms 에서
  218ms 로 빨라진다. (그래서 x64 전용)
- 이미 지나간 이미지의 디코딩 · 축소(중간에 멈출 수 없음)가 작업 자리를 차지하고 있으면, 지금 보려는 이미지는
  기다리지 않고 바로 시작한다. 큰 이미지 사이의 작은 이미지가 그 뒤에서 기다리던 문제를 없앤다.
- 표시에 0.3초 이상 걸린 이미지는 로그(%LOCALAPPDATA%\ImageViewer\logs)에 "느린 표시"로 단계별 시간을 남긴다.
- 이동 방향의 다음 두 장과 반대 방향 한 장을 미리 디코딩하고 현재 보기 조건으로 렌더링까지 해 둔다.
  빠르게 연속으로 넘기면(0.6초 이내) 진행 방향으로 네 장까지 미리 읽고, 두 장씩 병렬로 처리한다.
- 슬라이드쇼 재생 중에는 재생 순서(랜덤이면 시작할 때 섞은 목록)의 다음 이미지도 미리 읽는다.
- 미리 읽기는 별도 대기열에서 처리해 지금 보려는 이미지의 로딩을 막지 않는다. 미리 읽는 중인 이미지로 이동하면
  처음부터 다시 읽지 않고 그 결과를 이어받는다. 로딩 표시는 0.5초 이상 걸릴 때만 나타난다.
- 고화질이 0.25초 안에 준비되지 않으면(미리 읽지 않은 먼 이미지 · 아주 큰 이미지) 이미 만들어 둔 썸네일을
  흐리게 늘려 같은 위치 · 크기에 먼저 보여 주고, 고화질이 준비되면 바꾼다. 썸네일이 없거나 원본 크기 보기
  모드이면 이전 이미지를 그대로 둔다. 0.5초가 지나면 로딩 게이지도 함께 표시한다.

측정 (test.zip: JPEG 31장 · PNG 1장, 최대 2592×1728, 1280×800 창 · 폭 맞춤):

| 항목 | 1.0.1 | 현재 |
|---|---|---|
| 한 장 디코딩 + 렌더링 (평균 / 최대) | 186 / 475 ms | 36 / 95 ms |
| → 키 0.15초 간격 연속 이동 (평균 / 최대) | 183 / 284 ms | 20 / 49 ms |
| → 키 0.1초 간격 연속 이동 (평균 / 최대) | 209 / 305 ms | 22 / 53 ms |
| 랜덤 슬라이드쇼 전환 (평균 / 최대) | 107 / 297 ms | 15 / 21 ms |
| 랜덤 슬라이드쇼 중 0.15초 간격 키 이동 (평균 / 최대) | 191 / 333 ms | 22 / 49 ms |

전체 화면 (2560×1440): 이미지가 화면보다 작거나 비슷해 축소 디코딩을 할 수 없으므로 한 장 처리 시간은
평균 49ms · 최대 137ms 로 늘지만, 미리 읽기 덕분에 이동 지연은 창 모드와 같은 수준이다
(→ 키 0.1~0.3초 간격 평균 25~35ms, 랜덤 슬라이드쇼 전환 평균 16ms · 최대 30ms).

## 메모리 · 안전 한도

- 디코딩 전에 픽셀 수를 검사 (기본 150 MP, 설정 가능), ZIP 항목은 실제 압축 해제 바이트 수로 한도 검사 (기본 512 MB)
- 디코딩 결과(최대 8개)와 표시용 비트맵은 크기 제한 LRU 캐시, 썸네일은 최근 300개만 메모리에 유지
- 디코딩·리사이즈·필터는 백그라운드에서 수행하고, 오래된 요청 결과는 버전/취소 토큰으로 폐기
- GIF / WebP 애니메이션은 SkiaSharp 로 첫 프레임만 바로 읽고, 나머지 프레임은 재생하면서 차례로 디코딩한다.
  (ImageMagick 은 첫 프레임만 요청해도 모든 프레임을 풀어서, 1080×1920 · 267프레임 WebP 에 8.5초 · 1.8GB 가 들었다)
  - 모든 프레임이 512 MB 안에 들어가면(필요하면 화면에 필요한 크기까지 줄여서) 한 번만 디코딩해 보관한다.
  - 넘으면 재생 위치 앞쪽 128 MB 만큼만 미리 디코딩해 두고 지나간 프레임은 버린다 (위 WebP: 메모리 약 470 MB, CPU 약 5%).
  - 프레임 시각은 실제 경과 시간 기준으로 맞춰, 타이머 지연이 쌓여 재생이 느려지지 않게 한다.

## 프로젝트 구조

```text
src/ImageViewer/            Avalonia UI 앱 (Models, ViewModels, Views, Services, Helpers)
  Services/ImageSources/    IImageSource, FolderImageSource, ZipImageSource
  Services/Updates/         UpdateService (릴리스 확인 · 다운로드 · SHA-256), UpdateInstaller (설치 / 포터블 교체)
src/ImageViewer.Installer/  NSIS 설치 스크립트 (ImageViewer.nsi), WiX MSI 스크립트 (ImageViewer.wxs)
.github/workflows/          GitHub Actions 자동 빌드 · 릴리스 배포 (release.yml)
tests/ImageViewer.Tests/             단위 테스트 (배율 계산, 탐색, 슬라이드쇼, 설정, 단축키 등)
tests/ImageViewer.IntegrationTests/  통합 테스트 (폴더/ZIP 소스, 디코딩, 렌더링, 썸네일)
build/publish.ps1           게시 + 설치 프로그램 생성 스크립트
```

## GitHub Actions 릴리스 워크플로 가이드

`.github/workflows/release.yml` 워크플로는 Windows 환경(`windows-latest`)에서 전체 테스트를 실행한 뒤 아래 4개 배포 파일을 자동으로 빌드하고 GitHub Release에 등록한다.

| 파일 이름 | 설명 |
|---|---|
| `ImageViewer-<버전>-portable.exe` | 무설치 단일 실행 파일 (포터블 버전 자동 업데이트 대상) |
| `ImageViewer-<버전>-setup.exe` | NSIS 설치 프로그램 (설치 버전 자동 업데이트 대상) |
| `ImageViewer-<버전>.msi` | Windows Installer (MSI) 설치 패키지 |
| `SHA256SUMS.txt` | 위 배포 파일들의 SHA-256 무결성 해시 목록 |

---

### 방법 1: Git 태그를 푸시하여 자동 배포 (권장)

`v*.*.*` 형식의 태그를 원격 저장소에 푸시하면 GitHub Actions가 자동으로 실행되어 버전 동기화 → 빌드 → GitHub Release 생성 및 파일 업로드까지 완료한다.

```powershell
# 1. 변경 사항 커밋 및 main 브랜치 푸시
git add -A
git commit -m "Release v1.0.7"
git push origin main

# 2. 버전 태그 생성 및 푸시 (푸시 즉시 GitHub Actions 실행)
git tag -a v1.0.7 -m "ImageViewer v1.0.7"
git push origin v1.0.7
```

> **참고**: 워크플로가 실행될 때 태그 이름(`v1.0.7`)에서 버전(`1.0.7`)을 추출하여 `ImageViewer.csproj`, `app.manifest`, `ImageViewer.nsi`의 버전을 빌드 전에 자동으로 맞춰 주므로 태그만 푸시해도 해당 버전으로 빌드된다.

---

### 방법 2: GitHub 웹 페이지(Actions 탭)에서 수동 실행

명령행에서 태그를 푸시하지 않고 GitHub 웹 화면에서 직접 빌드를 실행할 수도 있다.

1. GitHub 저장소의 **Actions** 탭으로 이동한다.
2. 왼쪽 워크플로 목록에서 **Release** 를 선택한다.
3. 오른쪽 위의 **Run workflow** 버튼을 클릭한다.
4. **릴리스 태그** 입력란을 용도에 맞게 입력한 뒤 **Run workflow** (초록색 버튼)를 누른다:
   - **GitHub Release까지 바로 배포할 때**: `v1.0.7` 처럼 태그 이름을 입력한다. 해당 태그로 새 GitHub Release가 만들어지고 4개 파일이 첨부된다.
   - **릴리스 없이 빌드 파일만 테스트/확인할 때**: 입력란을 **비워 두고** 실행한다. `ImageViewer.csproj`의 현재 `<Version>` 값으로 빌드한 뒤 워크플로 실행 결과 하단의 **Artifacts** 섹션(`ImageViewer-<버전>-installers`)에 파일을 올려 주므로 다운로드하여 테스트할 수 있다.

---

### 방법 3: 로컬 PC에서 직접 빌드하여 수동 업로드

GitHub Actions를 거치지 않고 로컬 PC에서 직접 4개 파일을 생성하려면 아래 명령을 실행한다.

```powershell
# 1. 버전 수정 후 전체 빌드 (테스트 + 포터블 EXE + NSIS Setup EXE + WiX MSI + SHA256SUMS.txt)
powershell -ExecutionPolicy Bypass -File build\publish.ps1

# (선택 옵션)
# - 테스트 생략: powershell -ExecutionPolicy Bypass -File build\publish.ps1 -SkipTests
# - MSI 생략:   powershell -ExecutionPolicy Bypass -File build\publish.ps1 -SkipMsi
```

빌드가 완료되면 `artifacts\installer\` 폴더에 생성된 아래 파일들을 GitHub **Releases → Draft a new release** 화면에서 태그(`v<버전>`)와 함께 직접 업로드한다:
- `artifacts\installer\ImageViewer-<버전>-setup.exe`
- `artifacts\installer\ImageViewer-<버전>.msi`
- `artifacts\installer\ImageViewer-<버전>-portable.exe`
- `artifacts\installer\SHA256SUMS.txt`
