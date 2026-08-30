이 폴더(native)에 실장비용 벤더 네이티브 DLL 을 넣어두면 설치 파일에 함께 포함됩니다.
(파일이 없으면 설치 스크립트가 자동으로 건너뜁니다.)

예:
  - ComiEcatSdk.dll   (Comizoa EtherCAT — IO/Motion 실장 시)
  - niimaqdx.dll 관련 (NI IMAQdx — 실카메라 사용 시. 단, NI 런타임은 NI 설치관리자로 별도 설치 권장)

현재 들어 있는 것 (2026-08-29):
  - PrinterInterface.dll / PrintEngine.dll  (Meteor x86 네이티브)

    관리 래퍼(PrinterInterfaceCLS.dll, MeteorCLS.dll)는 csproj 참조라 publish 산출물에
    이미 들어가고, 설치 스크립트가 publish 폴더를 통째로 넣으므로 여기 둘 필요가 없다.
    네이티브만 여기 둔다 — 제어PC 의 Meteor 설치 경로가 PATH 에 잡혀 있는지 확인되지 않아,
    앱 폴더에 두어 로드 경로를 확정한다(MVS SDK·OpenCvSharp 에서 같은 함정을 겪었다).

    출처: lib\Meteor\x86\  (제어PC 의 C:\Program Files\Meteor Inkjet\Meteor\Api\x86 사본)
    관리 래퍼와 <같은 빌드>여야 한다 — 한쪽만 갱신하지 말 것.

주의:
  - NI-DAQmx / IMAQdx, Comizoa 런타임은 각 벤더 설치관리자로 대상 PC에 별도 설치해야
    정상 동작하는 경우가 많습니다. 여기 DLL 만 넣는다고 런타임이 갖춰지는 것은 아닙니다.
  - 32/64비트(x64) 를 프로그램과 맞출 것.
