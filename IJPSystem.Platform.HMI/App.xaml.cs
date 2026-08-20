using IJPSystem.Drivers.IO;
using IJPSystem.Drivers.IO.Comizoa;
using IJPSystem.Drivers.Motion;
using IJPSystem.Drivers.Motion.ACS;
using IJPSystem.Drivers.Motion.Comizoa;
using IJPSystem.Drivers.Vision;
using IJPSystem.Drivers.Vision.Imaqdx;
using IJPSystem.Drivers.Vision.Ebus;
using IJPSystem.Drivers.Vision.Hikrobot;
using IJPSystem.Machines.Pulse;
using IJPSystem.Platform.Common.Constants;
using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Config;
using IJPSystem.Platform.Domain.Models.Motion;
using IJPSystem.Platform.HMI.Common;
using IJPSystem.Platform.HMI.Common.Models;
using IJPSystem.Platform.HMI.ViewModels;
using IJPSystem.Platform.HMI.Views;
using IJPSystem.Platform.Infrastructure.Config;
using IJPSystem.Platform.Infrastructure.Devices.DropWatcher;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace IJPSystem.Platform.HMI
{
    public partial class App : System.Windows.Application
    {
        private IMachine? _machine;
        private bool _errorDialogOpen;   // 미처리 예외 창 중복(무한 중첩) 방지

        // 단일 인스턴스 가드 — 이미 실행 중일 때 재실행하면 드라이버(COM 포트/EtherCAT/카메라)를
        // 이중 점유하려다 장비가 오동작한다(실장 요청 2026-07-23). OS 뮤텍스로 두 번째 실행을 차단.
        private static System.Threading.Mutex? _singleInstanceMutex;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _singleInstanceMutex = new System.Threading.Mutex(true, @"Global\IJPSystem_HMI_Instance",
                                                              out bool isFirstInstance);
            if (!isFirstInstance)
            {
                Dialogs.Show("IJPSystem 이 이미 실행 중입니다.\n\n기존 창을 사용하세요. 이 창은 종료됩니다.",
                             "중복 실행", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }

            // 글로벌 미처리 예외 → LoggerService 에 기록 (다음 충돌 진단용)
            DispatcherUnhandledException += (s, ev) =>
            {
                ev.Handled = true; // 앱이 즉시 죽지 않게 막음
                LoggerService.WriteToFile("FATAL", $"[UI thread] {ev.Exception}");

                // 같은 예외가 타이머 등에서 반복되면 창이 무한히 쌓이므로, 한 번에 하나만 표시.
                if (_errorDialogOpen) return;
                _errorDialogOpen = true;
                try
                {
                    Dialogs.Show($"미처리 예외:\n\n{ev.Exception.Message}\n\n자세한 내용은 C:\\Logs 의 .txt 로그를 확인하세요.",
                                    "Application Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally { _errorDialogOpen = false; }
            };
            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                LoggerService.WriteToFile("FATAL", $"[non-UI thread] {ev.ExceptionObject}");
            };
            TaskScheduler.UnobservedTaskException += (s, ev) =>
            {
                LoggerService.WriteToFile("FATAL", $"[Task] {ev.Exception}");
                ev.SetObserved();
            };

            // SplashWindow 즉시 표시 (이후 머신/드라이버 초기화 진행 상황 단계별 표시)
            var splashVM = new SplashViewModel();
            var splash   = new SplashWindow { DataContext = splashVM };
            splash.Show();

            try
            {
                // ① 현장 진단용 시작 배너 — 버전/비트수/관리자권한/OS (디버거 없이 환경 확인)
                LogStartupBanner();

                var loader = new ConfigLoader();

                var appSettings = await splashVM.RunStepAsync(
                    "System Configuration", "AppConfig.json 로드",
                    () => loader.LoadAppSettings(GetConfigPath("AppConfig.json")));
                IJPSystem.Platform.Infrastructure.Config.AppSettingsService.Initialize(appSettings);

                // 장비 설정(노즐 헤드 사양 등) — 레시피 DB 와 <b>분리</b>한다. 레시피는 호기 간에
                // 복사해 다니지만 장비 설정이 따라오면 다른 장비의 피치·배율로 측정하게 된다.
                IJPSystem.Platform.Infrastructure.Config.MachineSettings.Initialize(
                    GetConfigPath("MachineData.db"));

                // 차트 축 글꼴 — 차트가 처음 그려지기 전에 정해야 한다.
                // Typeface 를 한 번 읽어 해석을 끝낸 뒤 로그를 남긴다(지연 해석이라 그냥 찍으면
                // 항상 "미해석"만 나온다 — 실제로 그래서 실장 로그가 쓸모없었다, 2026-08-07).
                Common.ChartFont.Configure(appSettings.ChartFontFile);
                _ = Common.ChartFont.Typeface;
                LoggerService.WriteToFile("INFO", $"[BOOT] 차트 축 글꼴: {Common.ChartFont.Description}");

                // 로그 보존 정리 — 기동 시 1회. 파일 수가 많으면 수 초 걸릴 수 있어 백그라운드로 돌린다
                // (스플래시 진행을 막지 않는다). 실패해도 기동에 영향 없음.
                int keepDays = appSettings.LogSaveDays;
                _ = Task.Run(() => LogRetentionService.Cleanup(keepDays));

                // 실장 진단: 실제로 읽은 설정 파일 경로와 파싱된 DriverMode 값을 기록.
                // 스플래시/화면이 Virtual 로 뜨는 원인(잘못된 파일/파싱 실패)을 이 로그로 즉시 확인.
                LoggerService.WriteToFile("INFO",
                    $"[Config] AppConfig 로드: {GetConfigPath("AppConfig.json")} → " +
                    $"DriverMode IO={appSettings.DriverMode.IO}, Motion={appSettings.DriverMode.Motion}, " +
                    $"Vision={appSettings.DriverMode.Vision}, Head={appSettings.DriverMode.Head}");

                var ioDriver = await splashVM.RunStepAsync(
                    "I/O Driver", $"{appSettings.DriverMode.IO} I/O 드라이버 연결",
                    InitializeIODriver);

                var motionDriver = await splashVM.RunStepAsync(
                    "Motion Driver", $"{appSettings.DriverMode.Motion} Motion 드라이버 연결",
                    InitializeMotionDriver);

                var visionDriver = await splashVM.RunStepAsync(
                    "Vision Driver", $"{appSettings.DriverMode.Vision} Vision 드라이버 연결",
                    InitializeVisionDriver);

                // 헤드(Meteor PCC) 연결 확인 — Vision 다음 단계. 읽기 전용 1회 조회.
                // 미부착은 실패가 아니라 경고(!)로 표시하고 기동은 계속한다(엔진 없이도 HMI 는 떠야 함).
                // Head=None(미사용 구성)이어도 항목 자체는 항상 표시한다 — 단계가 통째로 사라지면
                // "확인을 못 한 건지, 안 쓰는 구성인지" 화면에서 구분할 수 없다.
                // 가상도 한 단계로 보여 준다 — 스플래시에서 "미사용"으로 지나가면
                // 화면에 가상 값이 뜨는 이유를 알 수 없다.
                string headMode  = DriverMode(d => d.Head);
                bool headEnabled = headMode == "meteor";
                bool headVirtual = headMode == "virtual";
                await splashVM.RunStepAsync(
                    "Print Head",
                    headEnabled ? "Meteor 헤드 PCC 부착 상태 확인"
                    : headVirtual ? "가상 헤드 — 실물 없이 화면 확인용"
                    : "미사용 — DriverMode.Head=None",
                    () =>
                    {
                        if (headVirtual)
                            return (Enabled: true, Connected: false, Detail: "가상 헤드 — 화면의 값은 실물이 아닙니다");
                        if (!headEnabled)
                            return (Enabled: false, Connected: false, Detail: "미사용 — DriverMode.Head=None");
                        var s = ProbeMeteorHead();
                        return (Enabled: true, Connected: s.Connected, Detail: s.Detail);
                    },
                    r => (!r.Enabled  ? InitStepStatus.Skipped
                          : r.Connected ? InitStepStatus.Done
                                        : InitStepStatus.Warning,
                          r.Detail));

                _machine = await splashVM.RunStepAsync(
                    "Machine Setup", "PulseMachine 초기화 + Motor Config 로드",
                    () => appSettings.MachineType.ToUpper() switch
                    {
                        "PULSE" => CreatePulse(loader, ioDriver, motionDriver, visionDriver),
                        _ => throw new NotSupportedException($"Unsupported: {appSettings.MachineType}"),
                    });

                // 드라이버 Connect 후 실제 로드된 네이티브 DLL(ComiEcatSdk 등) 경로·버전 기록
                LogLoadedNativeModules();

                splashVM.MachineName = _machine.MachineName.ToUpper();

                // MainViewModel 은 DispatcherTimer 등을 만들기 때문에 UI 스레드에서 생성
                var mainVM = await splashVM.RunStepAsync(
                    "HMI 준비", "메인 ViewModel 구성 + 화면 진입",
                    () =>
                    {
                        var controller = new PulseController(_machine);
                        return new MainViewModel(controller);
                    },
                    background: false);

                // 마지막 ✓ 잠깐 보여주기
                await Task.Delay(350);

                // SplashWindow 가 먼저 인스턴스화되어 Application.MainWindow 로 잡히므로
                // 실제 메인 창을 명시적으로 지정 — 종료 커맨드(MainWindow.Close())가 올바른 창을 닫게 함
                var mainWindow = new MainWindow { DataContext = mainVM };
                Current.MainWindow = mainWindow;
                mainWindow.Show();
            }
            catch (Exception ex)
            {
                LoggerService.WriteToFile("FATAL", $"Startup failed: {ex}");
                Dialogs.Show($"Startup failed: {ex.Message}");
                Shutdown();
            }
            finally
            {
                splash.Close();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 모든 종료 경로(메뉴 EXIT / X 버튼 / Alt+F4 / Shutdown)의 단일 정리 지점
            try { _machine?.Terminate(); }
            catch (Exception ex) { LoggerService.WriteToFile("ERROR", $"Machine.Terminate failed: {ex}"); }

            base.OnExit(e);
        }

        private IMachine CreateMachine()
        {
            var loader = new ConfigLoader();
            var appSettings = loader.LoadAppSettings(GetConfigPath("AppConfig.json"));
            var ioDriver     = InitializeIODriver();
            var motionDriver = InitializeMotionDriver();
            var visionDriver = InitializeVisionDriver();

            return appSettings.MachineType.ToUpper() switch
            {
                "PULSE" => CreatePulse(loader, ioDriver, motionDriver, visionDriver),
                _ => throw new NotSupportedException($"Unsupported: {appSettings.MachineType}")
            };
        }

        private IMachine CreatePulse(ConfigLoader loader, IIODriver io, IMotionDriver motion, IVisionDriver vision)
        {
            var machine = new PulseMachine(io, motion, vision);
            machine.Config = loader.LoadMotionConfig(GetConfigPath(AppConstants.MotorConfigFile))
                             ?? new MotionAxisRoot();
            machine.Initialize();
            return machine;
        }

        
        
        /// <summary>
        /// Meteor 헤드(PCC) 부착 상태 1회 조회. 예외를 던지지 않으므로 스플래시 단계가 실패로 끝나지 않는다.
        /// PiOpenPrinter 는 프린터를 점유(claim)하므로 확인 후 즉시 해제 —
        /// 이어서 생성되는 MainViewModel 의 상시 모니터가 다시 붙을 수 있게 한다.
        /// </summary>
        private static MeteorHeadStatus ProbeMeteorHead()
        {
            using var monitor = new MeteorStatusMonitor();
            var status = monitor.Poll();
            LoggerService.WriteToFile(status.Connected ? "INFO" : "WARN", $"[HEAD] {status.Detail}");
            return status;
        }

        /// <summary>AppConfig.json 의 DriverMode 값(대소문자·공백 무시). 미설정 시 Virtual.</summary>
        private static string DriverMode(Func<DriverModeSettings, string> pick)
            => (pick(AppSettingsService.Current?.DriverMode ?? new DriverModeSettings()) ?? "Virtual")
               .Trim().ToLowerInvariant();

        private IIODriver InitializeIODriver()
        {
            string path = GetConfigPath(AppConstants.IoConfigFile);
            var loader = new ConfigLoader();
            var ioConfig = loader.LoadIOConfig(path);

            IIODriver ioDriver = DriverMode(d => d.IO) switch
            {
                "comizoa"  => new ComizoaIODriver(),
                "ethercat" => new EtherCatIODriver(),
                _          => new VirtualIODriver(),   // Virtual / 미인식
            };
            ioDriver.Initialize(ioConfig.GetAllDevices());

            return ioDriver;
        }

        private IMotionDriver InitializeMotionDriver()
        {
            string path = GetConfigPath(AppConstants.MotorConfigFile);
            var loader = new ConfigLoader();
            var motionConfig = loader.LoadMotionConfig(path);

            IMotionDriver motionDriver = DriverMode(d => d.Motion) switch
            {
                // 실장 EtherCAT 설정(ComiEcatLibCfg.ini)을 Config 폴더에서 로드
                "comizoa" => new ComizoaMotionDriver { IniPath = GetConfigPath("ComiEcatLibCfg.ini") },
                "acs"     => new AcsMotionDriver(),
                _         => new VirtualMotionDriver(),  
            };
            if (motionConfig?.MotionAxisList != null)
            {
                motionDriver.Initialize(motionConfig.MotionAxisList);

                // 티칭 저장 범위를 드라이버와 무관하게 남긴다. Comizoa 드라이버 로그에만 있으면
                // Virtual 모드에서 설정이 빠진 걸 알 길이 없다 — 실제로 MotorConfig.json 에서
                // TeachLimit 줄이 사라진 채 범위 밖 값이 저장됐다(2026-08-10).
                var limits = motionConfig.MotionAxisList.Where(a => a.TeachLimit != null).ToList();
                LoggerService.WriteToFile("INFO", limits.Count == 0
                    ? $"[Config] 티칭 저장 범위 설정 없음 — {path}"
                    : $"[Config] 티칭 저장 범위: {string.Join(", ", limits.Select(a => $"{a.Name} {a.TeachLimitText}"))}");
            }

            return motionDriver;
        }
        private IVisionDriver InitializeVisionDriver()
        {
            string path = GetConfigPath(AppConstants.VisionConfigFile);
            var loader = new ConfigLoader();
            var root = loader.LoadVisionConfig(path);

            // 카메라별 Driver 지정(VisionConfig)이 우선, 없으면 전역 DriverMode.Vision.
            // 9호기처럼 벤더가 섞이면(JAI=eBUS / 하이크로봇=별도) 카메라마다 달라진다.
            string global = DriverMode(d => d.Vision);

            // ★단 Virtual 은 예외 — 카메라별 지정보다 전역이 이긴다.
            //   Virtual 은 "이 PC 에는 하드웨어가 없다"는 선언이라, 카메라별 Driver 가 이기면
            //   개발 PC 에서 벤더 SDK(MVS 등)를 로드하려다 실패하고 그 카메라만 미연결로 남는다.
            //   시뮬레이션이 목적인데 일부만 가상이 되는 셈이라 전 카메라를 가상으로 내린다.
            if (global == "virtual")
            {
                var overridden = root.VisionCameraList
                                     .Where(c => !string.IsNullOrWhiteSpace(c.Driver))
                                     .Select(c => $"{c.CameraId}={c.Driver}")
                                     .ToList();
                if (overridden.Count > 0)
                    LoggerService.WriteToFile("INFO",
                        $"[VISION] DriverMode.Vision=Virtual — VisionConfig 의 카메라별 Driver 무시(전 카메라 가상): {string.Join(", ", overridden)}");

                var virtualDriver = CreateVisionDriver("virtual");
                virtualDriver.Initialize(root.VisionCameraList);
                return virtualDriver;
            }

            var keys = root.VisionCameraList
                           .Select(c => CompositeVisionDriver.ResolveKey(c, global))
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .ToList();

            // 드라이버가 한 종류뿐이면 다중화 계층을 끼우지 않는다 — 0호기 같은 단일 벤더
            // 장비의 동작 경로를 그대로 둔다.
            IVisionDriver visionDriver = keys.Count > 1
                ? new CompositeVisionDriver(global, CreateVisionDriver)
                : CreateVisionDriver(keys.Count == 1 ? keys[0] : global);

            visionDriver.Initialize(root.VisionCameraList);
            return visionDriver;
        }

        /// <summary>드라이버 키 → 인스턴스. 미인식 키는 Virtual 로 떨어져 앱이 뜨는 것을 막지 않는다.</summary>
        private static IVisionDriver CreateVisionDriver(string key) => key switch
        {
            "imaqdx"   => new ImaqdxVisionDriver(),      // 0호기 — NI-IMAQdx(niimaqdx.dll)
            "ebus"     => new EbusVisionDriver(),        // 9호기 드랍와처 — Pleora eBUS SDK(JAI)
            "hikrobot" => new HikrobotVisionDriver(),    // 9호기 글라스뷰 — Hikrobot MVS SDK
            _          => new VirtualVisionDriver(),
        };
        private static string GetConfigPath(string fileName) => PathUtils.GetConfigPath(fileName);

        /// <summary>시작 배너: 앱 버전·프로세스 비트수·관리자권한·런타임·OS 를 1회 기록.</summary>
        private static void LogStartupBanner()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                string ver     = asm.GetName().Version?.ToString() ?? "?";
                string bitness = Environment.Is64BitProcess ? "x64(64bit)" : "x86(32bit)";

                bool admin = false;
                try
                {
                    using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                    admin = new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
                catch { /* 권한 조회 실패는 무시 */ }

                LoggerService.WriteToFile("INFO",
                    $"[BOOT] IJPSystem HMI v{ver} | {bitness} | Admin={admin} | " +
                    $".NET {Environment.Version} | OS {Environment.OSVersion.Version} | PC {Environment.MachineName}");

                // 어셈블리 버전은 빌드마다 안 바뀌어 "새 DLL 이 적용됐는지"를 못 가린다 —
                // 실제로 로드된 파일의 경로·수정시각을 남겨야 복사가 먹었는지 확인할 수 있다.
                foreach (string line in Common.BuildInfo.DescribeLoaded())
                    LoggerService.WriteToFile("INFO", line);
            }
            catch { /* 배너 실패는 앱 진행에 영향 없음 */ }
        }

        /// <summary>로드된 벤더 네이티브 DLL(ComiEcat/cmm 등)의 경로·파일버전 기록 — 비트수/버전 불일치 진단.</summary>
        private static void LogLoadedNativeModules()
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetCurrentProcess();
                bool any = false;
                foreach (System.Diagnostics.ProcessModule m in proc.Modules)
                {
                    string name = m.ModuleName ?? "";
                    if (name.IndexOf("Comi",   StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("cmm",    StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("niimaq", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    any = true;
                    string fver = "";
                    try { fver = m.FileVersionInfo?.FileVersion ?? ""; } catch { }
                    LoggerService.WriteToFile("INFO", $"[BOOT] 네이티브 모듈: {name} v{fver} @ {m.FileName}");
                }
                if (!any)
                    LoggerService.WriteToFile("WARN",
                        "[BOOT] 벤더 네이티브 모듈(ComiEcatSdk 등)이 로드되지 않음 — DriverMode Virtual 이거나 DLL 로드 실패 가능.");
            }
            catch { /* 모듈 열거 실패는 무시 */ }
        }
    }
}