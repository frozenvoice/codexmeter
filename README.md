# ProMeter

Windows tray monitor for ChatGPT Pro quota and model usage.

ProMeter는 현재 PC에서 발생한 요청만 세지 않습니다. ChatGPT 계정 conversation history를 **재구성 입력**으로 읽어 회사 PC, 집 PC, Android 앱에서 사용한 계정 단위 GPT Pro 사용량을 추정합니다. 일치하는 서버 quota counter만 Authoritative입니다.

```text
GPT PRO
██████████████░░░░░░ 34 / 50

Used       34
Remaining  16
Reset      3d 11h
```

## 설치

### 릴리스 실행 파일

`dotnet publish` 후 아래 경로의 `prometer.exe`를 실행합니다.

```text
src\ProMeter\bin\Release\net8.0-windows\win-x64\publish\prometer.exe
```

WebView2 Runtime이 필요합니다. Windows 11과 최신 Edge가 있으면 보통 이미 설치되어 있습니다. 없다면 [Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)을 설치하세요.

관리자 권한은 필요하지 않습니다.

## 비공식 엔드포인트와 약관

ProMeter는 ChatGPT 웹사이트가 쓰는 **비공식(unofficial) 내부 엔드포인트**를 사용합니다. 공개 OpenAI API가 아니며 예고 없이 바뀔 수 있습니다. 프로그래밍 방식 history 접근은 지원되지 않으며, 적용 중인 ChatGPT 약관과 충돌할 수 있습니다. 동기화를 켜기 전에 현재 약관을 확인하세요. 공식 ChatGPT Data Export import는 실시간은 아니지만 더 낮은 위험의 대안입니다. 수동 Sync now를 쓴다고 해서 이 연동이 공식 지원이거나 약관 준수로 바뀌지는 않습니다.

Windows 시작 시 실행과 자동 history 동기화는 **명시적 opt-in**입니다. 새 설치에서는 둘 다 꺼져 있습니다.

브라우저 companion은 **임베디드 OAuth를 피하기 위한 경로**입니다. ChatGPT 내부 엔드포인트 연동을 공식으로 만들지는 않습니다. 공식 ChatGPT Data Export import는 실시간은 아니지만 더 낮은 위험의 대안입니다.

전송 방식은 조용히 바뀌지 않습니다. Browser companion / WebView2 / Data Export 중 하나를 직접 고릅니다.

## 브라우저 companion (권장)

Google / Microsoft / Apple 로그인은 WebView2에서 **지원되지 않습니다**. 일반 Chrome 또는 Edge ChatGPT 세션과 Manifest V3 확장을 사용하세요. ProMeter는 user-agent를 위장하거나 쿠키 DB를 읽지 않습니다.

1. `publish\win-x64\extension` 또는 저장소의 `extension\` 폴더를 Chrome/Edge에서 **Load unpacked**로 로드합니다.
2. `chrome://extensions`에서 확장 ID를 복사합니다.
3. ProMeter Settings에 확장 ID를 넣고 **Register Chrome/Edge native host**를 누릅니다. 공식 Native Messaging 레지스트리만 사용합니다 (`Software\Google\Chrome\NativeMessagingHosts\com.prometer.bridge`, `Software\Microsoft\Edge\NativeMessagingHosts\com.prometer.bridge`).
4. Chrome/Edge에서 chatgpt.com에 로그인합니다.
5. 확장 팝업에서 **Connect to ProMeter**를 누릅니다.
6. Welcome 또는 tray에서 **Run first manual sync** / **Sync now**를 누릅니다. 로그인만으로는 history 스캔이 시작되지 않습니다.

네이티브 호스트는 `prometer-companion-host.exe`입니다. Chrome Native Messaging은 실행 파일 인자를 허용하지 않아 트레이 앱과 분리되어 있습니다. 확장은 `https://chatgpt.com/*`에서만 승인된 상대 경로를 fetch하고, 본문을 sanitize한 뒤 Native Messaging으로 전달합니다. 쿠키와 access token은 Windows 앱으로 보내지 않습니다.

Whale은 Chromium이지만 ProMeter가 레지스트리 위치를 만들지 않습니다. Chrome/Edge 공식 등록을 쓰거나 Whale 자체 문서를 따르세요.

## WebView2 fallback

이메일/비밀번호처럼 WebView2에서 실제로 되는 인증만 선택적 fallback입니다. Google/Microsoft/Apple WebView 로그인은 unsupported입니다. 세션은 `%LOCALAPPDATA%\ProMeter\webview`에만 유지됩니다.

## 최초 설정

1. ProMeter를 실행하면 Welcome 창이 열립니다.
2. 비공식 엔드포인트와 약관 안내를 확인합니다.
3. Browser companion / WebView2 fallback / Data Export 중 연결 방식을 고릅니다.
4. Pro $100 / Pro $200 / Custom 중 플랜을 고릅니다.
5. 원하는 경우에만 Start with Windows / Automatic synchronization을 켭니다. 둘 다 기본은 꺼져 있습니다.
6. Sign in / Connect와 **Run first manual sync**는 별도 동작입니다.

Chrome/Whale/Edge cookie DB를 읽거나 복호화하지 않습니다.

## Tray 사용법

기본 모드는 **Tray Only**입니다. 작업표시줄에 텍스트를 붙이지 않고 알림 영역의 아이콘만 사용합니다.

- 좌클릭: flyout
- 우클릭: Open ProMeter / Sync now / Open Login / Settings / View statistics / Start with Windows / About / Exit
- 아이콘: 남은 Pro 횟수 또는 progress ring
- 앱을 닫아도 tray에서 계속 동작합니다. 종료는 **Exit**만 해당합니다.

선택적으로 Settings에서 아주 작은 always-on-top 위젯을 켤 수 있습니다. Windows 11 taskbar 내부 삽입은 하지 않습니다.

## Quota 의미

기본 preset은 OpenAI Help Center 문서 *GPT-5.6 and GPT-6 Pro in ChatGPT* (2026)에 따릅니다.

| Plan | Weekly Pro | 비고 |
| --- | --- | --- |
| Pro $100 | 50 | GPT-6 Pro와 GPT-5.6 Sol Pro가 주간 한도를 공유 |
| Pro $200 | 200 | Sol Pro 일일 170, 합산 일일 200 |
| Custom | 사용자 지정 | 모든 숫자는 Settings에서 수정 가능 |

확인되지 않은 reasoning quota는 `Limit: Unknown`으로 표시합니다. 없는 분모를 추정하지 않습니다.

서버에서 reset timestamp를 얻으면 그대로 사용하고, 없으면 Settings의 reset anchor로 주간 period를 계산한 뒤 `(estimated)`를 붙입니다.

## 계정 history 재구성

Conversation history는 재구성 입력입니다. 로컬 브라우저 요청 가로채기는 사용하지 않습니다. 서버 used + limit + reset이 일치할 때만 카운트를 Authoritative로 표시합니다.

포함:

- 일반 Chat
- Archived Chat
- Projects 내부 Chat

제외 / 복구 불가:

- Temporary Chat
- 삭제된 conversation
- history에 남지 않은 실패 요청

한 번의 사용자 요청은 `request_id` 기준으로 1회로 계산합니다. hidden assistant / reasoning / tool call / final response가 같아도 중복 카운트하지 않습니다.

## Temporary / Delete limitation

Coverage는 `Good` / `Estimated` / `Incomplete` 등으로 표시됩니다. 공식 quota API 값이 없으면 `Authoritative` 또는 `Exact`라고 표시하지 않습니다.

## Privacy

- 대화 본문을 DB에 저장하지 않습니다
- access token / cookie / session token을 DB·로그·export에 넣지 않습니다
- telemetry / analytics SDK가 없습니다
- ChatGPT 서버 외에 네트워크를 쓰지 않습니다
- 데이터는 `%LOCALAPPDATA%\ProMeter\`에만 있습니다

## Troubleshooting

| 증상 | 확인 |
| --- | --- |
| Authentication required | companion: Chrome/Edge에서 Connect 후 Sync now. WebView2: tray → Open Login |
| Rate limited | 잠시 후 Sync now. conversation body는 동시성 1 |
| Provider schema mismatch | ChatGPT JSON이 바뀐 상태. 로그의 parsing error 확인 |
| Offline | chatgpt.com 연결 확인 |
| 카운트가 공식 UI와 다름 | Temporary/삭제/미포함 branch 한계. Coverage 창 확인 |
| 아이콘이 안 보임 | Windows 알림 영역 overflow에서 ProMeter 표시 |

Settings → **Open logs**로 rolling log를 엽니다.

## Development build

```powershell
$env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
dotnet restore
dotnet build
dotnet test
```

## Release build

```powershell
dotnet publish src\ProMeter\ProMeter.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\win-x64
```

실행 파일: `publish\win-x64\prometer.exe`

## 기술 선택

Windows 전용이라 **.NET 8 + WPF + WebView2 + SQLite**를 사용합니다. tray와 WebView2 연동이 안정적이고 Electron보다 메모리 사용량이 낮습니다.

## License

MIT
