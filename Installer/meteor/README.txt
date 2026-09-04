Meteor PCC 펌웨어 — 설치본 동봉분
=================================

PCC-E 는 전원이 들어오면 스스로 부팅하지 못한다. PC 에 붙어서
SoC 애플리케이션과 FPGA 이미지를 내려받아야 비로소 살아난다.
그 파일들이 없으면 PCC 는 34초마다 접속을 시도했다 끊기기만 하고,
로그에는 이렇게만 남는다:

    *** Can't find SocApp image file '...\Config\ApplicationImages\SocApp'
    *** Failed to find Rbf folder '...\IJPSystem\'
    # PCC:256 Client [192.168.2.2] Disconnected

2026-09-04 11호기 브링업에서 이것 때문에 반나절을 썼다. 그래서 동봉한다.


원본
----
C:\Program Files\Meteor Inkjet\Meteor\Config\ApplicationImages\
C:\Program Files\Meteor Inkjet\Meteor\Config\Rbf\
(Meteor SDK 4.9.33800.14 · 파일 날짜 2025-08-01)


왜 전부가 아니라 이것만인가
---------------------------
벤더 Rbf\ 폴더는 826MB 다 — 지원하는 모든 헤드·모든 PCC 세대의
FPGA 이미지가 전부 들어 있다. PULSE 는 EPSON S800 + PCC-E 하나뿐이라
그 조합에 해당하는 두 개만 가져왔다.

  Rbf\PccE_ES800.rbx      7.0MB  PCC-E 본체 FPGA
  Rbf\HDC_ES800.rbx       147KB  헤드 드라이버 카드 FPGA

ApplicationImages\ 는 통째로 넣었다(1.4MB 밖에 안 된다).
  SocApp                         PCC-E 용 SoC 애플리케이션 ← 이게 핵심
  Pcc2E_PrintApp.elf             PCC2-E 용
  Pcc3E_PrintApp.elf             PCC3-E 용
  PhoenixBootMicroUpgradeApp.rbx 부트 마이크로 업그레이드

★헤드나 PCC 세대가 바뀌면 여기서 끝난다. S3200 으로 가면
  PccE_ES3200.rbx + HDC_ES3200.rbx 를, 진짜 PCC2-E 하드웨어를 달면
  pcc2e_ES800-revB.rbx 를 같은 원본 폴더에서 더 가져와야 한다.
  파일명 규칙: PccE_<헤드>.rbx / pcc2e_<헤드>-revB.rbx / HDC_<헤드>.rbx


설치 위치가 두 군데인 이유
--------------------------
엔진이 두 파일을 서로 다른 기준으로 찾는다. 위 오류 메시지가 그렇게 말한다.

  ApplicationImages  →  {app}\Config\ApplicationImages   (cfg 폴더의 부모 기준)
  Rbf                →  {app}\Rbf                        (PrintEngine.dll 기준)
  Rbf                →  {app}\Config\Rbf                 (보험. 아래 참고)

11호기에서는 Rbf 를 두 군데 모두에 복사한 뒤 성공했기 때문에, 둘 중
어느 쪽을 실제로 읽었는지 확정하지 못했다. 7MB 짜리 하나를 두 번 까는
비용보다 현장에서 다시 헤매는 비용이 크므로 일단 둘 다 깐다.
로그의 "Loading PCC:1 FPGA file:" 줄에 찍힌 경로를 한 번 확인하면
필요 없는 쪽을 지울 수 있다.
