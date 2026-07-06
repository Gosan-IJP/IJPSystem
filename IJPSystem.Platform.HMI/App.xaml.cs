using IJPSystem.Drivers.IO;
using IJPSystem.Drivers.IO.Comizoa;
using IJPSystem.Drivers.Motion;
using IJPSystem.Drivers.Motion.ACS;
using IJPSystem.Drivers.Motion.Comizoa;
using IJPSystem.Drivers.Vision;
using IJPSystem.Drivers.Vision.Imaqdx;
using IJPSystem.Machines.Pulse;
using IJPSystem.Platform.Common.Constants;
using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Config;
using IJPSystem.Platform.Domain.Models.Motion;
using IJPSystem.Platform.HMI.Common;
using IJPSystem.Platform.HMI.ViewModels;
using IJPSystem.Platform.HMI.Views;
using IJPSystem.Platform.Infrastructure.Config;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace IJPSystem.Platform.HMI
{
    public partial class App : System.Windows.Application
    {
        private IMachine? _machine;
        private bool _errorDialogOpen;   // 미처리 예외 창 중복(무한 중첩) 방지

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

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
                var loader = new ConfigLoader();

                var appSettings = await splashVM.RunStepAsync(
                    "System Configuration", "AppConfig.json 로드",
                    () => loader.LoadAppSettings(GetConfigPath("AppConfig.json")));
                IJPSystem.Platform.Infrastructure.Config.AppSettingsService.Initialize(appSettings);

                var ioDriver = await splashVM.RunStepAsync(
                    "I/O Driver", $"{appSettings.DriverMode.IO} I/O 드라이버 연결",
                    InitializeIODriver);

                var motionDriver = await splashVM.RunStepAsync(
                    "Motion Driver", $"{appSettings.DriverMode.Motion} Motion 드라이버 연결",
                    InitializeMotionDriver);

                var visionDriver = await splashVM.RunStepAsync(
                    "Vision Driver", $"{appSettings.DriverMode.Vision} Vision 드라이버 연결",
                    InitializeVisionDriver);

                _machine = await splashVM.RunStepAsync(
                    "Machine Setup", "PulseMachine 초기화 + Motor Config 로드",
                    () => appSettings.MachineType.ToUpper() switch
                    {
                        "PULSE" => CreatePulse(loader, ioDriver, motionDriver, visionDriver),
                        _ => throw new NotSupportedException($"Unsupported: {appSettings.MachineType}"),
                    });

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
                _         => new VirtualMotionDriver(),   // Virtual / 미인식
            };
            if (motionConfig?.MotionAxisList != null)
            {
                motionDriver.Initialize(motionConfig.MotionAxisList);
            }

            return motionDriver;
        }
        private IVisionDriver InitializeVisionDriver()
        {
            string path = GetConfigPath(AppConstants.VisionConfigFile);
            var loader = new ConfigLoader();
            var root = loader.LoadVisionConfig(path);

            IVisionDriver visionDriver = DriverMode(d => d.Vision) switch
            {
                "imaqdx" => new ImaqdxVisionDriver(),
                _        => new VirtualVisionDriver(),   // Virtual / 미인식
            };
            visionDriver.Initialize(root.VisionCameraList);

            return visionDriver;
        }
        private static string GetConfigPath(string fileName) => PathUtils.GetConfigPath(fileName);
    }
}