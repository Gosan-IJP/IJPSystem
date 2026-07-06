이 폴더(native)에 실장비용 벤더 네이티브 DLL 을 넣어두면 설치 파일에 함께 포함됩니다.
(파일이 없으면 설치 스크립트가 자동으로 건너뜁니다.)

예:
  - ComiEcatSdk.dll   (Comizoa EtherCAT — IO/Motion 실장 시)
  - niimaqdx.dll 관련 (NI IMAQdx — 실카메라 사용 시. 단, NI 런타임은 NI 설치관리자로 별도 설치 권장)

주의:
  - NI-DAQmx / IMAQdx, Comizoa 런타임은 각 벤더 설치관리자로 대상 PC에 별도 설치해야
    정상 동작하는 경우가 많습니다. 여기 DLL 만 넣는다고 런타임이 갖춰지는 것은 아닙니다.
  - 32/64비트(x64) 를 프로그램과 맞출 것.
