# macOS 앱 서명(Code Signing) 및 공증(Notarization) 가이드

GitHub Actions로 빌드된 macOS용 `ImageViewer.app` / `ImageViewer.dmg`를 다운로드했을 때 **"Apple은 ‘ImageViewer.app’에 사용자의 Mac에 손상을 입히거나 사용자의 개인정보에 침입할 수 있는 악성 코드가 없음을 확인할 수 없습니다."** 경고가 뜨지 않도록 서명·공증하는 절차입니다.

---

## 1. 원인 요약 (키체인 확인 결과)

1. **인증서 종류 차이 (`Apple Development` vs `Developer ID Application`)**
   - 현재 키체인에 설치된 `Apple Development: Kang Dongho (L4MC8QZS4Z)` 인증서는 **로컬 개발·디버깅 전용**입니다.
   - 인터넷(GitHub Releases)을 통해 외부 배포하고 Apple 공증(`notarytool`)을 통과하려면 반드시 **`Developer ID Application`** 인증서가 필요합니다.
2. **실제 Apple Team ID 확인**
   - `L4MC8QZS4Z`는 개인 개발자 인증서 ID(`CN`)이며, 유료 계정의 실제 **Team ID(`OU`)는 `GMXNVH8YU9`**입니다.
   - `.github/workflows/release.yml`에는 `APPLE_TEAM_ID: GMXNVH8YU9`로 설정되어 있습니다.
3. **GitHub Actions Secrets 미등록**
   - GitHub Actions 클라우드 서버는 로컬 Mac의 키체인에 접근할 수 없으므로, `.p12` 인증서와 앱 전용 암호를 **GitHub Repository Secrets**에 등록해야 서명·공증 단계가 실행됩니다.

---

## 2. 설정 절차 (순서대로 진행)

### 1단계: Xcode에서 `Developer ID Application` 인증서 발급 및 `.p12` 내보내기

1. **Xcode** 실행 → 상단 메뉴 **Xcode > Settings... (`⌘,`) > Accounts** 탭으로 이동합니다.
2. 유료 계정(`Kang Dongho`, Team ID `GMXNVH8YU9`)을 선택하고 우측 하단 **Manage Certificates...** 버튼을 클릭합니다.
3. 좌측 하단 **`+`** 버튼을 클릭한 뒤 **`Developer ID Application`**을 선택해 생성합니다.
   - 목록에 `Developer ID Application: Kang Dongho` 인증서가 추가됩니다.
4. 생성된 **`Developer ID Application`** 항목을 **우클릭 → Export Certificate...** 를 클릭합니다.
5. 파일 이름을 `DeveloperID.p12`로 지정하고, 사용할 **내보내기 암호(비밀번호)**를 입력해 저장합니다.

> **확인 명령어 (터미널)**
>
> ```bash
> security find-identity -v -p codesigning
> ```
>
> 출력 결과에 `"Developer ID Application: Kang Dongho (GMXNVH8YU9)"`가 표시되면 정상입니다.

---

### 2단계: `.p12` Base64 변환 및 Apple 앱 전용 암호 발급

1. **`.p12` 파일을 Base64 문자열로 변환**하여 클립보드에 복사합니다.

   ```bash
   base64 -i ~/Desktop/DeveloperID.p12 | pbcopy
   ```

   _(파일을 다른 경로에 저장했다면 해당 경로를 지정하세요.)_

2. **Apple 앱 전용 암호 발급**
   - [account.apple.com](https://account.apple.com) 로그인 → **로그인 및 보안 > 앱 전용 암호(App-Specific Passwords)** 클릭
   - 새 암호 생성(이름 예: `ImageViewer-Notary`) 후 발급된 `xxxx-xxxx-xxxx-xxxx` 형식의 암호를 복사해 둡니다.

---

### 3단계: GitHub 저장소 Secrets 등록

GitHub 저장소의 **Settings > Secrets and variables > Actions > New repository secret**에서 아래 4개 항목을 등록합니다.

| Secret 이름             | 설명 및 입력 값                                         |
| :---------------------- | :------------------------------------------------------ |
| `MACOS_CERT_P12_BASE64` | 2단계에서 복사한 `DeveloperID.p12`의 Base64 문자열      |
| `MACOS_CERT_PASSWORD`   | 1단계에서 `.p12` 내보낼 때 설정한 암호                  |
| `APPLE_ID`              | 유료 Apple Developer 계정 이메일 주소                   |
| `APPLE_APP_PASSWORD`    | 2단계에서 발급받은 앱 전용 암호 (`xxxx-xxxx-xxxx-xxxx`) |

---

### 4단계: 릴리스 실행

Secrets 등록 후 태그를 푸시하거나 GitHub Actions에서 **Release** 워크플로우를 실행하면 자동으로 다음 과정이 수행됩니다.

1. 임시 키체인에 `Developer ID Application` 인증서 로드
2. 모든 `.dylib`, `ImageViewer` 실행 파일, `ImageViewer.app`에 Hardened Runtime(`build/entitlements.plist`) + 타임스탬프 서명
3. `xcrun notarytool submit --wait`으로 `.app` 공증 후 `xcrun stapler staple` 적용 → `.tar.gz` 생성
4. `.dmg` 생성 및 서명 → `xcrun notarytool submit --wait`으로 `.dmg` 공증 후 `xcrun stapler staple` 적용

---

## 3. 참고: 공증 전 임시 실행 방법

아직 공증되지 않은 `ImageViewer.app`을 Mac에서 바로 실행하려면 아래 두 방법 중 하나를 사용할 수 있습니다.

- **시스템 설정에서 허용**: 앱 실행 시도 후 **시스템 설정 > 개인정보 보호 및 보안** 하단에서 **"ImageViewer.app이(가) 차단됨" → [그래도 열기]** 클릭
- **터미널에서 Quarantine 속성 제거**:
  ```bash
  xattr -cr /Applications/ImageViewer.app
  ```
