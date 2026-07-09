using IJPSystem.Platform.HMI.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace IJPSystem.Platform.HMI.Views
{
    public partial class MainDashboardView : UserControl
    {
        // ── 상태 ────────────────────────────────────────────────────
        private bool _isAnimating = false;
        private bool _isScanning  = false;
        private readonly List<Ellipse> _particles  = new();
        private readonly List<TranslateTransform> _nozzleXTransforms = new();
        // CompositionTarget.Rendering — V-sync 기반 프레임 콜백 (DispatcherTimer 대비 jitter 적음)
        private bool _renderingHooked;
        private DateTime _animStart;
        private readonly Random _rng = new();

        // 시퀀스 진행 상태 추적 — 잉크 분사 / 스캔라인 가시성 제어
        private int _currentStepNo;
        private double _maxScanT;                  // PrintedAreaScale 단조 증가용 (한 번 인쇄된 영역 유지)
        private const int PrintScanStepNo = 9;     // AutoPrintSequence step 9 = 인쇄 진행 (15단계 시퀀스)
        // 각 step 진입 시각 (animStart 기준 초) — 스크립트 모드 phase 애니메이션 기점
        private readonly Dictionary<int, double> _stepTimes = new();
        // 파티클 분사 throttle — V-sync ~60fps 환경에서 매 프레임 분사 시 GC 압력 큼
        private int _particleFrameSkip;

        // ── 진단용 ────────────────────────────────────────────────
        // 프레임 간격 / head 점프 / motor 점프 임계값 초과 시 Debug.WriteLine
        // VS 디버그 실행 시 [출력] 창의 디버그 출력에서 [DASH] 태그로 확인
        // static readonly로 둬서 if (DiagEnabled) 블록이 컴파일 타임에 unreachable로 잡히지 않게 함
        private static readonly bool DiagEnabled = false;   // 헤드 매핑 이슈 재발 시 true로 켜면 [DASH] 진단 로그 출력
        private const double FrameSpikeMs      = 50;     // 16ms 정상, 50ms 초과 = 프레임 스킵
        private const double HeadJumpPx        = 40;     // 1프레임에 40px 이상 점프 = 의심
        private const double MotorJumpMm       = 20;     // 1프레임에 20mm 이상 점프 = 의심
        private DateTime _lastFrameAt;
        private double   _lastScanMm = double.NaN;   // 직전 프레임 스캔축(이송축) 위치

        // ── 레이아웃 상수 ────────────────────────────────────────────
        private const double HeadParkedX    = -250;
        private const double HeadScanStartX =    0;
        private const double HeadScanEndX   =  540;
        private const double PrintAreaMaxW  =  534;

        private const double GlassParkedL   = -600;
        private const double GlassCenter    =    0;
        private const double GlassParkedR   =  620;

        private const double NozzleCenterX = 130;
        private const double NozzleBaseY   = 222;

        // ── 실장 구조: 헤드(X) 고정 / 스테이지(Y) 이동 ──────────────────
        // 헤드는 스캔존에 고정되고, 스테이지(글라스)가 그 밑을 통과하며 인쇄한다.
        // 상수 정합: 스캔선(글라스 자식)이 항상 고정 헤드 토출점 바로 아래에 오도록 —
        //   헤드 토출점 화면X = NozzleCenterX + HeadFixedX = 130 + 270 = 400
        //   스캔선 화면X      = 132(ScanLine Left) + t·PrintAreaMaxW + GlassX
        //   스테이지 이동     = GlassScanStart → GlassScanStart − PrintAreaMaxW
        //   ⇒ 132 + GlassScanStart = 400 이면 t 와 무관하게 스캔선이 헤드에 고정.
        private const double HeadFixedX     = 270;                          // 고정 헤드 위치(translate)
        private const double GlassScanStart = 268;                          // 인쇄 시작 시 스테이지 위치(= 400 − 132)
        private const double GlassScanEnd   = GlassScanStart - PrintAreaMaxW; // 인쇄 종료 시 스테이지 위치

        // ── Phase 시간표 (초) ────────────────────────────────────────
        // Glass 반입/반출은 elapsed 기반 (시작/종료 이벤트가 별도로 없음)
        // Head 관련 phase는 step 진입 시각을 기점으로 한 duration만 사용
        private const double T_GlassLoadStart    = 0.0;
        private const double T_GlassLoadDur      = 1.5;
        private const double T_HeadPosDur        = 0.7;
        private const double T_ScanDur           = 2.5;   // 실제 모터 페이스에 가깝게 (이전 5.0초 → 2.5초)
        private const double T_HeadParkDur       = 0.7;
        private const double T_GlassUnloadStart  = 6.2;   // ScanDur 단축 반영 (1.7+0.7+2.5+0.7 + 여유 0.6)
        private const double T_GlassUnloadDur    = 1.5;
        private const double T_TotalCycle        = 7.7;   // GlassUnloadStart + GlassUnloadDur

        private MainDashboardViewModel? _vm;

        public MainDashboardView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            DataContextChanged += OnDataContextChanged;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            CreateNozzleDots();
        }

        // ── ViewModel 이벤트 구독/해제 ──────────────────────────────
        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            UnsubscribeFromViewModel();
            if (e.NewValue is MainDashboardViewModel vm)
            {
                _vm = vm;
                _vm.AutoPrintStarted     += OnAutoPrintStarted;
                _vm.AutoPrintStepChanged += OnStepChanged;
                _vm.AutoPrintAborted     += StopAnimation;
                _vm.AutoPrintCompleted   += StopAnimation;
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
            => UnsubscribeFromViewModel();

        private void UnsubscribeFromViewModel()
        {
            if (_vm == null) return;
            _vm.AutoPrintStarted     -= OnAutoPrintStarted;
            _vm.AutoPrintStepChanged -= OnStepChanged;
            _vm.AutoPrintAborted     -= StopAnimation;
            _vm.AutoPrintCompleted   -= StopAnimation;
            _vm = null;
        }

        // ── 노즐 점 생성 ────────────────────────────────────────────
        private void CreateNozzleDots()
        {
            const int    count    = 6;
            const double spacingX = 10;

            for (int i = 0; i < count; i++)
            {
                var tX = new TranslateTransform { X = HeadFixedX };
                var dot = new Ellipse
                {
                    Width = 5, Height = 5,
                    Fill = new SolidColorBrush(Color.FromRgb(167, 139, 250)),
                    Opacity = 0.85,
                    RenderTransform = tX,
                    Effect = new DropShadowEffect
                    {
                        Color = Color.FromRgb(124, 58, 237),
                        BlurRadius = 4, ShadowDepth = 0, Opacity = 0.8
                    }
                };
                Canvas.SetLeft(dot, NozzleCenterX - 25 + i * spacingX);
                Canvas.SetTop(dot, NozzleBaseY);
                MainCanvas.Children.Add(dot);
                _nozzleXTransforms.Add(tX);
            }
        }

        private void SyncNozzleX(double x)
        {
            foreach (var t in _nozzleXTransforms) t.X = x;
        }

        // ── 보간 / 이징 헬퍼 ────────────────────────────────────────
        private static double Lerp(double a, double b, double t) => a + (b - a) * t;
        private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);
        private static double EaseInCubic(double t) => t * t * t;

        // ── CompositionTarget.Rendering 후킹 ────────────────────────
        // V-sync에 맞춰 호출되어 DispatcherTimer보다 frame jitter가 적음.
        // 시그니처가 EventHandler(object?, EventArgs)로 OnFrameTick과 동일하므로 그대로 연결.
        private void HookRendering()
        {
            if (_renderingHooked) return;
            CompositionTarget.Rendering += OnFrameTick;
            _renderingHooked = true;
        }

        private void UnhookRendering()
        {
            if (!_renderingHooked) return;
            CompositionTarget.Rendering -= OnFrameTick;
            _renderingHooked = false;
        }

        // step 전환 시점의 기대 head 위치로 즉시 스냅 — 다음 OnFrameTick까지 점프 방지
        // 헤드 고정 구조 — step 전환 시에도 헤드는 항상 고정 위치(점프 방지용 스냅만 유지).
        private void SnapHeadForStep(int stepNumber)
        {
            HeadXTransform.X     = HeadFixedX;
            HeadLabelTransform.X = HeadFixedX;
            SyncNozzleX(HeadFixedX);
        }

        // 한 phase 진행률(0~1) — 시작 전 0, 끝난 뒤 1
        private static double PhaseT(double elapsed, double start, double dur)
        {
            if (elapsed <= start) return 0;
            if (elapsed >= start + dur) return 1;
            return (elapsed - start) / dur;
        }

        // ── 절차적 애니메이션 메인 루프 ─────────────────────────────
        private void OnFrameTick(object? sender, EventArgs e)
        {
            DateTime now = DateTime.Now;
            double elapsed = (now - _animStart).TotalSeconds;

            // [진단] 프레임 간격 스파이크 감지
            if (DiagEnabled && _lastFrameAt != default)
            {
                double frameMs = (now - _lastFrameAt).TotalMilliseconds;
                if (frameMs > FrameSpikeMs)
                    Debug.WriteLine($"[DASH] FRAME SPIKE  +{frameMs:F0}ms  step={_currentStepNo} elapsed={elapsed:F2}s");
            }
            _lastFrameAt = now;

            // ── 진행 상태 판정 ──
            // 15단계 시퀀스에서 진공 해제(반출)는 step 13.
            const int VacuumOffStepNo = 13;
            bool unloadStarted    = _stepTimes.TryGetValue(VacuumOffStepNo, out double unloadStart);
            bool isPrintingNow    = _currentStepNo == PrintScanStepNo;
            bool printAlreadyDone = _currentStepNo > PrintScanStepNo;

            // ── 인쇄 진행률 t (0..1) ──
            // 실장/실연결이면 스캔축(Y) 모터 위치 기준, 아니면 step 9 진입 시각 기반 스크립트.
            double liveScanMm = _vm?.GetLiveScanMm() ?? 0.0;
            bool hasRange = _vm != null && _vm.HasPrintRange;
            double t;
            if (hasRange)
            {
                double rangeMm = _vm!.PrintEndScanMm - _vm.PrintStartScanMm;
                t = Math.Abs(rangeMm) < 1e-6 ? 0.0
                    : Math.Clamp((liveScanMm - _vm.PrintStartScanMm) / rangeMm, 0.0, 1.0);
            }
            else
            {
                t = (isPrintingNow && _stepTimes.TryGetValue(PrintScanStepNo, out var t9))
                    ? Math.Clamp((elapsed - t9) / T_ScanDur, 0.0, 1.0)
                    : 0.0;
            }
            if (printAlreadyDone) t = 1.0;

            // ── 헤드: 고정 (실장 구조 — 헤드는 움직이지 않음) ──
            HeadXTransform.X     = HeadFixedX;
            HeadLabelTransform.X = HeadFixedX;
            SyncNozzleX(HeadFixedX);

            // ── 스테이지(글라스) X: 반입(우→스캔시작) → 스캔(Y 진행률로 고정 헤드 밑 통과) → 반출(좌로 배출) ──
            double glassX;
            if (unloadStarted)
            {
                double tu = EaseInCubic(PhaseT(elapsed, unloadStart, T_GlassUnloadDur));
                glassX = Lerp(GlassScanEnd, GlassParkedL, tu);            // 스캔 종료 위치 → 좌측 배출
            }
            else if (_currentStepNo >= PrintScanStepNo)
            {
                glassX = Lerp(GlassScanStart, GlassScanEnd, t);           // 스캔: 스테이지가 고정 헤드 밑을 통과
            }
            else
            {
                double tl = EaseOutCubic(PhaseT(elapsed, T_GlassLoadStart, T_GlassLoadDur));
                glassX = Lerp(GlassParkedR, GlassScanStart, tl);          // 반입: 우측에서 스캔 시작 위치로
            }
            GlassTransform.X = glassX;

            // [진단] 스캔축 점프 감지
            if (DiagEnabled && !double.IsNaN(_lastScanMm))
            {
                double motorDelta = Math.Abs(liveScanMm - _lastScanMm);
                if (motorDelta > MotorJumpMm)
                    Debug.WriteLine($"[DASH] SCAN JUMP  {_lastScanMm:F2}→{liveScanMm:F2} (Δ{motorDelta:F2}mm)  step={_currentStepNo} t={t:F2}");
            }
            _lastScanMm = liveScanMm;

            // 하단 표시는 스캔축(이송축) 실제 모터 mm — bottom MOTOR POSITION Y와 일치
            UpdateScanDisplayMm(liveScanMm);

            // ── 인쇄 영역 채움 + 스캔선(고정 헤드 바로 아래) ──
            // 상수 정합상 스캔선 화면X = 132 + t·PrintAreaMaxW + glassX = 고정 헤드 토출점(t 무관).
            if (printAlreadyDone) _maxScanT = 1.0;                        // 인쇄 지나갔으면 100% 잠금
            else if (isPrintingNow) _maxScanT = Math.Max(_maxScanT, t);
            PrintedAreaScale.ScaleX = _maxScanT;

            if (isPrintingNow)
            {
                ScanLineTransform.X = t * PrintAreaMaxW;
                bool inScanRange = t > 0.001 && t < 0.999;
                ScanLine.Opacity = inScanRange ? 1.0 : 0.0;
                _isScanning = inScanRange;
            }
            else
            {
                ScanLine.Opacity = 0;
                _isScanning = false;
            }

            // ── 파티클 업데이트 + 분사 ──
            UpdateParticles();
            if (_isScanning && (++_particleFrameSkip % 2 == 0))
                SpawnInkDrops();   // 30Hz로 throttle — GC 빈도 절반

            // 사이클 종료는 ViewModel의 AutoPrintCompleted/Aborted 이벤트 → StopAnimation()에서 처리.
            // 고정 시간(T_TotalCycle) 게이트는 사용하지 않음 — READY/PRINT 좌표 거리에 따라
            // 시퀀스 길이가 달라지므로 시각도 시퀀스 완료에 종속시킴.
        }

        // ── 파티클 시스템 ──────────────────────────────────────────
        private void UpdateParticles()
        {
            for (int i = _particles.Count - 1; i >= 0; i--)
            {
                var p = _particles[i];
                Canvas.SetTop(p, Canvas.GetTop(p) + 2.2);
                p.Opacity -= 0.04;
                if (p.Opacity <= 0 || Canvas.GetTop(p) > 285)
                {
                    MainCanvas.Children.Remove(p);
                    _particles.RemoveAt(i);
                }
            }
        }

        private void SpawnInkDrops()
        {
            double headCenterX = NozzleCenterX + HeadXTransform.X;

            int count = _rng.Next(3, 6);
            for (int k = 0; k < count; k++)
            {
                double x = headCenterX + _rng.NextDouble() * 50 - 25;
                double y = NozzleBaseY + _rng.NextDouble() * 4;
                if (x < 133 || x > 667) continue;

                byte alpha = (byte)_rng.Next(140, 210);
                var drop = new Ellipse
                {
                    Width = _rng.Next(2, 5),
                    Height = _rng.Next(3, 6),
                    Fill = new SolidColorBrush(Color.FromArgb(alpha, 80, 50, 220)),
                    Opacity = 0.95
                };
                Canvas.SetLeft(drop, x);
                Canvas.SetTop(drop, y);
                MainCanvas.Children.Add(drop);
                _particles.Add(drop);
            }
        }

        // ── 위치·상태 표시 ─────────────────────────────────────────
        // 스캔축(이송축) 실제 모터 mm 값을 그대로 표시 (bottom MOTOR POSITION Y와 동일 소스)
        private void UpdateScanDisplayMm(double motorMm)
        {
            YPosText.Text = $"GY : {motorMm,8:F3} mm";
        }

        private void SetStatus(string text, string hexColor)
        {
            StatusText.Text = text;
            StatusText.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(hexColor));
        }

        // ── 변환 초기화 ────────────────────────────────────────────
        private void ResetTransforms()
        {
            GlassTransform.X        = GlassParkedR;   // 반입은 우측에서 시작
            HeadXTransform.X        = HeadFixedX;     // 헤드 고정
            HeadLabelTransform.X    = HeadFixedX;
            PrintedAreaScale.ScaleX = 0;
            ScanLineTransform.X     = 0;
            ScanLine.Opacity        = 0;
            SyncNozzleX(HeadFixedX);
        }

        // ── ViewModel.AutoPrintStarted ──────────────────────────────
        private void OnAutoPrintStarted()
        {
            Dispatcher.Invoke(() =>
            {
                _isAnimating    = true;
                _isScanning     = false;
                _currentStepNo  = 0;
                _maxScanT       = 0;
                _stepTimes.Clear();
                UnhookRendering();

                ResetTransforms();
                SetStatus("▶  STARTING ...", "#38BDF8");
                YPosText.Text = "GY :    0.000 mm";

                _animStart    = DateTime.Now;
                _lastFrameAt  = default;
                _lastScanMm   = double.NaN;
                if (DiagEnabled) Debug.WriteLine("[DASH] === AUTO PRINT STARTED ===");
                HookRendering();
            });
        }

        // ── ViewModel.AutoPrintStepChanged — 상태 텍스트 + step 번호 추적 ──
        private void OnStepChanged(int stepNumber)
        {
            if (!_isAnimating) return;
            Dispatcher.Invoke(() =>
            {
                double elapsedAtStep = (DateTime.Now - _animStart).TotalSeconds;
                bool isReentry = _stepTimes.ContainsKey(stepNumber);
                _currentStepNo = stepNumber;
                // step 진입 시각은 사이클당 1회만 기록 — 알람 일시정지 후 재개로 인한
                // 재실행 시 시각이 덮어써져 스크립트 phase가 처음부터 재시작되는 문제 방지
                if (!isReentry)
                    _stepTimes[stepNumber] = elapsedAtStep;

                if (DiagEnabled)
                {
                    double scanMm = _vm?.GetLiveScanMm() ?? 0.0;
                    Debug.WriteLine(
                        $"[DASH] STEP {stepNumber}{(isReentry ? " (RETRY)" : "")}  " +
                        $"elapsed={elapsedAtStep:F2}s  scan={scanMm:F2}mm  " +
                        $"hasRange={_vm?.HasPrintRange} (start={_vm?.PrintStartScanMm:F2} end={_vm?.PrintEndScanMm:F2})");
                }

                // 디스패처 지연으로 OnFrameTick의 첫 반영이 지연되면 head가 점프해 보임 →
                // step 전환 시각의 기대 위치를 즉시 스냅해 첫 프레임의 시작점을 정렬
                SnapHeadForStep(stepNumber);

                switch (stepNumber)
                {
                    case 1: SetStatus("▶  LOADING  ·  GLASS SUBSTRATE ENTERING ...", "#38BDF8"); break;
                    case 2: SetStatus("⊙  VACUUM ON  ·  GLASS CLAMPED", "#22C55E"); break;
                    case 3:
                    case 4: SetStatus("⬇  PRINT HEAD  ·  POSITIONING TO SCAN START", "#60A5FA"); break;
                    case 5:
                    case 6: SetStatus("◉  PRINTING  ·  INKJET PRINTING IN PROGRESS ...", "#A78BFA"); break;
                    case 7: SetStatus("⊘  VACUUM OFF  ·  GLASS RELEASED", "#F59E0B"); break;
                    case 8:
                    case 9: SetStatus("◀  UNLOADING  ·  HEAD PARK + GLASS EXITING ...", "#38BDF8"); break;
                }
            });
        }

        // ── STOP / Completion ──────────────────────────────────────
        private void StopAnimation()
        {
            Dispatcher.Invoke(() =>
            {
                if (!_isAnimating) return;
                _isAnimating = false;
                _isScanning  = false;
                UnhookRendering();

                foreach (var p in _particles)
                    MainCanvas.Children.Remove(p);
                _particles.Clear();

                SetStatus("⏹  STOPPED  ·  PRESS START TO RESTART", "#F59E0B");
            });
        }

        // ── 알람 팝업 테스트 ────────────────────────────────────────
        private void OpenAlarm_Click(object sender, RoutedEventArgs e)
        {
            // Application.Current.MainWindow 는 로그인 시점에 등록된 LoginWindow 일 수 있어
            // Windows 컬렉션에서 실제 MainWindow 타입을 직접 찾는다
            var mainWin = System.Windows.Application.Current.Windows
                .OfType<MainWindow>()
                .FirstOrDefault();
            
            if (mainWin?.DataContext is MainViewModel mainVM)
                mainVM.AlarmVM.RaiseAlarm("SNS-EMO");
        }
    }
}
