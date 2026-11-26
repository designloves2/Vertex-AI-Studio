# Vertex AI Studio (Image)

Vertex OpenAI Proxy(`http://127.0.0.1:3000`)를 통해 Google 이미지 모델(Nano Banana 등)로
대화하듯 이미지를 만들고 이어서 수정하는 Windows 앱입니다.

## 설치

1. [Releases](https://github.com/designloves2/Vertex-AI-Studio/releases/latest)에서 `VertexAIStudioApp-win-Setup.exe`를 받아 실행합니다.
   - 서명되지 않은 설치 파일이라 SmartScreen 경고가 나오면 **추가 정보 → 실행**을 누릅니다.
2. 설치가 끝나면 바탕화면과 시작 메뉴에 **Vertex AI Studio**가 생깁니다.
3. [Vertex OpenAI Proxy](https://github.com/designloves2/vertex-openai-proxy) 앱을 먼저 실행해 두세요.

- 업데이트: 앱이 시작할 때 GitHub Releases를 확인해 새 버전이 있으면 상단에 알려 줍니다.
  **업데이트 후 재시작**을 누르면 받아서 다시 시작합니다(설정 › 업데이트에서 끄거나 바로 확인 가능).
- 제거: Windows 설정 › 앱에서 제거합니다. 대화·이미지·설정은 `%LOCALAPPDATA%\VertexAIStudio`에 따로 남습니다.

## 주요 기능

- 대화 기록과 생성 이미지를 저장해 다음 날에도 이어서 수정, "이걸로 계속"으로 기준 이미지 지정
- 참조 이미지 첨부(드래그&드롭, Ctrl+V), 프롬프트 인핸스(기본 gemini-3.7-flash)
- `/compact` 또는 압축 버튼으로 대화 요약(컨텍스트 자동 압축 임계치 설정)
- 모델·비율(10종)·해상도(1K/2K/4K)·PNG/JPEG·인물 생성·사고 수준 등 Vertex 옵션
- Enter / Ctrl+Enter 전송 키 전환, 접을 수 있는 좌우 패널, 미리보기 휠 줌·드래그 이동·더블클릭 맞춤

## 개발

- .NET 8 SDK, WinUI 3 (Windows App SDK 1.6), unpackaged + self-contained
- `src/ImageStudio.Core` — UI 없는 로직(프록시 클라이언트, SQLite 저장소, 기록 구성, /compact)
- `src/ImageStudio.App` — WinUI 3 앱, `tests/ImageStudio.Tests` — xUnit
- 테스트: `dotnet test tests/ImageStudio.Tests`

## 릴리즈

1. `src/ImageStudio.App/ImageStudio.App.csproj`의 `<Version>`을 올립니다.
2. `.\release.ps1 -Publish` — 테스트, 게시, Velopack 설치 파일과 델타 패키지 생성, GitHub 릴리즈 `v<버전>` 업로드.
   (필요: `dotnet tool install -g vpk --version 1.2.161`, `gh auth login`)

설치 폴더(`%LOCALAPPDATA%\VertexAIStudioApp`)는 제거할 때 지워지므로 데이터 폴더와 이름이 달라야 합니다.
