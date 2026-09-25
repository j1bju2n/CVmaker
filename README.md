# CV!mania — osu!mania Cut Version maker

osu! stable에서 **지금 선택한 비트맵**을 실시간으로 읽어 와서, 곡의 파형과 BPM 변속 구간·북마크를 보여주고,
원하는 부분을 들으면서 구간을 고르면 **Cut Ver. 오디오 + 타이밍이 맞춰진 .osu**를 바로 만들어 주는 Windows 프로그램입니다.
osu!mania 전용입니다(스크롤 속도 처리 등이 mania 기준). CV는 Cut Ver.의 약자입니다.

기존 방식(오디오 추출 → 외부 편집기에서 자르기 → 변속 지점 계산 → 오프셋 재확인 → SV 다시 맞추기)을 한 화면에서 끝냅니다.
UI는 영어/한국어(메뉴 `언어`)를 지원합니다. Made by Tamania
Claude Fable 5.1모델을 사용하여 제작했습니다.

## 다운로드
[Releases](https://github.com/j1bju2n/CVmania/releases)에서 `CVmania-vX.Y.Z-win-x64.zip`을 받아 압축을 풀고 `CVmania.exe`를 실행하면 됩니다(설치·런타임 불필요, Windows 10/11 64비트).
처음 실행할 때 Windows SmartScreen 경고가 뜨면 `추가 정보 → 실행`을 누르세요(서명되지 않은 개인 배포 프로그램이라 뜨는 안내입니다).

**English:** CV!mania (formerly CVmaker) reads the beatmap currently selected in osu! stable, shows the waveform with its BPM sections and bookmarks, lets you pick regions while listening, and exports a cut version (audio + re-timed `.osu`) whose offset stays exact and whose osu!mania scroll speed stays 1.0x at the cut's main BPM. Download the zip from Releases, unzip, run `CVmania.exe`; switch the UI language from the `Language` menu. Drag on the waveform to select, `Enter` to keep, `P` to preview, `Export...` to write the files into your Songs folder (press `F5` in osu!).

## 왜 오프셋이 안 틀어지나
- osu! stable은 `bass.dll`(BASS 2.4)로 오디오를 재생합니다. CV!mania도 **같은 BASS로 디코딩**해서 자르기 때문에 샘플 0번이 게임의 0 ms와 같습니다.
- MP3는 LAME(NAudio.Lame)으로 인코딩하고 항상 LAME 태그(인코더 지연 정보)를 기록합니다. BASS는 이 태그를 읽어 지연을 제거하므로 왕복 오차가 0 샘플입니다(테스트로 검증).
- 내보낼 때마다 결과 파일을 다시 BASS로 디코딩해 렌더링한 PCM과 상호상관으로 비교합니다. 어긋나면 자동으로 보정해 다시 인코딩하고, 결과를 창에 표시합니다.
- 잘라낸 구간마다 그 지점에 유효한 빨간 줄(BPM/박자)을 위상 그대로 다시 넣고, 이전 구간과 격자가 자연스럽게 이어지면 중복 빨간 줄을 만들지 않습니다. SV·볼륨·kiai 상태도 구간 시작에 이어 붙입니다.
- 구간들은 항상 잘린 지점끼리 정확히 맞닿습니다. 양 끝 처리는 두 가지입니다. **페이드**: 시작은 구간 안쪽 처음 N ms가 무음에서 점점 커지고, 끝은 구간 뒤의 소리 N ms가 점점 0으로 줄며 다음 구간 위에 겹칩니다. **확장**: 구간 밖의 소리 N ms를 그대로(100%) 이웃 구간 위에 겹칩니다. 첫 구간 앞과 마지막 구간 뒤에서만 소리가 실제로 붙고, 겹치는 부분은 소프트 리미터로 클리핑을 막습니다.

## 스크롤 속도가 왜 바뀌고, 어떻게 맞추나
osu!mania의 스크롤 속도는 `SV × (구간 BPM ÷ 주 BPM)`입니다. 여기서 **주 BPM**은 곡 선택 화면에 괄호로 표시되는 값으로, 첫 빨간 줄은 0 ms부터, 마지막 빨간 줄은 마지막 노트까지 재서 **가장 오래 쓰인 BPM**입니다.
구간을 잘라내면 이 주 BPM이 바뀔 수 있고(예: Twin Bloom [Ramification] 145–290 (290) → 컷 145–218 (188)), 그러면 남은 모든 구간의 체감 속도가 한꺼번에 달라집니다.
내보내기의 **스크롤 속도** 항목이 기본값 **변속 없이 유지**이면, 컷 버전의 주 BPM 구간에서 실제로 쓰이는 SV가 정확히 1.0x가 되도록 **모든 SV에 같은 배율**을 곱하고, 초록 줄이 없는 빨간 줄에는 그 배율의 초록 줄을 넣습니다(빨간 줄은 SV를 1로 되돌리기 때문).
매퍼가 의도한 상대적 변속(느려지는 구간 등)은 그대로 남습니다. 대화상자에 원본/컷의 주 BPM과 배율이 표시되고, **그대로 내보내기**를 고르면 SV를 건드리지 않습니다.

## 작업이 사라지지 않게: 자동 저장·복원과 작업 파일
- 구간을 바꿀 때마다(추가·분할·삭제·페이드) 그 곡의 작업이 `%LOCALAPPDATA%\CVmania\sessions\`에 **자동 저장**됩니다. 저장 단위는 곡(폴더 + 오디오 파일)이라 같은 곡의 다른 난이도로 옮겨도 작업이 그대로 이어집니다. 구간 목록 오른쪽에 마지막 자동 저장 시각이 표시됩니다.
- 다른 곡을 열었다가(또는 프로그램을 껐다가) **같은 곡을 다시 선택하면 마지막 작업이 그대로 복원**되고 상태줄에 "이 곡의 마지막 작업을 복원했습니다"가 뜹니다.
- 내보낸 직후 osu!에서 F5 → 컷 버전을 선택해 테스트해도, 그 파일은 이 프로그램이 만든 것으로 기억되어 있어 **작업창이 바뀌지 않습니다**(상태줄에 안내). 컷 버전 파일 자체를 편집하고 싶으면 `파일 > .osu 열기`로 직접 여세요.
- `파일 > 작업 저장...`(Ctrl+S)으로 `*.cvmania.json` 파일에 따로 저장하고, `작업 불러오기...`(Ctrl+L)로 되돌릴 수 있습니다. 파일에는 비트맵 경로가 들어 있어 다른 곡이 열려 있어도 그 비트맵을 먼저 연 뒤 구간을 적용합니다.

## 사용법
1. osu!를 켜고 곡 선택 화면(또는 에디터의 곡 선택)에서 맵을 고르면 자동으로 불러옵니다(`osu! 선택 따라가기`). `.osu` 파일을 창에 끌어다 놓거나 `파일 > .osu 열기`로 직접 열 수도 있습니다.
2. `스냅:` 줄에서 격자를 고릅니다(마디, 1/1 … 1/16, 끄기). 선 색은 osu! 에디터와 같습니다(1/2 빨강, 1/3 보라, 1/4 파랑, 1/6·1/12 노랑 계열, 1/8 노랑, 1/16 보라).
   빨간 줄(BPM 변경)은 빨간 세로선, 원본의 **북마크**는 파란 세로선(위에 작은 삼각형)으로 보입니다. `보기 > 북마크 표시`로 끄고 켤 수 있습니다.
3. 파형을 드래그해 선택하고 `Enter`로 **유지할 구간**에 추가합니다(초록). 구간 밖은 모두 잘립니다.
   - 이미 있는 구간 **안에서** 드래그하면 주황색으로 표시됩니다. `Enter`를 누르면 그 부분이 별도 구간으로 **분할**되고, `Delete`를 누르면 그 부분이 **삭제**됩니다.
   - 구간을 클릭(또는 목록에서 선택)하면 양 끝에 `◀ in 0 ms` / `out 0 ms ▶` 핸들이 뜹니다. 클릭하면 팝업에서 `확장` 체크(끄면 페이드)와 ms(0~1000, 실제로 있는 소리 길이까지)를 정합니다.
   - 확정하지 않은 선택은 우클릭으로 지웁니다.
   - 시작·끝이 1/1·1/2 박자선이나 BPM 변경선에서 벗어나 있으면(1/3, 1/4 같은 잔 스냅 위치나 격자 밖) "의도한 것인가요?" 확인창이 뜹니다. 벗어난 거리를 ms와 현재 스냅 칸 수로 알려줍니다.
4. `P`로 잘린 결과를 미리 듣습니다. 재생 위치가 유지 구간 안이면 그 지점부터, 아니면 처음부터 재생됩니다(원곡 기준 위치도 표시).
5. `내보내기...`를 누르면 설정 창이 뜹니다: 형식(MP3 192k 등), 이음새 크로스페이드, 제목 접미사, 난이도 이름, **제작자 이름(기존 매퍼 이름이 채워져 있고 자유롭게 수정)**, **채보 내용(노트 유지 / 빈 난이도 / 오디오만)**, **기존 북마크 유지** 여부, **스크롤 속도(변속 없이 유지 / 그대로 내보내기)**, 출력 폴더, 배경 복사, 오프셋 검증.
   노트를 유지할 때 잘린 지점 때문에 롱노트 끝이 다음 노트와 겹치거나 롱노트가 1/4박보다 짧아지면, 저장 전에 "겹치는 노트 확인" 창이 뜹니다. `▸ 위치 보기`를 펼치면 출력 시각·원곡 시각·열·내용이 스크롤 목록으로 나오고, 계속 진행하거나 취소할 수 있습니다(자동 수정은 하지 않습니다).
   위쪽 전체 파형(오버뷰)은 클릭하거나 드래그하면 보기 창이 마우스를 따라 움직입니다.
   기본 출력은 `Songs\{Artist} - {Title} (Cut Ver.)` 폴더이며, osu! 곡 선택 화면에서 `F5`를 누르면 보입니다.

### 단축키
| 키 | 동작 |
|---|---|
| Space / Esc | 재생·일시정지 / 정지 |
| ← → (Shift: 마디, Ctrl: 10 ms) | 한 박자 이동 |
| I / O | 선택 시작 / 끝 = 현재 위치 |
| Enter | 선택을 구간으로 추가, 구간 안이면 분할 |
| Delete | 구간 안 선택 부분 삭제, 또는 선택한 구간 삭제 |
| 우클릭 | 확정하지 않은 선택 지우기 |
| L / P / Z | 선택 반복 / 미리듣기 ↔ 원곡 / 전체 보기 |
| 휠, Ctrl+휠 / Shift+휠 / Alt+휠 | 시간 확대·축소 / 가로 스크롤 / 파형 세로 배율 |
| 가운데 드래그 | 이동 |
| Ctrl+O / F5 / Ctrl+E | .osu 열기 / 다시 불러오기 / 내보내기 |
| Ctrl+S / Ctrl+L | 작업 저장 / 작업 불러오기 (`*.cvmania.json`) |

### 명령줄
`CVmania.exe "경로\맵.osu"` 로 특정 맵을 바로 열 수 있고(osu! 추적은 꺼짐), `--out "폴더"`로 출력 폴더, `--regions 3000-13000,20000-30000`으로 구간, `--lang ko|en`으로 언어를 미리 지정할 수 있습니다.
`--screenshot "폴더"`는 개발용으로, 창들을 PNG로 저장하고 종료합니다(`--sessions-dir`, `--session-check "다른맵.osu"`를 붙이면 자동 저장·복원 흐름도 점검해 `session-check.txt`에 기록). 파형 아래 노트 표시와 북마크 표시는 `보기` 메뉴에서 끌 수 있습니다.

## 빌드
```bash
dotnet build CVmania.sln -c Debug
dotnet test tests/CVmania.Core.Tests
dotnet run --project src/CVmania.App
dotnet publish src/CVmania.App -c Release -r win-x64   # publish/CVmania.exe 단일 파일 (bass.dll, libmp3lame 포함, 약 67 MB)
```
요구 사항: Windows 10/11 x64, .NET 8 SDK(빌드 시). 배포본은 self-contained라 런타임 설치가 필요 없습니다. 설정은 `%LOCALAPPDATA%\CVmania\settings.json`에 저장됩니다(1.0.x의 `CVmaker` 설정은 처음 실행할 때 자동으로 옮겨집니다).

## 구조
```
src/CVmania.Core   .osu 파서(줄 보존), 타이밍 모델(주 BPM 계산 포함), 컷 플래너(.osu 재작성, SV 정규화, 북마크), 렌더러(구간별 페이드·크로스페이드), 인코더(MP3/OGG/WAV), 오프셋 검증
src/CVmania.App    WPF UI: osu! 메모리 감시(OsuMemoryDataProvider), BASS 플레이어, 파형/격자/구간/북마크 뷰, 내보내기 창, 다국어
tests/             xunit — 파서·타이밍·플래너·렌더러 단위 테스트 + 인코딩→BASS 디코딩 왕복 테스트
tools/MemProbe     osu! 메모리 읽기 확인용 콘솔
tools/AudioProbe   BASS 디코딩/인코더 진단, 콘솔 내보내기(export) 도구
```

## 참고한 프로젝트
- [Leinadix/companella](https://github.com/Leinadix/companella) — 현재 맵 감지 구조(메모리 읽기 + 창 제목 폴백)
- [FunOrange/osu-trainer](https://github.com/FunOrange/osu-trainer) — 곡 선택 화면 폴링 방식
- [Piotrekol/ProcessMemoryDataFinder](https://github.com/Piotrekol/ProcessMemoryDataFinder) — `OsuMemoryDataProvider` (GPL-3.0)
- [ppy/osu](https://github.com/ppy/osu) — 주 BPM(가장 오래 쓰인 BPM) 계산 규칙과 mania 스크롤 속도 공식의 참고
- BASS — [un4seen.com](https://www.un4seen.com/) (비상업 무료)

## 라이선스
`OsuMemoryDataProvider`가 GPL-3.0이므로 이 프로젝트도 GPL-3.0입니다. BASS는 비상업 용도 무료, LAME은 LGPL입니다.
