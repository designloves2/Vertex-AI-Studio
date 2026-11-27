# Vertex AI Studio (Image)

**English** · [한국어](#한국어)

A Windows app for **creating and iteratively editing images in a chat** with Google Vertex AI image models
(the Nano Banana family). [Vertex OpenAI Proxy](https://github.com/designloves2/vertex-openai-proxy) is built in,
so **signing in with your Google account** is all it takes — no separate server to run.

![Main window](docs/screenshots/maximized.png)

## Install

1. Download `VertexAIStudioApp-win-Setup.exe` from the [latest release](https://github.com/designloves2/Vertex-AI-Studio/releases/latest) and run it.
   - The installer is not code-signed; if SmartScreen warns, choose **More info → Run anyway**.
   - .NET, the Windows App SDK and the Node.js runtime are bundled — nothing else to install (Windows 10 1809+ / 11).
2. **Vertex AI Studio** appears on the desktop and in the Start menu.
3. On first launch the **Get started with your Google account** page opens. Sign in in the browser window; everything else is automatic.

| Step | What happens |
|---|---|
| 1. Google Cloud CLI | Installed per user if missing (no admin rights) |
| 2. Google sign-in | Browser sign-in → Application Default Credentials |
| 3. Project | Picked automatically if you have one, otherwise chosen from a list |
| 4. Vertex AI permission | `roles/aiplatform.user` checked and granted if missing |
| 5. Vertex AI API | `aiplatform.googleapis.com` enabled |
| 6. Embedded proxy | Started on `127.0.0.1:3001` |

- You need a Google Cloud project with billing. The setup page tells you when billing or a project is missing.
- On a PC that already has Vertex OpenAI Proxy, its `.env` (project, region, model) is imported on first launch.
- To run the setup again: ⚙ Settings › **Embedded proxy · Google account** › **Google account setup**.

## Features

**Chat-style generation and editing**
- Conversations and images are saved, so you can keep editing after restarting the app or the next day.
- Edit the previous result ("make it brighter", "change only the background"); **Continue with this** pins the base image.
- Reference images: drag & drop, Ctrl+V or the attach button.
- Per-image save / copy / open folder / regenerate. Preview opens fit-to-window with wheel zoom, drag to pan, double-click to fit.

**Context management**
- The conversation is re-sent with every request, with only the latest N images (default 3) inlined.
- `/compact` (or the **Compact** button) summarises the conversation and keeps the 2 latest images; it also runs automatically at a threshold (default 80%).

**Prompt enhance**
- An LLM (default `gemini-3.7-flash`, configurable) rewrites your prompt into a detailed image prompt; both versions are shown.

**Vertex image options**
- Models: Nano Banana 2.1 / 2 / 2 lite / Pro … listed as `friendly name (model id)`
- Output (image + text / image only), thinking level (low/medium/high), media resolution
- 10 aspect ratios (auto = 2:3, 1:1, 2:3, 3:2, 3:4, 4:3, 4:5, 5:4, 9:16, 16:9, 21:9), output resolution 1K/2K/4K, PNG/JPEG
- Person generation (all ages / adults / none), region, max output tokens, streaming
- Options a model rejects are dropped and retried, and listed above the result as "options not applied".

**UI**
- Both the conversation list (left) and the image settings panel (right) fold away; their state is remembered.
- Top-bar dropdowns change model, ratio, resolution, format and enhance directly — kept in sync with the panel in real time.
- Send key: Enter or Ctrl+Enter (the other one inserts a newline).

![Top-bar dropdowns and settings panel](docs/screenshots/chips-sync.png)

## Updates and uninstall

- On start the app checks GitHub Releases and shows a banner when a newer version exists; **Update and restart** downloads it and relaunches.
  Turn auto-check off or check now under ⚙ Settings › Updates.
- Uninstall from Windows Settings › Apps. Conversations, images and settings (`%LOCALAPPDATA%\VertexAIStudio`) are kept.

## Where data lives

| Location | Contents |
|---|---|
| `%LOCALAPPDATA%\VertexAIStudio\studio.db` | Conversations and messages (SQLite) |
| `%LOCALAPPDATA%\VertexAIStudio\images\` | Original images as received (never re-encoded) |
| `%LOCALAPPDATA%\VertexAIStudio\settings.json` | App settings |
| `Pictures\VertexAIStudio\` (configurable) | Results saved in the chosen output format |
| `%APPDATA%\gcloud\application_default_credentials.json` | Google sign-in (gcloud ADC) |
| `%LOCALAPPDATA%\VertexAIStudioApp\` | Installed app (removed on uninstall) |

## Development

- .NET 8 SDK, WinUI 3 (Windows App SDK 1.6), unpackaged + self-contained, Velopack (install/update)
- `src/ImageStudio.Core` — UI-free logic: proxy client, SQLite store, history builder, `/compact`, embedded proxy host, Google setup
- `src/ImageStudio.App` — the WinUI 3 app
- `tests/ImageStudio.Tests` — xUnit (`dotnet test tests/ImageStudio.Tests`)
- `proxy/` — embedded proxy (a copy of vertex-openai-proxy's `index.js`; `UPSTREAM_COMMIT` records the source commit)
  - Run `npm ci` in `proxy` once before a dev run (node is taken from PATH)
  - Refresh: `.\proxy\sync-from-upstream.ps1 [-Upstream <path to the proxy repo>]`

## Release

1. Bump `<Version>` in `src/ImageStudio.App/ImageStudio.App.csproj`.
2. `.\release.ps1 -Publish` — tests → publish → bundle the embedded proxy (+ node.exe) → Velopack installer and delta packages → GitHub release `v<version>`
   - Requires Node.js, `dotnet tool install -g vpk --version 1.2.161`, `gh auth login`
   - Without `-Publish` it only builds the installer into `artifacts\releases`.

The install folder (`%LOCALAPPDATA%\VertexAIStudioApp`) is deleted on uninstall, so it must differ from the data folder.

## License

The embedded proxy (`proxy/`) is under its upstream MIT license (`proxy/LICENSE`).

---

## 한국어

Google Vertex AI 이미지 모델(Nano Banana 계열)로 **대화하듯 이미지를 만들고 이어서 수정**하는 Windows 앱입니다.
[Vertex OpenAI Proxy](https://github.com/designloves2/vertex-openai-proxy)가 내장되어 있어, 별도 프로그램 없이
**Google 계정 로그인만으로** 바로 사용할 수 있습니다.

![메인 화면](docs/screenshots/maximized.png)

### 설치

1. [최신 릴리즈](https://github.com/designloves2/Vertex-AI-Studio/releases/latest)에서 `VertexAIStudioApp-win-Setup.exe`를 받아 실행합니다.
   - 코드 서명이 없는 설치 파일이라 SmartScreen 경고가 나오면 **추가 정보 → 실행**을 누릅니다.
   - .NET과 Windows App SDK, Node.js 런타임이 함께 들어 있어 따로 설치할 것이 없습니다(Windows 10 1809 이상 / 11).
2. 설치가 끝나면 바탕화면과 시작 메뉴에 **Vertex AI Studio**가 생깁니다.
3. 처음 실행하면 **Google 계정으로 시작하기** 화면이 열립니다. 브라우저 창에서 로그인만 하면 나머지는 자동입니다.

| 단계 | 내용 |
|---|---|
| 1. Google Cloud CLI 준비 | 없으면 사용자 계정에 자동 설치 (관리자 권한 불필요) |
| 2. Google 계정 로그인 | 브라우저에서 로그인 → Application Default Credentials 생성 |
| 3. 프로젝트 선택 | 프로젝트가 하나면 자동, 여러 개면 목록에서 선택 |
| 4. Vertex AI 권한 | `roles/aiplatform.user` 확인, 없으면 부여 |
| 5. Vertex AI API | `aiplatform.googleapis.com` 사용 설정 |
| 6. 내장 프록시 시작 | `127.0.0.1:3001`에서 실행 |

- Google Cloud 프로젝트와 결제 계정이 필요합니다. 결제가 연결되지 않았거나 프로젝트가 없으면 설정 화면이 안내합니다.
- 기존 Vertex OpenAI Proxy가 설치된 PC라면 그 `.env`의 프로젝트·리전·모델 설정을 처음 실행할 때 자동으로 가져옵니다.
- 설정을 다시 하려면 ⚙ 설정 › **내장 프록시 · Google 계정** › **Google 계정 설정**을 누릅니다.

### 주요 기능

**대화형 생성·수정**
- 대화 기록과 생성 이미지가 저장되어 앱을 다시 켜도, 다음 날에도 이어서 수정합니다.
- "더 밝게", "배경만 바꿔줘"처럼 이전 결과를 기준으로 수정하고, **이걸로 계속**으로 기준 이미지를 지정합니다.
- 참조 이미지 첨부: 드래그&드롭, Ctrl+V, 첨부 버튼.
- 이미지별 저장 / 복사 / 폴더 열기 / 다시 생성. 미리보기는 화면 맞춤, 휠 줌, 드래그 이동, 더블클릭 맞춤.

**컨텍스트 관리**
- 요청마다 대화 기록을 다시 보내되, 최근 이미지 N장(기본 3)만 실제 이미지로 포함합니다.
- `/compact` 명령이나 **압축** 버튼으로 대화를 요약해 컨텍스트를 줄입니다(기준 이미지 2장 유지). 임계치(기본 80%)에서 자동 압축.

**프롬프트 인핸스**
- 입력한 프롬프트를 LLM(기본 `gemini-3.7-flash`, 변경 가능)이 상세한 이미지 프롬프트로 다듬어 보냅니다. 원문과 강화문을 함께 표시합니다.

**Vertex 이미지 옵션**
- 모델: Nano Banana 2.1 / 2 / 2 lite / Pro 등 — 목록은 `별칭 (모델 ID)`로 표시
- 출력(이미지+텍스트 / 이미지), 사고 수준(낮음/중간/높음), 미디어 해상도
- 가로세로 비율 10종(자동=2:3, 1:1, 2:3, 3:2, 3:4, 4:3, 4:5, 5:4, 9:16, 16:9, 21:9), 출력 해상도 1K/2K/4K, PNG/JPEG
- 인물 생성(모든 연령 / 성인 / 허용 안 함), 리전, 출력 토큰 한도, 스트리밍
- 모델이 받아들이지 않는 옵션은 빼고 다시 요청하며, 결과 위에 "적용되지 않은 옵션"으로 알려 줍니다.

**화면**
- 왼쪽 대화 목록과 오른쪽 이미지 설정 패널을 각각 접을 수 있습니다(상태 기억).
- 상단 드롭다운에서 모델·비율·해상도·형식·인핸스를 바로 변경 — 접힌 패널과 실시간으로 동기화됩니다.
- 전송 키: Enter 또는 Ctrl+Enter 중 선택(나머지는 줄바꿈).

![상단 드롭다운과 설정 패널](docs/screenshots/chips-sync.png)

### 업데이트와 제거

- 앱이 시작할 때 GitHub Releases를 확인해 새 버전이 있으면 상단에 알려 줍니다. **업데이트 후 재시작**을 누르면 받아서 다시 시작합니다.
  ⚙ 설정 › 업데이트에서 자동 확인을 끄거나 바로 확인할 수 있습니다.
- 제거는 Windows 설정 › 앱에서 합니다. 대화·이미지·설정(`%LOCALAPPDATA%\VertexAIStudio`)은 제거해도 남습니다.

### 데이터 위치

| 위치 | 내용 |
|---|---|
| `%LOCALAPPDATA%\VertexAIStudio\studio.db` | 대화·메시지 (SQLite) |
| `%LOCALAPPDATA%\VertexAIStudio\images\` | 받은 원본 이미지 (재인코딩 없이 보관) |
| `%LOCALAPPDATA%\VertexAIStudio\settings.json` | 앱 설정 |
| `사진\VertexAIStudio\` (변경 가능) | 출력 형식으로 저장한 결과 이미지 |
| `%APPDATA%\gcloud\application_default_credentials.json` | Google 로그인 정보 (gcloud ADC) |
| `%LOCALAPPDATA%\VertexAIStudioApp\` | 설치된 앱 (제거 시 삭제) |

### 개발

- .NET 8 SDK, WinUI 3 (Windows App SDK 1.6), unpackaged + self-contained, Velopack(설치·업데이트)
- `src/ImageStudio.Core` — UI 없는 로직: 프록시 클라이언트, SQLite 저장소, 기록 구성, `/compact`, 내장 프록시 실행, Google 설정
- `src/ImageStudio.App` — WinUI 3 앱
- `tests/ImageStudio.Tests` — xUnit (`dotnet test tests/ImageStudio.Tests`)
- `proxy/` — 내장 프록시(vertex-openai-proxy의 `index.js` 사본, `UPSTREAM_COMMIT`에 기준 커밋)
  - 개발 실행 전 `proxy` 폴더에서 `npm ci` 한 번 (node는 PATH에서 찾음)
  - 갱신: `.\proxy\sync-from-upstream.ps1 [-Upstream <프록시 저장소 경로>]`

### 릴리즈

1. `src/ImageStudio.App/ImageStudio.App.csproj`의 `<Version>`을 올립니다.
2. `.\release.ps1 -Publish` — 테스트 → 게시 → 내장 프록시(+node.exe) 포함 → Velopack 설치 파일·델타 패키지 생성 → GitHub 릴리즈 `v<버전>` 업로드
   - 필요: Node.js, `dotnet tool install -g vpk --version 1.2.161`, `gh auth login`
   - `-Publish` 없이 실행하면 `artifacts\releases`에 설치 파일만 만듭니다.

설치 폴더(`%LOCALAPPDATA%\VertexAIStudioApp`)는 제거할 때 지워지므로 데이터 폴더와 이름이 달라야 합니다.

### 라이선스

내장 프록시(`proxy/`)는 원본 저장소의 MIT 라이선스(`proxy/LICENSE`)를 따릅니다.
