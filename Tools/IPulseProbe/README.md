# iPulse Probe — iCore LED 컨트롤러 진단 (읽기 전용)

9호기 조명 컨트롤러(iCore iPulse `1P1S-100A`) 2대가 **COM12 한 포트에 sID1/sID2** 로 물려 있다.
앱에 배선하기 전에 아래 세 가지를 실물로 확정하기 위한 도구다.

1. **Modbus RTU 로 응답하는가** — 함수코드가 FC3(Holding)인지 FC4(Input)인지
2. **32bit 파라미터의 워드 순서** — Duration/Period/Trigger Delay 는 주소가 2씩 띄어진 32bit
3. **시간 파라미터의 스케일** — 1us 단위인지 0.1us 단위인지

**쓰기는 하지 않는다.** 조명이 켜지거나 LED 가 손상될 동작은 이 도구에 없다.
(`Rated Current`·`Brightness` 계열은 매뉴얼에 주소가 없어 추측 접근 자체를 금지한다 —
정격 전류를 초과해 쓰면 LED 가 파손된다)

## 실행 전 준비

1. **iPulse Configurator 에서 [Port Close]** — COM 포트는 한 프로세스만 열 수 있다.
   열어둔 채로 실행하면 "다른 프로그램이 점유 중" 으로 끝난다.
2. 컨피규레이터 화면의 **Duration / Period / Trigger Delay 표시값을 적어 둔다.**
   raw 값과 비교해야 2·3번이 판정된다.

## 실행

```
IPulseProbe.exe                          # COM12, sID 1·2, 115200 8N1
IPulseProbe.exe --port COM12 --units 1,2 --baud 115200 --timeout 1000
```

self-contained 라 .NET 설치 없이 그대로 실행된다(`publish\IPulseProbe.exe`).

## 판정 방법

| 확인 | 방법 |
|---|---|
| 워드 순서 | 컨피규레이터 표시값과 일치하는 쪽(`HighFirst` / `LowFirst`). 값이 작아 상위 워드가 0 이면 값이 든 워드 위치로 판별 |
| 스케일 | 화면 `10.0us` ↔ raw `100` → 0.1us 분해능(`RegisterScale=10`) / raw `10` → 1us(`=1`) |
| Operation(0x300) | `0`=OFF(소등), `1`=Continuous(상시 점등), `2`=Pulse |

## 레지스터 맵 (매뉴얼 rev03 GUI 라벨 괄호)

| 주소 | 항목 | 비고 |
|---|---|---|
| 0x200 | Slave Address | DIP-SW 로 설정(0~14) |
| **0x300** | **Operation** | 0=OFF / 1=Continuous / 2=Pulse |
| 0x301 | Trigger Input | 0=Internal, 1=DigitalIO, 2=RJ45, 3=SoftTrigger, 4=ChannelPort |
| 0x302 | Trigger Activation | 0=Rising, 1=Falling |
| 0x303 | Trigger Output | 0=LEDSync, 1=Bypass, 2=Error, 3=Low, 4=High |
| 0x304 | Trigger Output Inverter | |
| 0x305 | Sequence Mode | 0=OFF, 1=Sequence |
| 0x310 | Duration [us] | 32bit |
| 0x312 | Period [us] | 32bit |
| **0x314** | **Trigger Delay [us]** | 32bit. LabVIEW `iCore_Set Delay time` 대상 |
| 0x316 | Maximum Voltage [V] | 32bit |
| 0x318 | Multi Trigger | 32bit |
| 0x100 / 0x102 / 0x104 / 0x106 / 0x108 | Trigger_Count / Error_Count / AlarmCode / SequenceIndex / Period Limit | 읽기 전용, 32bit |

※ 매뉴얼에 `SEQ_Count(0x307)` 과 `Auto Voltage Adjustment(0x307)` 이 **같은 주소로 표기**돼 있다(오타로 보임).
  둘 다 건드리지 않으므로 실사용에는 영향 없다.

※ `LED Enable` 채널이 꺼져 있으면 Operation 을 Continuous 로 바꿔도 **불이 안 들어온다.**
  컨피규레이터에서 LED1 체크 여부를 먼저 확인할 것.

## 빌드

솔루션(`IJPSystem.slnx`)에는 넣지 않았다 — 실장 진단용이라 앱 빌드에 딸려갈 이유가 없다.

```
dotnet publish Tools/IPulseProbe -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o Tools/IPulseProbe/publish
```
