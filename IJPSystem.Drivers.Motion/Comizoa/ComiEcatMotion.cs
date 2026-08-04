using IJPSystem.Platform.Common.Utilities;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace IJPSystem.Drivers.Motion.Comizoa
{
    /// <summary>
    /// Comizoa EtherCAT (PCI/Ex, ComiECAT) 모션 구현 — ComiEcatSdk.dll 래퍼.
    ///
    /// 실제 DLL export 확인 결과 이 DLL 은 PCI/Ex EtherCAT SDK(헤더 ComiEcatSdk_Api.h)이며,
    /// 모션은 ecmSx*(단축)/ecmHome*(원점)/ecNet*/ecGn* 접두사를 사용한다.
    ///
    /// 규약(중요):
    ///   - 명령 함수(SetSvon/Move*/Stop/SetSpeedPatt/Home* 등)는 t_cmdidx 를 반환하며 0 이면 실패다.
    ///   - ecmSxSt_WaitCompt / ecmHomeSt_WaitCompt 는 1=성공(완료대기 성공).
    ///   - ecmSxSt_GetFlags 는 t_word 비트플래그(TEcmSxSt_Flags)를 반환한다.
    ///   - 위치/속도 단위는 ecmSxCfg_SetUnitDist(펄스/논리단위) 설정에 따른 "논리 단위"다.
    ///   - NetID 는 단일 네트워크 기준 0.
    ///   - x86 빌드 필수(이 DLL 이 32비트). x64 프로세스는 로드 불가.
    ///
    /// ※ 실장(현장) 검증 필요 항목:
    ///   1) 초기화(디바이스 로드/모션환경 파일) — ecGn_LoadDevice/ecmGn_InitFromFile 는 이 DLL(2022)과
    ///      온라인 문서(2025) 사이 버전차가 있어 시그니처가 다를 수 있다. LabVIEW Init.vi 또는 헤더로 확정할 것.
    ///      (단, 호출규약이 Cdecl(호출자 스택정리)이라 인자 수가 달라도 프로세스가 죽지는 않는다.)
    ///   2) 원점복귀 방향(Direction)·모드(HomeMode) — 물리적으로 축을 움직이므로 반드시 사이트 값으로 설정할 것.
    ///   3) GetPosition 의 카운터 선택 인자 타입 — 문서상 t_f64 로 표기되나 실제는 열거형(int)으로 판단해 int 로 선언함.
    /// </summary>
    public sealed class ComiEcatMotion : IComiMotion
    {
        private const string Dll = "ComiEcatSdk.dll";
        private const int NetID = 0;               // 단일 EtherCAT 네트워크

        // ── 상수(문서 확인값) ─────────────────────────────────────────
        private const byte ecAL_STATE_OP = 8;      // EtherCAT AL-State: OP(운전)
        private const int ecmDIR_N = 0;            // (-) 방향
        private const int ecmDIR_P = 1;            // (+) 방향
        private const int ecmSMODE_TRAPE  = 1;     // 사다리꼴 가감속
        private const int ecmSMODE_SCURVE = 2;     // S-curve 가감속
        private const int ecmCNT_CMD  = 0;         // Command(지령) 위치 카운터
        private const int ecmCNT_FEED = 1;         // Feedback(실제) 위치 카운터

        // ===== P/Invoke =====================================================
        // -- 네트워크/초기화 (LabVIEW Init.vi 와 동일 함수 사용) --
        // ecGn_LoadDevice 시 라이브러리가 현재 작업 디렉터리의 'ComiEcatLibCfg.ini' 를 읽어 마스터를 구성한다.
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool ecGn_LoadDevice(out int errCode);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool ecGn_UnloadDevices(out int errCode);
        // 진단용: 드라이버가 인식한 마스터/네트워크 수. LoadDevice 실패(-5) 원인 구분에 사용.
        [DllImport(Dll)] private static extern int ecGn_GetNumDevices(out int errCode);
        [DllImport(Dll)] private static extern int ecGn_GetNumNetworks(out int errCode);
        // 구성된 슬레이브 수(LabVIEW Init.vi 와 동일). 반환=슬레이브 수.
        [DllImport(Dll)] private static extern int ecNet_GetCfgSlaveCount(int netId, out int errCode);
        // AlState 는 EEcAlState(4바이트 enum) → int 로 마샬. (byte 로 선언하면 값/스택 크기가 어긋남)
        [DllImport(Dll)] private static extern uint ecNet_SetAlState(int netId, int alState, out int errCode);
        // 현재 AL-State 조회(LabVIEW Init.vi 와 동일). OP 전환 완료 확인용. 반환=현재 상태값.
        [DllImport(Dll)] private static extern int ecNet_GetAlState(int netId, out int errCode);

        // -- 서보 제어 --
        [DllImport(Dll)] private static extern int ecmSxCtl_SetSvon(int netId, int axis, int svon, out int errCode);
        [DllImport(Dll)] private static extern int ecmSxCtl_GetSvon(int netId, int axis, out int errCode);
        [DllImport(Dll)] private static extern int ecmSxCtl_ResetAlm(int netId, int axis, out int errCode);

        // -- 축 설정(속도패턴/단위) --
        [DllImport(Dll)] private static extern int ecmSxCfg_SetSpeedPatt(int netId, int axis, int speedMode, double vini, double vend, double vwork, double acc, double dec, out int errCode);
        // UnitDist 는 t_f64(double) — 절대 long 이 아니다(공식 예제 확인). long 으로 넘기면 비트패턴이 깨져 엉뚱한 분해능이 설정됨.
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool ecmSxCfg_SetUnitDist(int netId, int axis, double unitDist, out int errCode);

        // -- 단축 모션 --
        [DllImport(Dll)] private static extern int ecmSxMot_MoveToStart(int netId, int axis, double position, out int errCode);   // 절대
        [DllImport(Dll)] private static extern int ecmSxMot_MoveStart(int netId, int axis, double distance, out int errCode);      // 상대
        [DllImport(Dll)] private static extern int ecmSxMot_VMoveStart(int netId, int axis, int dir, out int errCode);            // 조그(속도이송)
        [DllImport(Dll)] private static extern int ecmSxMot_Stop(int netId, int axis, int isDecStop, int isWaitCompt, out int errCode);

        // -- 상태 --
        // IsBusy 반환은 t_bool(1바이트) → byte. int 로 받으면 eax 상위바이트 잔여값 때문에 항상 busy 로 오판될 수 있음.
        [DllImport(Dll)] private static extern byte ecmSxSt_IsBusy(int netId, int axis, out int errCode);
        [DllImport(Dll)] private static extern int ecmSxSt_WaitCompt(int netId, int axis, out int errCode);
        [DllImport(Dll)] private static extern ushort ecmSxSt_GetFlags(int netId, int axis, out int errCode);
        [DllImport(Dll)] private static extern double ecmSxSt_GetPosition(int netId, int axis, int targCntr, out int errCode);
        // 현재 위치를 지정값으로 재정의(모션 없이 카운터만 재설정). Get 과 대칭 시그니처.
        [DllImport(Dll)] private static extern int ecmSxSt_SetPosition(int netId, int axis, int targCntr, double pos, out int errCode);
        // 축 전용 디지털 입력(하드리밋 ±/원점 등). 반환은 t_word 비트필드(TEcmSxSt_DI).
        [DllImport(Dll)] private static extern ushort ecmSxSt_GetDI(int netId, int axis, out int errCode);

        // -- 원점복귀 --
        [DllImport(Dll)] private static extern int ecmHomeCfg_SetMode(int netId, int axis, int homeOpMode, out int errCode);
        [DllImport(Dll)] private static extern int ecmHomeCfg_SetOffset(int netId, int axis, double offset, out int errCode);
        [DllImport(Dll)] private static extern int ecmHomeCfg_SetSpeedPatt(int netId, int axis, int speedMode, double vel, double acc, double dec, double homeSpecVel, out int errCode);
        [DllImport(Dll)] private static extern int ecmHomeMot_MoveStart(int netId, int axis, int direction, out int errCode);
        [DllImport(Dll)] private static extern int ecmHomeSt_WaitCompt(int netId, int axis, out int errCode);
        // ====================================================================

        private bool _init;
        // 축별 원점복귀 방향(+1/-1)을 SetHomeParameters 에서 저장 → Home() 에서 사용(안전값 확정용).
        private readonly System.Collections.Generic.Dictionary<AxisId, int> _homeDir = new();
        // 알람 진단: 축별 마지막으로 로깅한 상태 워드(변화 시에만 1회 로깅 — 폴링 스팸 방지).
        private readonly System.Collections.Generic.Dictionary<AxisId, ushort> _lastAlarmFlags = new();
        // 센서(DI) 진단: 축별 마지막 DI 워드(변화 시에만 로깅). 비트 매핑 실장 검증용.
        private readonly System.Collections.Generic.Dictionary<AxisId, ushort> _lastDi = new();

        // 축 전용 DI 비트 매핑. 실장 확인 결과 물리 배선 기준으로 표기:
        //   bit0 = 하한(Lower, -EL),  bit1 = 상한(Upper, +EL),  bit2 = ORG(원점/HOME)
        // ※ 상/하한은 스테이지 직선 이동 기준(위=상한). CW/CCW(모터 회전)와는 다른 개념이며,
        //   회전방향↔상하한 관계는 기구 조립에 따라 달라지므로 물리 스위치 기준으로 표기한다.
        private const int DI_BIT_LIMIT_LOWER = 0;   // -EL / 하한(물리 하단 스위치)
        private const int DI_BIT_LIMIT_UPPER = 1;   // +EL / 상한(물리 상단 스위치)
        private const int DI_BIT_ORG         = 2;   // ORG / HOME

        /// <summary>cmdidx(=0 실패) 반환 명령 함수 검사. 실패 시 오류코드를 이름·설명으로 디코딩.</summary>
        private static void CheckCmd(int cmdidx, int err, string op)
        {
            if (cmdidx == 0)
                throw new InvalidOperationException($"{op} 실패 (err={ComiEcatError.Describe(err)})");
        }
        private void Need() { if (!_init) throw new InvalidOperationException("Init 먼저 호출."); }

        public void Init(ComiEcatConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            // LabVIEW Init.vi 실제 순서: ecGn_LoadDevice → GetNumDevices → ecNet_GetCfgSlaveCount → ecNet_SetAlState(OP)
            //
            // ★ 핵심(DLL 분석으로 확인): Comizoa 라이브러리는 설정을 앱 폴더/CWD 가 아니라
            //   고정 위치 'C:\ProgramData\COMIZOA\' 에서 읽는다.
            //   (DLL 내부: SHGetKnownFolderPath(Common AppData) + "\COMIZOA\...")
            //   필요한 구성 파일:
            //     · ComiEcatLibCfg.ini   (라이브러리/마스터 IP·모드)
            //     · ComiEcatDCSetup.ini  (DC 셋업)
            //     · CEcatNetCfg_*.cec     (EtherCAT 네트워크/슬레이브 구성)  ← Comizoa 설정 유틸리티가 생성
            //   이 파일들이 없으면 마스터 미구성 → LoadDevice 가 -5(ecERR_DEVICE_NOT_LOADED, NumDevices=0) 로 실패.
            //   앱의 Config\ComiEcatLibCfg.ini 를 이 위치에 없을 때만 복사하고, 폴더 내용을 로그로 남긴다.
            //   ※ .cec(네트워크 구성)는 앱이 만들 수 없다 — 실장 PC 의 C:\ProgramData\COMIZOA\ 에 존재해야 한다.
            string comizoaDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "COMIZOA");
            try
            {
                System.IO.Directory.CreateDirectory(comizoaDir);
                string dstIni = System.IO.Path.Combine(comizoaDir, "ComiEcatLibCfg.ini");
                if (!System.IO.File.Exists(dstIni) && !string.IsNullOrEmpty(cfg.SourcePath) && System.IO.File.Exists(cfg.SourcePath))
                {
                    System.IO.File.Copy(cfg.SourcePath, dstIni);
                    LoggerService.WriteToFile("INFO", $"[ComiEcat] ComiEcatLibCfg.ini → '{dstIni}' 복사(신규)");
                }
                string[] files = System.IO.Directory.GetFiles(comizoaDir);
                for (int i = 0; i < files.Length; i++) files[i] = System.IO.Path.GetFileName(files[i]);
                LoggerService.WriteToFile("INFO", $"[ComiEcat] '{comizoaDir}' 구성파일: [{string.Join(", ", files)}]");
            }
            catch (Exception ex)
            {
                LoggerService.WriteToFile("WARN", $"[ComiEcat] '{comizoaDir}' 준비 경고: {ex.Message}");
            }

            bool loaded = ecGn_LoadDevice(out int errLoad);
            int numDev = ecGn_GetNumDevices(out _);
            int numNet = ecGn_GetNumNetworks(out _);
            LoggerService.WriteToFile("INFO",
                $"[ComiEcat] LoadDevice={loaded} (err={ComiEcatError.Describe(errLoad)}), NumDevices={numDev}, NumNetworks={numNet}");
            if (!loaded)
                throw new InvalidOperationException(
                    $"LoadDevice 실패 (err={ComiEcatError.Describe(errLoad)}, NumDevices={numDev}). " +
                    $"C:\\ProgramData\\COMIZOA\\ 의 구성파일(ComiEcatLibCfg.ini/ComiEcatDCSetup.ini/CEcatNetCfg_*.cec) 존재 여부, " +
                    $"Comizoa 데몬 실행, 원격 마스터({cfg.MasterIp}) 연결을 확인.");

            // 구성된 슬레이브 수 확인(LabVIEW 와 동일: ecNet_GetCfgSlaveCount)
            int slaveCnt = ecNet_GetCfgSlaveCount(NetID, out int errCnt);
            LoggerService.WriteToFile("INFO",
                $"[ComiEcat] CfgSlaveCount(net={NetID}) = {slaveCnt} (err={ComiEcatError.Describe(errCnt)})");

            // 마스터/슬레이브를 OP(운전) 상태로 전환 — 이 단계가 되어야 PDO 입출력·모션이 가능.
            uint rc = ecNet_SetAlState(NetID, ecAL_STATE_OP, out int err);
            if (rc == 0) throw new InvalidOperationException($"SetAlState(OP) 실패 (err={ComiEcatError.Describe(err)})");

            // OP 전환은 즉시 완료가 아니다(DC 셋업 지연=DC_Setup_Delay, ini 기본 4000ms).
            // LabVIEW 처럼 ecNet_GetAlState 로 실제 OP 도달을 폴링 확인한다.
            int timeoutMs = Math.Max(6000, cfg.DcSetupDelay + 3000);
            var sw = Stopwatch.StartNew();
            int alState = 0;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                alState = ecNet_GetAlState(NetID, out _);
                if (alState == ecAL_STATE_OP) break;
                Thread.Sleep(50);
            }
            if (alState != ecAL_STATE_OP)
                throw new InvalidOperationException(
                    $"OP 전환 확인 실패 — 현재 AlState={alState} (기대=8/OP), {sw.ElapsedMilliseconds}ms 대기. " +
                    "슬레이브 전원/배선/링 연결, DC 설정 확인.");
            LoggerService.WriteToFile("INFO",
                $"[ComiEcat] SetAlState(OP) 성공 — 마스터 운전 상태 (도달 {sw.ElapsedMilliseconds}ms)");

            _init = true;
        }

        public void Unload()
        {
            if (!_init) return;
            try { ecNet_SetAlState(NetID, /*PREOP*/2, out _); } catch { }
            try { ecGn_UnloadDevices(out _); } catch { }
            _init = false;
        }

        public void ServoOn(AxisId a, bool on)
        {
            Need();
            int rc = ecmSxCtl_SetSvon(NetID, (int)a, on ? 1 : 0, out int err);
            CheckCmd(rc, err, "ServoOn");
        }

        public void MoveAbsolute(AxisId a, double pos)
        {
            Need();
            int rc = ecmSxMot_MoveToStart(NetID, (int)a, pos, out int err);   // 절대 위치
            CheckCmd(rc, err, "MoveAbsolute");
        }

        public void MoveRelative(AxisId a, double dist)
        {
            Need();
            int rc = ecmSxMot_MoveStart(NetID, (int)a, dist, out int err);    // 상대 거리
            CheckCmd(rc, err, "MoveRelative");
        }

        public void Jog(AxisId a, int dir)
        {
            Need();
            int rc = ecmSxMot_VMoveStart(NetID, (int)a, dir >= 0 ? ecmDIR_P : ecmDIR_N, out int err);
            CheckCmd(rc, err, "Jog");
        }

        public void Stop(AxisId a)
        {
            Need();
            // IsDecStop=1(감속정지), IsWaitCompt=0(대기 안함)
            int rc = ecmSxMot_Stop(NetID, (int)a, 1, 0, out int err);
            CheckCmd(rc, err, "Stop");
        }

        public void StopAll()
        {
            foreach (AxisId a in Enum.GetValues(typeof(AxisId)))
                try { ecmSxMot_Stop(NetID, (int)a, 1, 0, out _); } catch { }
        }

        public void SetVelocity(AxisId a, VelocityProfile p)
        {
            Need();
            // SpeedMode=사다리꼴, Vini=0, Vend=0, Vwork=속도, Acc/Dec
            int rc = ecmSxCfg_SetSpeedPatt(NetID, (int)a, ecmSMODE_TRAPE,
                                           0.0, 0.0, p.Velocity, p.Acceleration, p.Deceleration, out int err);
            CheckCmd(rc, err, "SetVelocity");
        }

        public void SetEncoderResolution(AxisId a, double ppu)
        {
            Need();
            // 논리단위 1 을 이동하기 위한 펄스 수(UnitDist, t_f64=double)
            bool ok = ecmSxCfg_SetUnitDist(NetID, (int)a, ppu, out int err);
            if (!ok) throw new InvalidOperationException($"SetEncoderResolution 실패 (err={err})");
        }

        public void SetHomeParameters(AxisId a, HomeParameters h)
        {
            Need();
            _homeDir[a] = h.Direction;   // Home() 에서 사용
            int rc;
            rc = ecmHomeCfg_SetMode(NetID, (int)a, h.HomeMode, out int err);           CheckCmd(rc, err, "SetHomeMode");
            rc = ecmHomeCfg_SetOffset(NetID, (int)a, h.Offset, out err);               CheckCmd(rc, err, "SetHomeOffset");
            // 속도패턴: SpeedMode=사다리꼴, Vel=고속, Acc/Dec, HomeSpecVel=저속(근접)
            rc = ecmHomeCfg_SetSpeedPatt(NetID, (int)a, ecmSMODE_TRAPE,
                                         h.HighVelocity, h.Acceleration, h.Acceleration, h.LowVelocity, out err);
            CheckCmd(rc, err, "SetHomeSpeedPatt");
        }

        public void Home(AxisId a)
        {
            Need();
            // ※ 방향은 SetHomeParameters 로 설정된 값 사용. 미설정 시 (-)방향 기본값.
            //   원점 방향/모드는 물리적 안전과 직결되므로 실장 전 반드시 사이트 값 확인.
            int dir = _homeDir.TryGetValue(a, out int d) ? (d >= 0 ? ecmDIR_P : ecmDIR_N) : ecmDIR_N;
            int rc = ecmHomeMot_MoveStart(NetID, (int)a, dir, out int err);
            CheckCmd(rc, err, "Home");
        }

        /// <summary>
        /// 현재 위치를 지정값으로 재정의한다(모션 없이 카운터만 재설정). 원점복귀 완료 후 0 세팅용.
        /// 지령(CMD)·피드백(FEED) 카운터를 함께 맞춰 이후 절대이동이 어긋나지 않게 한다.
        /// ※ 축이 정지 상태일 때만 호출할 것(구동 중 재정의는 위험).
        /// </summary>
        public void SetPosition(AxisId a, double pos)
        {
            Need();
            int rc = ecmSxSt_SetPosition(NetID, (int)a, ecmCNT_CMD, pos, out int err);  CheckCmd(rc, err, "SetPosition(CMD)");
            rc     = ecmSxSt_SetPosition(NetID, (int)a, ecmCNT_FEED, pos, out err);      CheckCmd(rc, err, "SetPosition(FEED)");
        }

        public void SetHomeDirection(AxisId a, int dir)
        {
            // 드라이브 설정(ecmHomeCfg_*)은 건드리지 않는다 — 방향은 ecmHomeMot_MoveStart 인자라
            // 여기 저장해 두었다가 Home() 이 그대로 넘긴다. Init 전에도 호출 가능.
            _homeDir[a] = dir >= 0 ? +1 : -1;
        }

        public void SetHomeSpeedPattern(AxisId a, double vel, double acc, double dec, double specVel)
        {
            Need();
            // LabVIEW Set Home Parameters.vi 와 동일: 홈 속도 패턴만 설정(모드/방향/오프셋 미변경).
            int rc = ecmHomeCfg_SetSpeedPatt(NetID, (int)a, ecmSMODE_TRAPE, vel, acc, dec, specVel, out int err);
            CheckCmd(rc, err, "SetHomeSpeedPatt");
        }

        public void SetSoftLimit(AxisId a, SoftLimit l)
        {
            Need();
            // ※ ecmSxCfg_SetSoftLimit 시그니처 미확정으로 현재 미구현(no-op).
            //   소프트리밋은 Comizoa 모션 환경파일에서 설정하거나, 헤더 확인 후 여기에 구현할 것.
        }

        public void SetAlarmState(AxisId a, bool reset)
        {
            Need();
            if (!reset) return;
            int rc = ecmSxCtl_ResetAlm(NetID, (int)a, out int err);
            CheckCmd(rc, err, "ResetAlarm");
        }

        public AxisState GetState(AxisId a)
        {
            Need();
            double pos = ecmSxSt_GetPosition(NetID, (int)a, ecmCNT_FEED, out _);   // 실제(피드백) 위치
            ushort f = ecmSxSt_GetFlags(NetID, (int)a, out _);                     // 상태 비트(TEcmSxSt_Flags 순서)
            bool busy = ecmSxSt_IsBusy(NetID, (int)a, out _) != 0;

            // 비트 순서(문서 TEcmSxSt_Flags): 0 RdyToSwOn,1 SwOn,2 OperEnabled(=서보온),3 ServoFault,
            //   4 VoltEnabled,5 QuickStop,6 SwOnDisabled,7 ServoWarn,8 CtlrFault,9 HomeError,
            //   10 OMS1,11 IntLimit,12 OMS2,13 HomeBusy,14 HomeAttained
            bool servoOn      = (f & (1 << 2)) != 0;
            bool alarm        = (f & (1 << 3)) != 0 || (f & (1 << 8)) != 0;   // ServoFault | CtlrFault
            bool homeBusy     = (f & (1 << 13)) != 0;   // 원점복귀 진행 중
            bool homeAttained = (f & (1 << 14)) != 0;   // 원점복귀 완료

            // 알람 진단: 알람 비트가 서면 raw 상태 워드를 값이 바뀔 때만 1회 로깅.
            // 실제 서보 폴트(bit3)인지, 전원투입 정상상태(bit6 SwOnDisabled 등)를 오판한 건지 판별용.
            if (alarm)
            {
                if (!_lastAlarmFlags.TryGetValue(a, out var prev) || prev != f)
                {
                    _lastAlarmFlags[a] = f;
                    LoggerService.WriteToFile("WARN",
                        $"[ComiEcat] Axis {(int)a} 알람 raw=0x{f:X4} " +
                        $"(bit3 ServoFault={(f >> 3) & 1}, bit8 CtlrFault={(f >> 8) & 1}, " +
                        $"bit2 OperEnabled={(f >> 2) & 1}, bit6 SwOnDisabled={(f >> 6) & 1}, " +
                        $"bit7 ServoWarn={(f >> 7) & 1}, bit9 HomeError={(f >> 9) & 1})");
                }
            }
            else if (_lastAlarmFlags.Count > 0)
            {
                _lastAlarmFlags.Remove(a);
            }

            // 축 전용 DI(±하드리밋 / 원점) 읽기 — GetFlags(상태워드)엔 없으므로 ecmSxSt_GetDI 사용.
            ushort di = ecmSxSt_GetDI(NetID, (int)a, out _);
            bool upperLimit = (di & (1 << DI_BIT_LIMIT_UPPER)) != 0;   // 상한(+EL)
            bool lowerLimit = (di & (1 << DI_BIT_LIMIT_LOWER)) != 0;   // 하한(-EL)
            bool orgSns     = (di & (1 << DI_BIT_ORG))         != 0;   // HOME(ORG)

            // 센서 비트 매핑 검증용: DI 워드가 바뀔 때만 raw 를 1회 로깅(폴링 스팸 방지).
            // 실장에서 각 센서를 물리적으로 눌러 어느 비트가 서는지 확인 → 매핑 상수 보정.
            if (!_lastDi.TryGetValue(a, out var prevDi) || prevDi != di)
            {
                _lastDi[a] = di;
                LoggerService.WriteToFile("INFO",
                    $"[ComiEcat] Axis {(int)a} DI raw=0x{di:X4} (상한={(upperLimit ? 1 : 0)}, 하한={(lowerLimit ? 1 : 0)}, HOME={(orgSns ? 1 : 0)})");
            }

            return new AxisState
            {
                Position   = pos,
                IsMoving   = busy,
                ServoOn    = servoOn,
                IsHomed    = homeAttained,
                HomeBusy   = homeBusy,
                Alarm      = alarm,
                UpperLimit = upperLimit,   // 상한 하드리밋
                LowerLimit = lowerLimit,   // 하한 하드리밋
                HomeSensor = orgSns        // 원점(HOME) 센서
            };
        }

        public bool WaitForDone(AxisId a, int timeoutMs)
        {
            Need();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!GetState(a).IsMoving) return true;
                Thread.Sleep(5);
            }
            return false;
        }

        // 원점복귀 완료 대기. 홈 모션은 단축모션(ecmSx) busy 와 무관하므로 WaitForDone(IsMoving) 로는
        // 완료를 못 잡는다. 홈 전용 플래그(HomeBusy/HomeAttained)로 1차 판정하되,
        // ★ 드라이브가 HomeAttained(bit14)를 모션 정지 전에 미리 세우는 사례가 있어(실장: 홈 done 직후
        //   축이 계속 이동 → 이어지는 절대이동이 err=-1020 로 거부됨), 실제 '위치 정지'까지 함께 확인한다.
        public bool WaitForHomeDone(AxisId a, int timeoutMs)
        {
            Need();
            // MoveStart 직후 드라이브가 HomeBusy 를 세우기 전에 완료로 오판하지 않도록 잠깐 안정화.
            Thread.Sleep(100);
            var sw = Stopwatch.StartNew();
            bool sawBusy = false;

            // 정지 판정: 위치가 최근 SettleWindowMs 동안 SettleBand(mm) 범위를 벗어나지 않으면 '정지'.
            //  · 서보 미세진동(수십 µm)은 밴드보다 작아 흡수 → 오래 매달리지 않음(0.005mm 정밀판정의 문제 해결)
            //  · 잔여 이송/크리프는 밴드를 벗어나 기준을 리셋 → 진짜 멈출 때만 확정
            //  · ecmSx IsBusy 는 홈 후 오래 stuck 되어 부적합하므로 사용하지 않음
            const double SettleBand = 0.1;
            const long SettleWindowMs = 250;
            double refPos = double.NaN;
            long refMs = 0;

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var st = GetState(a);
                if (st.HomeBusy) sawBusy = true;

                long nowMs = sw.ElapsedMilliseconds;
                if (double.IsNaN(refPos) || Math.Abs(st.Position - refPos) > SettleBand)
                {
                    refPos = st.Position;   // 밴드 이탈(이동 중) → 기준 갱신
                    refMs = nowMs;
                }
                bool settled = (nowMs - refMs) >= SettleWindowMs;

                bool homeFlagDone = !st.HomeBusy && (sawBusy || st.IsHomed);
                if (homeFlagDone && settled)
                    return st.IsHomed;
                Thread.Sleep(10);
            }
            return false;
        }

        public void Dispose() => Unload();
    }
}
