using System.Collections.Generic;

namespace IJPSystem.Drivers.Motion.Comizoa
{
    /// <summary>
    /// Comizoa EtherCAT SDK 오류코드(ComiEcatSdk_Err.h) → 이름·한글 설명 디코더.
    /// 로그에 "-5" 대신 "-5 (ecERR_DEVICE_NOT_LOADED: 디바이스가 로드되지 않음)" 처럼 남겨
    /// 디버거 없이 현장 로그만으로 원인을 알 수 있게 한다.
    /// ※ 직접 숫자로 정의된 일반/초기화 오류 위주(연결 단계 진단에 필요한 범위).
    /// </summary>
    internal static class ComiEcatError
    {
        private static readonly Dictionary<int, (string Name, string Desc)> Map = new()
        {
            [-5]   = ("ecERR_DEVICE_NOT_LOADED",   "마스터 디바이스가 로드되지 않음(보드 미인식/권한/드라이버)"),
            [-6]   = ("ecERR_WDM_VER_READ_FAIL",   "WDM 커널 드라이버 버전 읽기 실패"),
            [-7]   = ("ecERR_FW_VER_READ_FAIL",    "펌웨어 버전 읽기 실패"),
            [-8]   = ("ecERR_DEV_INACTIVE",        "마스터 디바이스가 비활성 상태"),
            [-9]   = ("ecERR_DEV_BOOT_NOT_COMPT",  "마스터 부팅이 아직 완료되지 않음(부팅 대기 필요)"),
            [-10]  = ("ecERR_INVALID_BOARDID",     "잘못된 보드 ID"),
            [-11]  = ("ecERR_INVALID_DEVIDX",      "잘못된 디바이스 인덱스"),
            [-12]  = ("ecERR_INVALID_VERSION",     "SDK(DLL)/WDM/펌웨어 버전 비호환"),
            [-20]  = ("ecERR_INVALID_NETID",       "잘못된 네트워크 ID(네트워크 미확립 — Scan 필요)"),
            [-21]  = ("ecERR_INVALID_NETID_CONFIG","네트워크 설정이 무효"),
            [-25]  = ("ecERR_INVALID_SLAVEID",     "슬레이브 인덱스/주소가 잘못됨"),
            [-30]  = ("ecERR_INVALID_CHANNEL",     "축/채널 번호가 잘못됨"),
            [-40]  = ("ecERR_INVALID_IXMAP_IDX",   "보간(Interpolation) 그룹 번호 오류"),
            [-50]  = ("ecERR_INVALID_IXMAP_AXES",  "IXMAP 축 구성 오류"),
            [-60]  = ("ecERR_INVALID_FUNC_ARG",    "함수 인자가 유효 범위를 벗어남"),
            [-65]  = ("ecERR_INVALID_HANDLE",      "잘못된 핸들"),
            [-66]  = ("ecERR_INVALID_RESULT_DATA", "결과 데이터가 무효"),
            [-70]  = ("ecERR_NULL_WDMNETCTXT",     "WDM 공유메모리 포인터 NULL"),
            [-73]  = ("ecERR_INVALID_AXIS_INPDO_TYPE","축 InputPDO 구성과 요청 데이터 불일치"),
            [-100] = ("ecERR_INVALID_IO_CHAN_MAP_DATA","I/O 채널 매핑 데이터 오류"),
            [-110] = ("ecERR_INVALID_FILE_PATH",   "파일 경로가 잘못됨/파일 없음"),
            [-125] = ("ecERR_FILE_NOT_FOUND",      "파일을 찾을 수 없음"),
            [-150] = ("ecERR_MEM_ALLOC_FAIL",      "메모리 할당 실패"),
            [-183] = ("ecERR_IMPROPER_AL_STATE",   "현재 AL 상태에서 허용되지 않는 동작"),
            [-185] = ("ecERR_NOT_SUPPORTED_FUNCTION","지원하지 않는 함수(DLL/펌웨어 버전 확인)"),
        };

        /// <summary>오류코드를 "코드 (이름: 설명)" 형태 문자열로 변환.</summary>
        public static string Describe(int code)
        {
            if (Map.TryGetValue(code, out var e))
                return $"{code} ({e.Name}: {e.Desc})";

            // 범위 기반(대략) — 정확한 세부코드는 헤더의 BASE 값 참조.
            string family = code switch
            {
                <= -10000 => "EtherCAT 네트워크 계열",
                <= -1000  => "모션 계열",
                <= -500   => "ODM 계열",
                _         => "미분류",
            };
            return $"{code} (알 수 없는 코드 — {family})";
        }
    }
}
