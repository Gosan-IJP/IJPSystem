# SerialScan — 어느 COM 포트에 Modbus 장치가 붙어 있나 (읽기 전용)

새 제어PC 마다 **DMD(메니스커스)** 와 **iCore(스트로브)** 가 몇 번 포트에 잡히는지 다르다.
USB 시리얼은 꽂는 순서에 따라 번호가 바뀌어서 **다른 호기의 설정을 옮겨 적을 수 없다.**

2026-09-07 11호기가 그랬다:

- `MeniscusConfig.json` 의 **COM6** → 실제로는 `Intel(R) Active Management Technology - SOL`.
  메인보드가 만드는 가상 포트라 **열리기는 하는데 아무도 대답하지 않는다.**
  로그에는 `DMD 연결됨 — COM6` 뒤에 `Read 실패: timed out` 이 찍혀, 배선 문제로 오인하기 쉽다.
- `StrobeConfig.json` 의 **COM12** → 장치 관리자에 아예 없었다.

장치 관리자만 봐서는 `USB Serial Port(COM9)` 와 `USB Serial Port(COM10)` 중 어느 쪽이
DMD 인지 알 수 없다. **두드려 봐야 안다.**

## 실행

`publish\SerialScan.exe` 는 self-contained 라 **.NET 설치 없이** 그대로 돌아간다
(제어PC 에는 .NET 이 없다 — 앱이 self-contained 이므로).

**앱을 먼저 닫을 것.** COM 포트는 한 프로세스만 열 수 있다.

```
SerialScan.exe                          # 모든 포트 · 9600/19200/38400/115200 · UnitId 1~4
SerialScan.exe --ports COM8,COM9,COM10  # USB 포트만 (빠르다)
SerialScan.exe --bauds 4800,57600       # 기본 목록에 없으면 넓힌다
SerialScan.exe --units 1-8
SerialScan.exe --addr 0x300             # 읽어 볼 레지스터 (기본 0)
SerialScan.exe --timeout 300
```

조합 하나에 timeout 만큼 걸린다. 포트 3 × 보레이트 4 × UnitId 4 = 48회 ≈ 15초.

## 결과 읽는 법

| 나온 것 | 뜻 |
|---|---|
| `홀딩[0x0] = 1013` | **장치 확정** — 포트·보레이트·UnitId 가 전부 맞다 |
| `응답함(Modbus 예외: ...)` | **장치는 있고 주소만 다르다** — 이것도 확실한 증거다 |
| 아무것도 안 나옴 | 그 자리엔 없다 |

두 번째가 중요하다. `MeniscusConfig.json` 의 `PressureReadAddress` 는 아직 미검증
placeholder 라, **주소를 몰라도 포트는 찾을 수 있다.**

찾은 값을 `Config\MeniscusConfig.json`(DMD) 또는 `Config\StrobeConfig.json`(iCore) 에 적는다.

## DMD 인가 iCore 인가

둘 다 잡히면 iCore 쪽은 **실제로 불을 켜 보면** 확실하다:

```
IPulseProbe.exe --port COM9 --light 1 1     # 1=Continuous(상시 점등)
IPulseProbe.exe --port COM9 --light 1 0     # 끄기
```

LED 가 켜지는 쪽이 스트로브다. 자세한 것은 `Tools\IPulseProbe\README.md`.

## 하지 않는 것

**쓰기를 하지 않는다.** 모르는 장치의 모르는 레지스터에 값을 넣으면 되돌리기 어렵다.
읽기만으로 포트·보레이트·UnitId 는 전부 판정된다.
