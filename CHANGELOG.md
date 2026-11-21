# 변경 내역 (Changelog)

## v1.1.2

### 새 기능 및 개선
- **Avalonia UI 12.1.6 업그레이드 및 Linux Wayland 지원**
  - Avalonia(`11.3.23` → `12.1.6`) 및 SkiaSharp(`3.119.1` → `3.119.4`) 업그레이드
  - `Avalonia.Wayland` 백엔드 추가: Linux 환경에서 `WAYLAND_DISPLAY` 감지 시 네이티브 Wayland 백엔드로 자동 실행
  - Avalonia 12 창 장식 API(`WindowDecorations`) 적용
- **하단 바 UI 간소화**
  - 하단 버블 바에서 슬라이드쇼 재생/간격 컨트롤 제거 (상단 메뉴 및 `Space` 단축키는 유지)

### 버그 수정
- **Linux / Wayland 이미지 목록(`Enter`) 재오픈 문제 수정**
  - 이미지 목록 창에서 `Enter`로 선택을 확정할 때 부모 창으로 키 이벤트가 전달되어 목록이 즉시 다시 열리던 문제 수정
- **초기 썸네일 바 표시 누락 수정**
  - 이미지를 처음 열었을 때 하단 썸네일 목록(`ShowSingleThumbnails` / `ShowComicThumbnails`)이 즉시 표시되지 않던 바인딩 알림 누락 수정
