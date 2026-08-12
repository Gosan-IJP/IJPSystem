# Drop Watcher 검사용 샘플 이미지

드랍와처(Drop Watcher) 화면이 실제 카메라 연동 전, **실측 Raw 이미지**로 화면/검사 로직을
확인할 수 있도록 하는 샘플 이미지 폴더입니다.

## 사용법

아래 파일명으로 Raw 검사 이미지를 이 폴더에 넣으면, 드랍와처 화면이 자동으로 이 이미지를 사용합니다.

```
Config/Samples/DropWatcher_Raw.png
```

- 지원 포맷: PNG / BMP / JPG (WPF BitmapImage 로딩 가능 포맷)
- 파일이 있으면: 화면 진입 및 Measure Velocity / Time Interval Measure 시 이 이미지를 표시
- 파일이 없으면: 기존대로 가상 비전 드라이버가 합성 이미지를 캡쳐

## 참고

- DEBUG 빌드: 프로젝트 루트의 `Config/Samples/` 를 우선 사용
- RELEASE 빌드: 실행 파일 옆 `Config/Samples/` 사용
- 경로 해석은 `PathUtils.GetConfigPath("Samples/DropWatcher_Raw.png")` 규칙을 따름
