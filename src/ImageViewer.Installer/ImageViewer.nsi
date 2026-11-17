; ImageViewer 설치 프로그램 (NSIS 3, Unicode)
;
; 빌드: build\publish.ps1  (게시 후 자동으로 makensis 실행)
;   또는 makensis /DVERSION=1.0.6 /DPUBLISH_DIR=..\..\artifacts\publish\win-x64 ImageViewer.nsi
;
; - 현재 사용자 설치 (관리자 권한 불필요): %LOCALAPPDATA%\Programs\ImageViewer
; - 탐색기 우클릭 메뉴 등록은 구성 요소 페이지에서 선택 (사용자 동의)
;   설치한 ImageViewer.exe --register-shell 을 실행해 HKCU\Software\Classes 아래에 등록한다.
;   (앱 설정 화면의 등록 기능과 같은 코드 · 같은 키를 쓰므로 등록 대상이 한곳에서 관리된다)
;     Directory\shell\ImageViewer                   폴더          "%1"
;     Directory\Background\shell\ImageViewer        폴더 빈 공간  "%V"
;     SystemFileAssociations\.zip | .cbz\shell\...  ZIP / CBZ     "%1"
;     SystemFileAssociations\.jpg | .png | ...\shell\ImageViewer   지원하는 모든 이미지 확장자 "%1"
;   여러 항목을 선택했을 때는 메뉴를 보이지 않는다 (MultiSelectModel=Single).
; - 제거 시 위 키와 제거 정보, 설정 화면에서 등록한 Windows 기본 앱 후보(ProgID · Capabilities)를 삭제한다. 사용자 설정(%LOCALAPPDATA%\ImageViewer)은 남겨 둔다.
; - Windows 11 에서는 클래식 메뉴("추가 옵션 표시", Shift+F10)에 나타난다.
; - 자동 업데이트: 앱이 새 설치 파일을 받아 "setup.exe /S /RELAUNCH[=경로]" 로 실행한다.
;   앱이 완전히 끝날 때까지 기다렸다가 덮어쓰고, 설치가 끝나면 앱을 다시 실행한다.
;   조용한 설치에서는 탐색기 메뉴를 이미 등록해 둔 경우에만 다시 등록한다 (사용자가 끈 메뉴를 되살리지 않는다).

Unicode true
ManifestDPIAware true

!ifndef VERSION
  !define VERSION "1.0.8"
!endif
!ifndef PUBLISH_DIR
  !define PUBLISH_DIR "..\..\artifacts\publish\win-x64"
!endif

!define APP_NAME     "ImageViewer"
!define APP_EXE      "ImageViewer.exe"
!define MENU_TEXT    "ImageViewer에서 보기"
!define UNINST_KEY   "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"
!define CLASSES      "Software\Classes"

Name "${APP_NAME}"
OutFile "..\..\artifacts\installer\${APP_NAME}-${VERSION}-setup.exe"
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
InstallDirRegKey HKCU "${UNINST_KEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "${APP_NAME} ${VERSION}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1042 "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=1042 "FileDescription" "${APP_NAME} 설치 프로그램"
VIAddVersionKey /LANG=1042 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1042 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1042 "LegalCopyright" "GPL-3.0"

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"

!define MUI_ICON   "..\ImageViewer\Resources\ImageViewer.ico"
!define MUI_UNICON "..\ImageViewer\Resources\ImageViewer.ico"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_SMALLDESC
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "${APP_NAME} 실행"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\..\LICENSE"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Korean"

; ---------------------------------------------------------------------------
!macro UnregisterVerb KEY
  DeleteRegKey HKCU "${CLASSES}\${KEY}\shell\${APP_NAME}"
!macroend

; 연결 정보가 바뀌었음을 탐색기에 알린다 (SHCNE_ASSOCCHANGED)
!macro NotifyShell
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
!macroend

; ---------------------------------------------------------------------------
Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "${APP_NAME} 은(는) 64비트 Windows 에서만 실행할 수 있습니다."
    Abort
  ${EndIf}
FunctionEnd

; 제거할 때 설치 폴더를 통째로 지우므로, 비어 있지 않은 다른 폴더(예: 바탕 화면)에는 설치하지 못하게 한다.
; 새 폴더, 빈 폴더, 이전에 ImageViewer 를 설치한 폴더만 허용 (그 밖에는 [다음] 버튼이 비활성화된다).
Function .onVerifyInstDir
  IfFileExists "$INSTDIR\${APP_EXE}" allowed
  FindFirst $0 $1 "$INSTDIR\*.*"
  loop:
    StrCmp $1 "" empty
    StrCmp $1 "." next
    StrCmp $1 ".." next
    FindClose $0
    Abort
  next:
    FindNext $0 $1
    Goto loop
  empty:
    FindClose $0
  allowed:
FunctionEnd

; 실행 중인 ImageViewer 가 끝날 때까지 기다린다 (실행 중인 exe 는 덮어쓸 수 없다).
; 자동 업데이트(조용한 설치)는 앱이 종료되는 동안 최대 20초 기다리고,
; 직접 설치할 때는 잠시 기다린 뒤 닫아 달라고 안내한다.
Function WaitForAppExit
  IfFileExists "$INSTDIR\${APP_EXE}" 0 done
  StrCpy $R9 0
  ${If} ${Silent}
    StrCpy $R7 40
  ${Else}
    StrCpy $R7 4
  ${EndIf}
  check:
    ClearErrors
    FileOpen $R8 "$INSTDIR\${APP_EXE}" a
    IfErrors locked
    FileClose $R8
    Goto done
  locked:
    IntOp $R9 $R9 + 1
    IntCmp $R9 $R7 timeout wait timeout
  wait:
    Sleep 500
    Goto check
  timeout:
    IfSilent done
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "${APP_NAME} 이(가) 실행 중입니다.$\r$\n프로그램을 모두 닫은 뒤 [다시 시도]를 눌러 주세요." IDRETRY retry
    Abort
  retry:
    StrCpy $R9 0
    Goto check
  done:
FunctionEnd

Section "${APP_NAME} (필수)" SecApp
  SectionIn RO
  SetShellVarContext current
  Call WaitForAppExit
  SetOutPath "$INSTDIR"
  File /r "${PUBLISH_DIR}\*.*"

  WriteUninstaller "$INSTDIR\uninstall.exe"
  CreateShortCut "$SMPROGRAMS\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"

  ; 프로그램 추가/제거 정보
  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayIcon" '"$INSTDIR\${APP_EXE}",0'
  WriteRegStr   HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr   HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" "$0"
SectionEnd

Section "탐색기 우클릭 메뉴에 '${MENU_TEXT}' 추가" SecShell
  ; 폴더 · ZIP · 이미지 파일 메뉴를 앱이 등록한다 (HKCU, 탐색기 알림 포함).
  ; 자동 업데이트(조용한 설치)에서는 이미 등록되어 있을 때만 새 버전으로 다시 등록한다.
  ReadRegStr $1 HKCU "${CLASSES}\Directory\shell\${APP_NAME}" ""
  ${If} ${Silent}
  ${AndIf} $1 == ""
    DetailPrint "탐색기 메뉴가 등록되어 있지 않아 건너뜀"
  ${Else}
    ExecWait '"$INSTDIR\${APP_EXE}" --register-shell' $0
    ${If} $0 != 0
      DetailPrint "탐색기 메뉴 등록 실패 (코드 $0)"
    ${EndIf}
  ${EndIf}
SectionEnd

Section /o "바탕 화면 바로 가기" SecDesktop
  SetShellVarContext current
  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
SectionEnd

; 자동 업데이트: /RELAUNCH[=경로] 가 있으면 설치 뒤 앱을 다시 실행한다 (보던 이미지 · ZIP 경로를 넘긴다).
Function .onInstSuccess
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/RELAUNCH" $R1
  IfErrors done
  StrCpy $R2 $R1 1
  ${If} $R2 == "="
    StrCpy $R1 $R1 "" 1
  ${EndIf}
  ${If} $R1 == ""
    Exec '"$INSTDIR\${APP_EXE}"'
  ${Else}
    Exec '"$INSTDIR\${APP_EXE}" "$R1"'
  ${EndIf}
  done:
FunctionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecApp}     "${APP_NAME} 프로그램 파일 (.NET 런타임 포함)"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecShell}   "폴더, ZIP / CBZ, 이미지 파일의 우클릭 메뉴에 '${MENU_TEXT}' 를 추가합니다. (현재 사용자 레지스트리)"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "바탕 화면에 바로 가기를 만듭니다."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---------------------------------------------------------------------------
Section "Uninstall"
  SetShellVarContext current

  ; 등록한 모든 메뉴(이미지 확장자 포함)를 앱의 해제 기능으로 제거한다.
  ; 실행 파일이 없거나 실패한 경우를 대비해 폴더 · ZIP 키는 아래에서 한 번 더 지운다.
  IfFileExists "$INSTDIR\${APP_EXE}" 0 +2
    ExecWait '"$INSTDIR\${APP_EXE}" --unregister-shell'

  !insertmacro UnregisterVerb "Directory"
  !insertmacro UnregisterVerb "Directory\Background"
  !insertmacro UnregisterVerb "SystemFileAssociations\.zip"
  !insertmacro UnregisterVerb "SystemFileAssociations\.cbz"
  !insertmacro NotifyShell

  Delete "$SMPROGRAMS\${APP_NAME}.lnk"
  Delete "$DESKTOP\${APP_NAME}.lnk"
  ; 설치 시 빈 폴더 / ImageViewer 전용 폴더만 허용했으므로 폴더째 삭제한다.
  IfFileExists "$INSTDIR\${APP_EXE}" 0 +2
    RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "${UNINST_KEY}"
SectionEnd
