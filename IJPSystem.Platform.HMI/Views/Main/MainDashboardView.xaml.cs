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
        private double _passFill;                  // 현재 스와스 밴드의 채움률(0..1) — 패스 내 단조 증가
        private int    _currentPassIndex;          // 진행 중인 패스(0-based). 바뀌면 _passFill 리셋
        // ── 애니메이션 기준 스텝 — <b>번호를 코드에 박지 않는다</b> ─────────────
        //
        // 예전에는 8(인쇄)·6(헤드다운)·10(헤드업)·4(시작이동)을 상수로 뒀다. 그러면 시퀀스 앞에
        // 단계가 하나만 끼어도 전부 어긋난다 — 글라스 정렬 16단계를 넣자 애니메이션이 통째로
        // 밀렸다(2026-08-26). 그래서 사이클을 시작할 때 <b>스텝 이름으로 번호를 찾아</b> 채운다.
        private int _printScanStepNo = 8;
        private int _headDownStepNo  = 6;
        private int _headUpStepNo    = 10;
        private int _moveStartStepNo = 4;
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
        private const double PrintAreaMaxW  =  356;   // 글라스 폭(360)과 맞춘다 — 헤드가 글라스 한쪽 끝에서 반대쪽 끝까지 지나간다
        // PrintedArea/PrintedDoneArea Rectangle 의 Height 와 반드시 같아야 한다(스와스 밴드 높이 계산 기준).
        private const double PrintedAreaH   =  174;

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
        private const double HeadFixedX     = 270;                          // 고정 헤드 위치(translate, 수평)
        private const double GlassScanStart = 268;                          // 인쇄 시작 시 스테이지 위치(= 400 − 132)
        private const double GlassScanEnd   = GlassScanStart - PrintAreaMaxW; // 인쇄 종료 시 스테이지 위치

        // ── 정렬 구간 표시 ────────────────────────────────────────────────
        //
        // 정렬 자리와 마크2 자리는 인쇄 구간보다 <b>훨씬 멀리</b> 있다. 이 장비는 인쇄가
        // Y 120~270mm 인데 정렬이 274mm, 마크2 가 434mm 다 — 인쇄 축척(2.9px/mm)으로 그리면
        // 마크2 가 화면 밖 650px 지점이라 정렬 이동이 통째로 안 보인다(실제로 그랬다).
        //
        // 그래서 정렬 구간만 <b>압축해서</b> 그린다. 같은 160mm 가 인쇄 때보다 작게 보이지만,
        // 화면의 요점은 "그 사이를 오간다"는 사실이지 거리의 절대값이 아니다.
        // 어차피 이 그림은 축척도가 아니다 — Ready→시작 구간과 인쇄 구간도 이미 축척이 다르다.
        private const double AlignHomeX   =   40;    // 마크1 자리 — 글라스가 카메라를 덮는 자리
        private const double AlignPxPerMm = 0.94;    // 160mm ↔ 150px
        private const double AlignMinX    = -130;    // 더 왼쪽이면 글라스가 화면을 벗어난다

        // ── 헤드 Z(수직 승강) — 이미지처럼 헤드가 글라스로 하강/상승 ──
        // HeadDown(step 6)에서 하강, HeadUp(step 10)에서 상승. 상승은 스테이지(Y) 복귀와 동시에 진행.
        private const double HeadZUp        = 0;    // 상승(파킹) 위치
        private const double HeadZDown      = 34;   // 하강(인쇄) 위치 — 글라스 근접

        // ── 헤드 크로스스캔(X, 갠트리 스텝오버) ──────────────────────────
        // 화면은 헤드를 글라스 위쪽에 두고 아래로 분사하는 구성이라, X 를 밴드에 픽셀 단위로
        // 정렬시키면 헤드가 글라스를 덮어버린다(헤드 하단 220 + Z 34 = 254, 글라스 상단 258).
        // 그래서 방향만 맞추고(밴드가 위로 쌓이므로 헤드도 위로) 이동량은 압축해 표시한다.
        // 실제 X 값은 헤드 옆 라벨에 mm 로 같이 띄워 과장 표현이 되지 않게 한다.
        private const double HeadCrossSpanPx = 36;   // 전체 스텝오버에 대응하는 헤드 화면 이동량

        // ※ 정렬축(T)은 이 애니메이션에서 다루지 않는다.
        //    실제 보정각(±0.1°)은 화면에서 식별되지 않는 반면, 글라스를 회전시키면 그 안의
        //    인쇄영역·스캔선·그리드가 함께 돌아 정상 동작하던 인쇄 표시가 깨진다.
        //    T 값은 하단 MOTOR POSITION 패널에서 숫자로 확인한다.

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
            // 이미 가동 중인 VM 에 재진입한 경우(정지→다른 화면→복귀) 애니메이션을 이어서 복원.
            ResumeAnimationIfRunning();
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

                // DataContext 가 로드 이후에 붙는 경우에도 복원(순서 무관 안전망).
                if (IsLoaded) ResumeAnimationIfRunning();
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // 언로드된 뷰가 CompositionTarget.Rendering 훅을 계속 물고 있지 않도록 정리.
            UnhookRendering();
            _isAnimating = false;
            UnsubscribeFromViewModel();
        }

        // 화면 전환 후 재진입 시, VM 이 아직 가동 중이면 애니메이션을 현재 스텝부터 이어 재개.
        // (AutoPrintStarted 는 사이클 시작에만 발생하므로, 중간 재진입한 새 View 는 이 경로로 복원한다.)
        private void ResumeAnimationIfRunning()
        {
            if (_isAnimating) return;                 // 이미 애니 중이면 건너뜀
            if (_vm == null || !_vm.IsRunning) return;
            if (_vm.CurrentStepNumber <= 0) return;   // 스텝 진입 전(STARTING) — 곧 이벤트로 시작됨

            ResolveStepNumbers();
            _isAnimating      = true;
            _isScanning       = false;
            _currentStepNo    = _vm.CurrentStepNumber;
            _passFill         = 0;
            _currentPassIndex = 0;
            _stepTimes.Clear();       // 시간기반 phase 는 비움 → 완료(1.0)로 간주(헤드 이미 하강 등)
            _animStart        = DateTime.Now;
            _lastFrameAt      = default;
            _lastScanMm       = double.NaN;

            // 글라스/헤드 위치는 OnFrameTick 이 실모터 위치(GetLiveScanMm)로 매 프레임 맞추므로,
            // 여기서는 렌더 훅만 걸면 다음 프레임부터 현재 위치로 자연스럽게 이어진다.
            SetStatus("▶  RESUMING ...", "#38BDF8");
            HookRendering();
        }

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

        // 헤드 Z(수직) 승강에 맞춰 노즐 점도 함께 상하 이동
        private void SyncNozzleY(double y)
        {
            foreach (var t in _nozzleXTransforms) t.Y = y;
        }

        // ── 보간 / 이징 헬퍼 ────────────────────────────────────────
        private static double Lerp(double a, double b, double t) => a + (b - a) * t;
        private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);
        private static double EaseInCubic(double t) => t * t * t;
        private static double EaseInOutCubic(double t) =>
            t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

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

            // ── 진행 상태 판정 (프린팅수 swath + 방향에 따라 스텝 구성이 달라짐) ──
            // 스텝 구성: 1~7 준비/헤드다운, 8~ 패스 루프, 이후 HeadUp/HeadUpDone/VacuumOff/MoveReady/MoveReadyDone.
            //   양방향 패스블록(4): 0:Print 1:PrintDone 2:SwathStep 3:SwathStepDone
            //   단방향 패스블록(6): 0:Print 1:PrintDone 2:Return 3:ReturnDone 4:SwathStep 5:SwathStepDone
            //   (마지막 패스는 스텝오버가 없어 2칸 짧음 — headUpStep 식이 이를 흡수)
            //   ※ 멀티 스와스는 headLength>0 전제(스텝오버 존재). 이 전제는 시퀀스와 동일.
            int swath          = Math.Max(1, _vm?.SwathCount ?? 1);
            bool bidi          = _vm?.IsBidirectional ?? true;
            int stepsPerPass   = bidi ? 4 : 6;
            // 헤드업 스텝은 이름으로 찾은 값을 쓴다 — 식으로 계산하면 앞에 단계가 끼는 순간 어긋난다.
            int headUpStep     = _headUpStepNo;
            // 헤드 UP 과 READY 복귀가 한 스텝에서 동시에 시작한다(AutoPrintSequence 참조).
            // 꼬리 구성: [HeadUpAndMoveReady] → Done → VacuumOff.
            int moveReadyStep  = headUpStep;
            bool inPassRegion  = _currentStepNo >= _printScanStepNo && _currentStepNo < headUpStep;
            int within         = inPassRegion ? (_currentStepNo - _printScanStepNo) % stepsPerPass : -1;
            bool isPrintStep   = inPassRegion && within == 0;   // 잉크 토출(스캔) 진행
            // 단방향 복귀(within==2)는 isPrintStep=false 라 스캔선/잉크가 자동으로 꺼진다(별도 처리 불필요).
            // 글라스는 복귀 구간에도 실모터 위치(t)를 따라가므로 End→Start 로 자연히 되돌아간다.
            bool afterAllPasses = _currentStepNo >= headUpStep;  // 모든 패스 종료(헤드업~복귀)

            // ── 인쇄 진행률 t (0..1) — 스캔축(Y) 모터 위치 기준. 패스 방향(정/역)은 t가 0↔1로 자연 반영 ──
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
                t = (isPrintStep && _stepTimes.TryGetValue(_currentStepNo, out var tp))
                    ? Math.Clamp((elapsed - tp) / T_ScanDur, 0.0, 1.0)
                    : 0.0;
            }

            // ── 헤드 수평(X 갠트리는 화면 세로가 크로스스캔이므로 수평은 고정) ──
            HeadXTransform.X     = HeadFixedX;
            HeadLabelTransform.X = HeadFixedX;
            SyncNozzleX(HeadFixedX);

            // ── 헤드 승강(Z) ──
            // 티칭(PRINT HEAD UP/DOWN)이 있으면 실측 Z 에 동기, 없으면 기존 스크립트로 폴백.
            //   Z 실측 ∈ [Up, Down] → 화면 [HeadZUp, HeadZDown]
            double headZ;
            if (_vm != null && _vm.HasLiftMapping)
            {
                double span = _vm.HeadDownLiftMm - _vm.HeadUpLiftMm;
                double p = Math.Clamp((_vm.GetLiveLiftMm() - _vm.HeadUpLiftMm) / span, 0.0, 1.0);
                headZ = Lerp(HeadZUp, HeadZDown, p);
            }
            else if (_currentStepNo < _headDownStepNo)
            {
                headZ = HeadZUp;                                     // 하강 전 — 상승 파킹
            }
            else if (_currentStepNo < headUpStep)
            {
                double td = _stepTimes.TryGetValue(_headDownStepNo, out var tDown)
                    ? EaseInOutCubic(PhaseT(elapsed, tDown, T_HeadPosDur)) : 1.0;
                headZ = Lerp(HeadZUp, HeadZDown, td);                // 하강 → 인쇄 위치(전 패스) 유지
            }
            else
            {
                double tu2 = _stepTimes.TryGetValue(headUpStep, out var tUp)
                    ? EaseInOutCubic(PhaseT(elapsed, tUp, T_HeadPosDur)) : 1.0;
                headZ = Lerp(HeadZDown, HeadZUp, tu2);               // 상승(마지막 패스 후)
            }

            // ── 헤드 크로스스캔(X 스텝오버) ──
            // 실측 X 가 있으면 (현재−시작)/전체이동량, 없으면 패스 인덱스로 폴백.
            // 밴드가 아래→위로 쌓이므로 헤드도 위(−Y)로 이동시켜 방향을 일치시킨다.
            int passIdx = inPassRegion ? (_currentStepNo - _printScanStepNo) / stepsPerPass : 0;
            double crossP;
            if (_vm != null && _vm.HasStepMapping)
                crossP = Math.Clamp((_vm.GetLiveStepMm() - _vm.StepOriginMm) / _vm.StepSpanMm, 0.0, 1.0);
            else
                crossP = swath > 1 ? (double)Math.Min(passIdx, swath - 1) / (swath - 1) : 0.0;

            double headCross = -HeadCrossSpanPx * crossP;

            // Z(하강)와 X(크로스스캔)를 같은 화면 세로축에 더한다 — 서로 범위가 달라 상쇄되지 않는다.
            double headScreenY   = headZ + headCross;
            HeadXTransform.Y     = headScreenY;
            HeadLabelTransform.Y = headScreenY;
            SyncNozzleY(headScreenY);

            UpdateStepDisplayMm(_vm?.GetLiveStepMm() ?? 0.0);

            // ── 스테이지(글라스) X — 실제 Y모터에 동기 ──
            //  1~3: 우측 Ready 파킹 대기 / 4~7: Ready→PrintStart 반입(Y 동기) /
            //  8~12: 스캔(PrintStart→PrintEnd, Y 동기) / 13~: PrintEnd→Ready 복귀(오른쪽 방향, Y 동기)
            //  실측 매핑(HasReadyMapping) 있으면 Y모터 위치로 동기, 없으면 스텝 진입시각 스크립트.
            bool hasReadyMap = _vm != null && _vm.HasReadyMapping;
            // 마지막 패스 종료 위치.
            //  - 단방향: 매 패스 Return 으로 항상 Start 복귀 → 마지막도 Start (bidi 아니면 무조건 false)
            //  - 양방향: 방향 교대(복귀 없음) → 홀수 swath 는 End, 짝수는 Start
            // ※ bidi 를 빼먹으면 단방향에서 마지막을 End 로 오판해, MoveReady(예: 27스텝) 진입 시
            //   글라스가 순간 반대 방향으로 튀었다가 Ready 로 이동한다.
            bool lastForward = bidi && (swath % 2 == 1);
            double scanEndX  = lastForward ? GlassScanEnd : GlassScanStart;
            double lastEndMm = _vm != null ? (lastForward ? _vm.PrintEndScanMm : _vm.PrintStartScanMm) : 0.0;

            double glassX;
            if (_currentStepNo >= moveReadyStep)
            {
                // 복귀: 마지막 패스 종료 위치 → 우측 Ready (오른쪽 방향)
                double denom = _vm != null ? _vm.ReadyScanMm - lastEndMm : 0.0;
                double p = (hasReadyMap && Math.Abs(denom) > 1e-6)
                    ? Math.Clamp((liveScanMm - lastEndMm) / denom, 0.0, 1.0)
                    : (_stepTimes.TryGetValue(moveReadyStep, out var tr)
                        ? EaseInOutCubic(PhaseT(elapsed, tr, T_GlassLoadDur)) : 1.0);
                glassX = Lerp(scanEndX, GlassParkedR, p);
            }
            else if (_currentStepNo >= _printScanStepNo)
            {
                glassX = Lerp(GlassScanStart, GlassScanEnd, t);          // 스캔(t: 실측 Y 또는 스크립트)
            }
            else if (_currentStepNo >= _moveStartStepNo)
            {
                // 반입: 지금 있는 자리 → 인쇄 시작 위치 (Y 동기)
                //
                // 예전에는 Ready→PrintStart 를 0..1 로 <b>잘라서</b> 썼다. 그러면 이 단계에 들어올 때
                // 스테이지가 그 구간 밖에 있으면 진행률이 0 이나 1 로 잘려 글라스가 한쪽 끝으로 튄다 —
                // 정렬을 넣으면서 실제로 그렇게 됐다(정렬은 마크2 재확인 자리에서 끝난다).
                //
                // 정렬 구간과 <b>같은 눈금</b>으로 실측 Y 를 그리면 경계에서 이어진다.
                glassX = hasReadyMap
                    ? ScanMmToScreenX(liveScanMm)
                    : Lerp(GlassParkedR, GlassScanStart,
                           _stepTimes.TryGetValue(_moveStartStepNo, out var tm)
                               ? EaseOutCubic(PhaseT(elapsed, tm, T_GlassLoadDur)) : 1.0);
            }
            else if (_vm?.IsAligning == true)
            {
                // 정렬 구간은 두 토막이다 — 파킹에서 마크1 자리로 들어오는 반입, 그리고 마크1↔마크2 왕복.
                //
                // ★ 어느 토막인지를 <b>Y 의 대소</b>로 가르면 안 된다. 예전에는 마크2 가 마크1 보다
                //   Y 가 크다고 보고 liveScanMm > home 일 때만 왕복을 그렸는데, 이 장비는 마크2 가
                //   -Y 쪽(266.7 → 116.7mm)이라 그 가지에 영영 못 들어갔다. 반입 가지의 진행률은
                //   [0,1] 로 잘리므로 -150mm 를 가는 내내 값이 한쪽 끝에 박혀, 정렬 구간 전체에서
                //   글라스가 제자리에 붙어 있었다(2026-08-28 실장).
                //
                //   그래서 <b>마크2 방향</b>을 기준으로 판단한다. t2 는 마크1 에서 0, 마크2 에서 1 이라
                //   +Y 장비든 -Y 장비든 같은 식이 그대로 돈다.
                double home  = _vm.GlassAlignScanMm;
                double mark2 = _vm.GlassAlignMark2ScanMm;
                double x;

                if (double.IsNaN(home) || !hasReadyMap)
                {
                    x = GlassParkedR;                                    // 티칭이 없으면 파킹 그대로
                }
                else
                {
                    double span = double.IsNaN(mark2) ? 0.0 : mark2 - home;
                    double t2   = Math.Abs(span) < 1e-6 ? 0.0 : (liveScanMm - home) / span;

                    if (t2 > 0)
                    {
                        // 마크1 ↔ 마크2 왕복 — 압축 축척으로 그린다.
                        // 화면에서는 늘 왼쪽이 마크2 다(기계가 +Y 로 가든 -Y 로 가든).
                        double mark2X = Math.Max(AlignMinX, AlignHomeX - Math.Abs(span) * AlignPxPerMm);
                        x = Lerp(AlignHomeX, mark2X, Math.Min(t2, 1.0));
                    }
                    else
                    {
                        // 파킹 → 정렬 자리 반입. 양 끝이 파킹·정렬자리와 정확히 맞아 이어진다.
                        double denom = home - _vm.ReadyScanMm;
                        double p = Math.Abs(denom) < 1e-6 ? 1.0
                                 : Math.Clamp((liveScanMm - _vm.ReadyScanMm) / denom, 0.0, 1.0);
                        x = Lerp(GlassParkedR, AlignHomeX, p);
                    }
                }

                glassX = Math.Clamp(x, AlignMinX, GlassParkedR);
            }
            else
            {
                glassX = GlassParkedR;                                   // 1~3: 우측 Ready 파킹 대기
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
            //
            // 스와스마다 글라스 폭(화면 세로)의 1/swath 씩 아래에서 위로 쌓인다.
            //   완료 밴드(PrintedDoneArea): 전폭 × (완료 패스수/swath) — 바닥 고정, 위로 성장
            //   현재 밴드(PrintedArea)    : 1/swath 높이 × 스캔 진행률 — 해당 밴드 위치로 이동
            int passIndex = passIdx;   // 0-based (헤드 크로스스캔 계산에서 이미 구함)
            if (passIndex != _currentPassIndex)
            {
                _currentPassIndex = passIndex;
                _passFill = 0;                       // 새 패스 시작 → 현재 밴드 채움 초기화
            }

            // 인쇄 주행 방향: 양방향은 패스마다 교대(짝수 패스=역방향), 단방향은 항상 정방향(Start→End).
            // 역방향은 t 가 1→0 이므로 채움은 (1−t), 우측에서 자란다.
            bool passForward = bidi ? (passIndex % 2 == 0) : true;
            if (isPrintStep)
            {
                double fill = passForward ? t : 1.0 - t;
                _passFill = Math.Max(_passFill, fill);       // 패스 내 단조 증가
            }
            else if (inPassRegion) _passFill = 1.0;          // 인쇄 외 구간(PrintDone/Return/SwathStep) — 해당 밴드는 다 찼다

            double bandH   = PrintedAreaH / swath;
            int donePasses = afterAllPasses ? swath : passIndex;

            PrintedDoneScale.ScaleY = (double)donePasses / swath;

            if (afterAllPasses)
            {
                PrintedAreaScale.ScaleX = 0;                 // 완료 밴드가 전부 덮으므로 현재 밴드는 숨김
            }
            else
            {
                PrintedArea.RenderTransformOrigin = new Point(passForward ? 0 : 1, 1);
                PrintedAreaScale.ScaleY = 1.0 / swath;
                PrintedAreaScale.ScaleX = _passFill;
                PrintedAreaOffset.Y     = -passIndex * bandH;
            }

            if (isPrintStep)
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
                // 헤드 Z 하강분(HeadXTransform.Y)만큼 분사 시작점도 내려 글라스에 근접시킴
                double y = NozzleBaseY + HeadXTransform.Y + _rng.NextDouble() * 4;
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

        // 크로스스캔(X) 실측 표기. 헤드의 화면 이동량은 압축 표기라, 실제 값을 숫자로
        // 함께 보여야 상태를 오해 없이 읽을 수 있다.
        private void UpdateStepDisplayMm(double stepMm)
        {
            AxisReadoutText.Text = $"GX : {stepMm,8:F3} mm";
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
            HeadXTransform.X        = HeadFixedX;     // 헤드 수평 고정
            HeadLabelTransform.X    = HeadFixedX;
            HeadXTransform.Y        = HeadZUp;        // 헤드 수직 — 상승 파킹
            HeadLabelTransform.Y    = HeadZUp;
            PrintedAreaScale.ScaleX = 0;
            PrintedAreaOffset.Y     = 0;
            PrintedDoneScale.ScaleY = 0;      // 쌓인 스와스 밴드도 비운다
            ScanLineTransform.X     = 0;
            ScanLine.Opacity        = 0;
            SyncNozzleX(HeadFixedX);
            SyncNozzleY(HeadZUp);
        }


        /// <summary>
        /// 스캔축(Y) mm → 글라스 화면 X.
        ///
        /// <para>Ready ↔ PrintStart 두 티칭점이 이미 화면 두 자리에 대응돼 있다. 그 눈금을 그대로
        /// 늘려 쓰면 정렬처럼 <b>인쇄 구간 밖</b>을 오가는 이동도 같은 축척으로 그려진다 —
        /// 구간마다 축척이 다르면 같은 100mm 가 어디서는 크게, 어디서는 작게 보인다.</para>
        ///
        /// <para>화면 밖으로 나가면 글라스가 사라져 버리므로 이동 범위 안으로 접는다.</para>
        /// </summary>
        private double ScanMmToScreenX(double scanMm)
        {
            if (_vm == null) return GlassParkedR;

            double denom = _vm.PrintStartScanMm - _vm.ReadyScanMm;
            if (double.IsNaN(denom) || Math.Abs(denom) < 1e-6) return GlassParkedR;

            double px = GlassParkedR + (scanMm - _vm.ReadyScanMm) * (GlassScanStart - GlassParkedR) / denom;
            return Math.Clamp(px, Math.Min(GlassScanEnd, GlassParkedR), Math.Max(GlassScanEnd, GlassParkedR));
        }
        // ── ViewModel.AutoPrintStarted ──────────────────────────────

        /// <summary>
        /// 애니메이션 기준 스텝 번호를 <b>스텝 이름으로</b> 다시 찾는다.
        ///
        /// <para>사이클마다 부른다 — 프린팅수·방향·글라스 정렬 사용 여부에 따라 스텝 구성이
        /// 달라지기 때문이다. 못 찾으면 옛 기본값을 그대로 둔다(애니메이션이 멈추는 것보다 낫다).</para>
        /// </summary>
        private void ResolveStepNumbers()
        {
            if (_vm == null) return;

            int before = _moveStartStepNo + _headDownStepNo + _printScanStepNo + _headUpStepNo;

            int Find(string key, int fallback)
            {
                int n = _vm.StepNumberOf(key);
                return n > 0 ? n : fallback;
            }

            _moveStartStepNo = Find("Step_AutoPrint_MoveStart", _moveStartStepNo);
            _headDownStepNo  = Find("Step_AutoPrint_HeadDown", _headDownStepNo);
            _printScanStepNo = Find("Step_AutoPrint_Print", _printScanStepNo);
            _headUpStepNo    = Find("Step_AutoPrint_HeadUpAndMoveReady", _headUpStepNo);

            if (DiagEnabled && (before != _moveStartStepNo + _headDownStepNo + _printScanStepNo + _headUpStepNo))
                Debug.WriteLine($"[DASH] STEPS  moveStart={_moveStartStepNo} headDown={_headDownStepNo} " +
                                $"print={_printScanStepNo} headUp={_headUpStepNo}");
        }
        private void OnAutoPrintStarted()
        {
            Dispatcher.Invoke(() =>
            {
                ResolveStepNumbers();   // 스텝 구성이 사이클마다 달라진다(프린팅수·정렬 사용 여부)
                _isAnimating    = true;
                _isScanning     = false;
                _currentStepNo  = 0;
                _passFill       = 0;
                _currentPassIndex = 0;
                _stepTimes.Clear();
                UnhookRendering();

                ResetTransforms();
                SetStatus("▶  STARTING ...", "#38BDF8");
                YPosText.Text = "GY :    0.000 mm";
                UpdateStepDisplayMm(0);

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
                // 스텝이 바뀔 때마다 기준 번호를 다시 잡는다 — 시작 신호와 스텝 생성의
                // 순서에 기대면, 그 순서가 바뀌는 날 화면이 조용히 어긋난다.
                ResolveStepNumbers();

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

                // 상태 배너 — 프린팅수 swath 에 따라 스텝 구성이 달라지므로 역할을 동적으로 계산
                int swath        = Math.Max(1, _vm?.SwathCount ?? 1);
                int headUpStep   = 4 * swath + 6;      // S=1→10, S=2→14
                int vacuumOffStep = headUpStep + 2;
                int moveReadyStep = headUpStep + 3;

                if (stepNumber == 1)
                    SetStatus("▶  LOADING  ·  GLASS SUBSTRATE ENTERING ...", "#38BDF8");
                else if (stepNumber <= 3)
                    SetStatus("⊙  VACUUM ON  ·  GLASS CLAMPED", "#22C55E");
                else if (stepNumber <= 5)
                    SetStatus("⬇  PRINT HEAD  ·  POSITIONING TO SCAN START", "#60A5FA");
                else if (stepNumber <= 7)
                    SetStatus("⬇  PRINT HEAD  ·  HEAD DOWN", "#60A5FA");
                else if (stepNumber < headUpStep)
                {
                    // 패스 구간: within 0:Print 1:PrintDone 2:SwathStep 3:SwathStepDone
                    int offset = stepNumber - 8;
                    int pass   = offset / 4 + 1;
                    int within = offset % 4;
                    if (within == 2 || within == 3)
                        SetStatus($"⇄  SWATH STEP-OVER  ·  X + HEAD LENGTH  (pass {pass}→{pass + 1})", "#F59E0B");
                    else
                    {
                        string dir = (pass % 2 == 1) ? "START → END" : "END → START";
                        SetStatus($"◉  PRINTING  ·  PASS {pass}/{swath}  ({dir})", "#A78BFA");
                    }
                }
                else if (stepNumber < vacuumOffStep)
                    SetStatus("⬆  PRINT HEAD  ·  HEAD UP", "#60A5FA");
                else if (stepNumber < moveReadyStep)
                    SetStatus("⊘  VACUUM OFF  ·  GLASS RELEASED", "#F59E0B");
                else
                    SetStatus("◀  RETURN TO READY ...", "#38BDF8");
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

    }
}
