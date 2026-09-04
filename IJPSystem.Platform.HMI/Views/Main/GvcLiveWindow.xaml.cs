using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using IJPSystem.Platform.Application.Sequences;
using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Models.Vision;
using IJPSystem.Platform.HMI.ViewModels;
using IJPSystem.Platform.HMI.Vision;

namespace IJPSystem.Platform.HMI.Views
{
    /// <summary>
    /// 대시보드 위에 뜨는 글라스 정렬 카메라(GVC) 라이브 창.
    ///
    /// <para><b>왜 필요한가</b>: 자동 인쇄가 도는 동안 마크를 제대로 잡고 있는지 보려면 지금은
    /// 정비 → 비전 → 글라스 화면으로 넘어가야 하는데, 그러면 대시보드의 단계 진행을 못 본다.
    /// 정렬은 둘을 <b>같이</b> 봐야 판단이 되는 구간이다 — 어느 단계에서 무엇을 보고 있는지.</para>
    ///
    /// <para><b>수명</b>: 정렬을 쓰는 판에서만 뜬다. 여는·닫는 판단은 대시보드가 하고
    /// (<see cref="MainDashboardView"/>), 이 창은 뜬 동안 프레임을 받아 그리기만 한다.</para>
    ///
    /// <para><b>정렬이 찍는 순간에는 비켜 준다</b>(<see cref="GlassAlignServices.Capturing"/>).
    /// 같은 카메라를 두 곳에서 동시에 꺼내면 "그 사진이 라이브와 겹친 것 아니냐"는 의심을
    /// 나중에 배제할 수 없다. 재는 순간은 한 판에 여덟 번, 매번 1초가 안 된다.</para>
    /// </summary>
    public partial class GvcLiveWindow : Window
    {
        /// <summary>
        /// 목표 주기 [ms] — 글라스 화면과 같은 값(≈15fps).
        ///
        /// <para>처음에 200ms(5fps)로 넣었더니 눈에 띄게 끊겼다(실장 2026-09-02). 정렬 촬상과
        /// 겹칠까 봐 보수적으로 잡았던 것인데, 겹침은 주기가 아니라
        /// <see cref="GlassAlignServices.Capturing"/> 이 막는다 — 주기를 늦출 이유가 없었다.</para>
        ///
        /// <para><b>고정 주기가 아니다</b>: 촬상이 늦으면 그만큼 늦게 다음 판을 걸 뿐,
        /// 틱을 통째로 버리지 않는다.</para>
        /// </summary>
        private const int TickMs = 66;

        /// <summary>
        /// 프레임 한 장을 기다리는 한계 [ms]. 드라이버 기본값(1초)으로 라이브를 돌리면
        /// 한 번 놓칠 때마다 화면이 1초 멈춘다 — 라이브는 한 장 건너뛰는 편이 낫다.
        /// </summary>
        private const int GrabTimeoutMs = 250;

        private readonly MainViewModel _mainVM;
        private readonly DispatcherTimer _timer;
        private readonly LiveFrameBuffer _buffer = new();

        private bool _ticking;
        private bool _closed;
        private int  _failStreak;

        /// <summary>이만큼 잇달아 실패하면 표시를 바꾼다. 한두 번은 정렬이 카메라를 쥐고 있는 정상 상황이다.</summary>
        private const int FailStreakToWarn = 10;

        public GvcLiveWindow(MainViewModel mainVM)
        {
            InitializeComponent();
            _mainVM = mainVM ?? throw new ArgumentNullException(nameof(mainVM));

            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(TickMs),
            };
            _timer.Tick += async (_, _) => await TickAsync();
        }

        /// <summary>
        /// 대시보드의 자리표(<c>GvcLiveSlot</c>) 위로 창을 옮긴다.
        ///
        /// <para>화면 좌표를 직접 박지 않는 이유: 제어 PC 해상도와 개발 PC 해상도가 다르고,
        /// 창을 옮기거나 최대화하면 그 자리가 통째로 움직인다. 자리표를 따라가면 언제나 맞는다.</para>
        /// </summary>
        public void PlaceOver(FrameworkElement slot)
        {
            if (slot == null || !slot.IsVisible || slot.ActualWidth < 1 || slot.ActualHeight < 1) return;

            try
            {
                // PointToScreen 은 <b>장치 픽셀</b>이다. Window.Left/Top 은 DIU 라 배율이 100% 가
                // 아닌 PC 에서 그대로 넣으면 창이 엉뚱한 데로 간다.
                var origin = slot.PointToScreen(new Point(0, 0));
                var source = PresentationSource.FromVisual(slot);
                var toDiu  = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
                var diu    = toDiu.Transform(origin);

                Left   = diu.X;
                Top    = diu.Y;
                Width  = slot.ActualWidth;
                Height = slot.ActualHeight;
            }
            catch (InvalidOperationException)
            {
                // 아직 화면에 올라가지 않은 요소 — 다음 호출에서 자리를 잡는다.
            }
        }

        public void StartLive()
        {
            if (_closed) return;
            _timer.Start();

            // 정렬이 잰 사진을 그대로 띄운다 — 재는 동안 비켜 서 있어도 화면에 이동 중
            // 얼룩이 남지 않고, 매칭이 무엇을 봤는지 눈으로 확인된다.
            GlassAlignServices.FrameMeasured += OnMeasuredFrame;
        }

        private void OnMeasuredFrame(VisionImage img)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closed) return;
                var frame = _buffer.Write(img);
                if (frame == null) return;

                LiveImage.Source = frame;
                WaitText.Visibility = Visibility.Collapsed;
            }));
        }

        private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private async Task TickAsync()
        {
            // 재진입 금지 — 촬상이 주기보다 오래 걸리면 틱이 겹쳐 대기열이 쌓인다.
            if (_ticking || _closed) return;

            // 정렬이 재는 순간은 비켜 준다. 창은 그대로 두고 마지막 프레임을 유지한다 —
            // 여기서 화면을 지우면 재는 동안(한 판에 여덟 번) 깜빡인다.
            if (GlassAlignServices.Capturing) { Rearm(TickMs); return; }

            _ticking = true;
            _timer.Stop();              // 이 판이 끝날 때까지 다음 틱을 걸지 않는다

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var vision = _mainVM.GetController()?.GetMachine()?.Vision;
                if (vision == null) return;

                string camId = GlassViewModel.ResolveCamId(vision, _mainVM);
                var image = await vision.CaptureAsync(camId, saveToDisk: false, GrabTimeoutMs);
                if (!image.IsValid) { Fail(); return; }

                // 버퍼 2장을 번갈아 쓴다 — 프레임마다 새 비트맵을 만들면 대형 객체 힙이 불어난다.
                var frame = _buffer.Write(image);
                if (frame == null) { Fail(); return; }

                LiveImage.Source = frame;
                WaitText.Visibility = Visibility.Collapsed;
                if (_failStreak != 0) { _failStreak = 0; SetState("LIVE", "#22C55E"); }
            }
            catch (Exception ex)
            {
                // 라이브 실패로 화면 로그를 채우지 않는다 — 정렬 로그가 묻힌다.
                LoggerService.WriteToFile("DEBUG", $"[GVC_POPUP] capture failed: {ex.Message}");
                Fail();
            }
            finally
            {
                _ticking = false;

                // 촬상에 쓴 시간을 빼고 남은 만큼만 기다린다 — 늦어도 그만큼만 늦을 뿐,
                // 판을 통째로 버리지 않는다(글라스 화면 LiveTickAsync 와 같은 이유).
                if (!_closed) Rearm(TickMs - sw.Elapsed.TotalMilliseconds);
            }
        }

        /// <summary>다음 틱을 <paramref name="afterMs"/> 뒤로 건다. 이미 늦었으면 곧바로.</summary>
        private void Rearm(double afterMs)
        {
            var next = TimeSpan.FromMilliseconds(Math.Max(1.0, afterMs));
            if (_timer.Interval != next) _timer.Interval = next;
            if (!_timer.IsEnabled) _timer.Start();
        }

        private void Fail()
        {
            if (++_failStreak == FailStreakToWarn) SetState("NO SIGNAL", "#EF4444");
        }

        private void SetState(string text, string colorHex)
        {
            StateText.Text = text;
            var brush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex));
            StateText.Foreground = brush;
            LiveDot.Fill = brush;
        }

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            _timer.Stop();

            // 정적 이벤트라 떼지 않으면 닫은 창과 라이브 버퍼가 수거되지 않는다.
            GlassAlignServices.FrameMeasured -= OnMeasuredFrame;

            LiveImage.Source = null;
            base.OnClosed(e);
        }
    }
}
