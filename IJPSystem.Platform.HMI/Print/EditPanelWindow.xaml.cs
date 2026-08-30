using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using IJPSystem.Platform.Infrastructure.Print;
using Microsoft.Win32;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// "Edit Panel" (Drawing Panel.vi) — 자유 벡터 드로잉 캔버스에 펜/도형을 그리고,
    /// Fill/Auto Fill/Pattern Dithering(체커보드 하프톤) 후 BMP 로 저장한다.
    /// Fill/Auto Fill/Dither 는 캔버스를 픽셀로 래스터화한 뒤 처리한다.
    /// </summary>
    public partial class EditPanelWindow : Window
    {
        private enum Tool { None, Pen, Line, Rectangle, Diamond, Ellipse, Eraser, Zoom, Pan, Crosshair, Select }

        private readonly double _widthMm, _lengthMm, _dpi;
        private readonly int _pxW, _pxH;

        private Tool _tool = Tool.Pen;
        private bool _drawing;
        private bool _fillPending;      // Fill 버튼 후 채울 지점 클릭 대기
        private Point _start;
        private Shape? _shape;          // line/rect/ellipse/diamond
        private Polyline? _stroke;      // pen/eraser

        // 되돌리기 항목은 두 종류다 — 그려 넣은 도형(UIElement)과 픽셀 획(PixelUndo).
        // 픽셀 획을 도형으로 흉내 내면 획 하나에 사각형이 수만 개 쌓인다.
        private readonly List<object> _added = new();
        private readonly Stack<object> _redo = new();

        private int _gridN = 2;          // NxN 디더 패턴(2~5)
        private bool[,] _tile = new bool[2, 2];   // NxN 사용자 정의 타일(true=검정). 미리보기에서 클릭 편집.

        // ── 탐색 도구 상태 ───────────────────────────────────────────
        private readonly ScaleTransform _zoomTf = new(1, 1);   // Zoom
        private bool _navDragging;                              // Pan / Select 드래그 중
        private Point _navStart;                                // 드래그 시작점
        private double _panOx, _panOy;                          // Pan 시작 스크롤 오프셋
        private UIElement? _selected;                           // Select 대상
        private TranslateTransform? _selTf;                     // Select 이동 변환
        private Rectangle? _selHi;                              // Select 선택 표시(점선)
        private Line? _hairV, _hairH;                           // Crosshair 십자선

        /// <summary>그린 그림이 저장된 경로. 취소했으면 null 이다 — 부르는 쪽이 이걸 보고 이어받는다.</summary>
        public string? SavedImagePath { get; private set; }

        /// <summary>저장할 자리. 빈 레이어에서 열렸으면 그 파일에 덮어쓴다(대화상자 없이).</summary>
        private readonly string? _targetPath;

        /// <summary>저장 이후 캔버스가 바뀌었나. 닫을 때 물어볼지 판단한다.</summary>
        private bool _dirty;

        public EditPanelWindow(double widthMm, double lengthMm, double dpi, string? targetPath = null)
        {
            InitializeComponent();
            _widthMm = widthMm; _lengthMm = lengthMm; _dpi = dpi <= 0 ? 600 : dpi;
            _targetPath = targetPath;

            _pxW = (int)Math.Round(_widthMm * _dpi / 25.4);
            _pxH = (int)Math.Round(_lengthMm * _dpi / 25.4);

            const double maxDisp = 760.0;
            double aspect = _widthMm / _lengthMm;
            double dispW, dispH;
            if (aspect >= 1) { dispW = maxDisp; dispH = maxDisp / aspect; }
            else { dispH = maxDisp; dispW = maxDisp * aspect; }
            DrawCanvas.Width = dispW; DrawCanvas.Height = dispH;
            DrawCanvas.LayoutTransform = _zoomTf;      // Zoom 도구용 스케일 변환
            PreviewKeyDown += EditPanelWindow_PreviewKeyDown;

            // 눈금이 적는 숫자는 화면 크기가 아니라 실제 이미지 픽셀 번호다.
            CanvasRuler.PixelsX = _pxW;
            CanvasRuler.PixelsY = _pxH;

            UpdateStatus();
            UpdateLineWidthMm();
            UpdateBoundaryMm();
            UpdateSizePreview();
        }

        // ── 도구 선택 ────────────────────────────────────────────────
        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (!(sender is RadioButton rb && rb.Tag is string tag && Enum.TryParse(tag, out Tool t))) return;
            _tool = t;
            if (DrawCanvas == null) return;   // XAML 초기 IsChecked 발화(생성 전)에는 무시
            _fillPending = false;

            // 십자선은 Crosshair 도구에서만 표시
            if (_tool != Tool.Crosshair) RemoveCrosshair();
            // 도구 전환 시 선택 해제
            if (_tool != Tool.Select) ClearSelection();

            DrawCanvas.Cursor = _tool switch
            {
                Tool.Zoom      => Cursors.Cross,
                Tool.Pan       => Cursors.Hand,
                Tool.Crosshair => Cursors.None,
                Tool.Select    => Cursors.Arrow,
                Tool.Eraser    => Cursors.Cross,
                Tool.None      => Cursors.Arrow,
                _              => Cursors.Pen
            };
        }

        private double LineWidth => double.TryParse(LineWidthBox.Text, out double v) && v > 0 ? v : 1;
        private int BoundaryThickness => Math.Max(1, int.TryParse(BoundaryBox.Text, out int v) ? v : 1);

        // ── 픽셀 편집 ────────────────────────────────────────────────
        // 눈금(⊞)을 켜면 그리기가 이미지 픽셀 격자에 딱 맞춰진다. 자유 곡선이 아니라
        // "이 픽셀을 켠다"가 되고, 저장 때 화면 캔버스를 이미지 크기로 늘려도
        // 칸 하나가 정확히 픽셀 하나가 된다 — 계단 없이 정확히 맞는다.

        /// <summary>눈금 토글이 곧 픽셀 편집 모드다 — 격자를 보면서 켜고 끄는 것이 목적이다.</summary>
        private bool PixelEdit => RulerToggle?.IsChecked == true;

        /// <summary>화면 캔버스 좌표에서 이미지 1픽셀이 차지하는 크기.</summary>
        private double CellW => DrawCanvas.Width  / Math.Max(1, _pxW);
        private double CellH => DrawCanvas.Height / Math.Max(1, _pxH);

        /// <summary>도형 꼭짓점을 픽셀 경계로 내린다. 픽셀 편집이 아니면 그대로 둔다.</summary>
        private Point SnapToCell(Point p) => PixelEdit
            ? new Point(Math.Floor(p.X / CellW) * CellW, Math.Floor(p.Y / CellH) * CellH)
            : p;

        /// <summary>
        /// Line Width [이미지 px] 를 화면 캔버스 단위로 바꾼 값.
        ///
        /// <para>
        /// 예전에는 Line Width 를 <b>그대로</b> StrokeThickness 에 넣었다. 화면 캔버스는 실제
        /// 이미지를 6배쯤 줄인 것이라, 7 이라고 적고 0.2963mm 라고 표시해 놓고 실제로는
        /// 1.8mm 로 그리고 있었다. 옆에 픽셀 눈금이 생기면 이 어긋남이 바로 보인다.
        /// </para>
        /// </summary>
        private double StrokeUnits => Math.Max(LineWidth * CellW, 0.05);

        // ── 픽셀 층 (그림판처럼 비트맵에 직접 찍는다) ────────────────
        //
        // 처음에는 켜진 칸마다 사각형을 만들어 Path 하나에 모았다. 200mm 캔버스에서
        // 획 하나가 1만 칸을 넘는데, 사각형을 넣을 때마다 WPF 가 그 뭉치를 다시
        // 삼각형으로 쪼갠다 — 칸 수의 제곱으로 늘어나 몇 초씩 멎었다.
        // 비트맵은 칸 하나가 비트 하나라, 몇 개를 칠하든 비용이 그만큼만 든다.
        //
        // 1bpp(Indexed1) 인 이유: 4724×4724 를 32비트로 잡으면 89MB 다. 이 앱은 x86 이라
        // 그만한 덩어리를 함부로 못 잡는다. 1bpp 면 같은 크기가 2.8MB 다.
        private WriteableBitmap? _pixels;
        private Image? _pixelImage;
        private byte[] _bits = Array.Empty<byte>();
        private int _bitStride;

        private readonly HashSet<long> _cellSeen = new();
        private Point _lastCellPoint;
        private List<int>? _strokeCells;        // 이번 획에서 실제로 바뀐 칸 (Undo 용)
        private bool _strokeOn;

        /// <summary>픽셀 획 하나의 되돌리기 정보. 바뀐 칸만 들고 있어 화면 크기와 무관하게 가볍다.</summary>
        private sealed record PixelUndo(int[] Cells, bool On);

        private void EnsurePixelLayer()
        {
            if (_pixels != null) return;

            _bitStride = (_pxW + 7) / 8;
            _bits = new byte[_bitStride * _pxH];

            // 팔레트 0 = 투명(안 찍음), 1 = 검정(찍음). 투명이어야 밑에 그린 것이 남는다.
            _pixels = new WriteableBitmap(_pxW, _pxH, 96, 96, PixelFormats.Indexed1,
                new BitmapPalette(new List<Color> { Colors.Transparent, Colors.Black }));

            _pixelImage = new Image
            {
                Source = _pixels,
                Width = DrawCanvas.Width,
                Height = DrawCanvas.Height,
                IsHitTestVisible = false,
            };
            // 확대했을 때 픽셀이 뭉개지면 안 된다 — 네모가 네모로 보여야 한다.
            RenderOptions.SetBitmapScalingMode(_pixelImage, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetEdgeMode(_pixelImage, EdgeMode.Aliased);

            Canvas.SetLeft(_pixelImage, 0);
            Canvas.SetTop(_pixelImage, 0);
            DrawCanvas.Children.Add(_pixelImage);
        }

        /// <summary>비트 하나를 바꾼다. 이미 그 상태면 false — 바뀐 칸만 되돌리기에 쌓는다.</summary>
        private bool SetPixel(int x, int y, bool on)
        {
            int i = y * _bitStride + (x >> 3);
            byte mask = (byte)(0x80 >> (x & 7));
            bool cur = (_bits[i] & mask) != 0;
            if (cur == on) return false;
            if (on) _bits[i] |= mask; else _bits[i] &= (byte)~mask;
            return true;
        }

        /// <summary>바뀐 줄만 화면에 올린다. 전체를 매번 올리면 2.8MB 를 초당 수십 번 복사한다.</summary>
        private void FlushRows(int y0, int y1)
        {
            if (_pixels == null || y1 < y0) return;
            y0 = Math.Max(0, y0); y1 = Math.Min(_pxH - 1, y1);
            _pixels.WritePixels(new Int32Rect(0, y0, _pxW, y1 - y0 + 1),
                                _bits, _bitStride, 0, y0);
        }

        /// <summary>
        /// (from → to) 구간의 픽셀을 켜거나 끈다.
        /// 어느 칸인지는 <see cref="PixelCells"/> 가 정한다 — 화면 없이 검증할 수 있게 떼어 뒀다.
        /// </summary>
        private void PaintCellLine(Point from, Point to)
        {
            EnsurePixelLayer();
            if (_strokeCells == null) return;

            int brush = Math.Max(1, (int)Math.Round(LineWidth));
            int minY = int.MaxValue, maxY = int.MinValue;

            foreach (var c in PixelCells.Stroke(from.X, from.Y, to.X, to.Y,
                                                CellW, CellH, brush, _pxW, _pxH, _cellSeen))
            {
                if (!SetPixel(c.X, c.Y, _strokeOn)) continue;
                _strokeCells.Add(c.Y * _pxW + c.X);
                if (c.Y < minY) minY = c.Y;
                if (c.Y > maxY) maxY = c.Y;
            }
            FlushRows(minY, maxY);
        }

        /// <summary>되돌리기/다시하기 — 그때 바뀐 칸만 반대로 되돌린다.</summary>
        private void ApplyPixelUndo(PixelUndo u, bool redo)
        {
            if (_pixels == null || u.Cells.Length == 0) return;
            bool target = redo ? u.On : !u.On;

            int minY = int.MaxValue, maxY = int.MinValue;
            foreach (int cell in u.Cells)
            {
                int y = cell / _pxW, x = cell % _pxW;
                SetPixel(x, y, target);
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            FlushRows(minY, maxY);
        }

        // ── 도형을 픽셀로 굽기 ───────────────────────────────────────
        //
        // 선·사각·마름모·타원은 그리는 동안에는 WPF 도형(미리보기)이다. 손을 떼는 순간
        // 그 윤곽을 Line Width 만큼의 붓으로 훑어 픽셀 층에 찍고, 도형은 지운다.
        //
        // 왜 굽는가: WPF 도형은 안티에일리어싱이 들어간 <b>벡터</b>다. 화면에서는 매끈해
        // 보이지만 저장할 때 회색 경계가 생기고, 굵기 7 이 정확히 7픽셀이라는 보장도 없다.
        // 잉크젯 패턴은 "이 노즐을 쏜다/안 쏜다" 뿐이라 회색이라는 것이 없다 — 그릴 때
        // 칸으로 확정해야 화면과 파일이 같아진다.

        /// <summary>도형 윤곽을 잇는 점들(캔버스 좌표). 이 점들 사이를 붓으로 훑는다.</summary>
        private IReadOnlyList<Point> OutlinePoints(Shape s)
        {
            var pts = new List<Point>();
            static double Num(double v) => double.IsNaN(v) ? 0 : v;

            switch (s)
            {
                case Line ln:
                    pts.Add(new Point(ln.X1, ln.Y1));
                    pts.Add(new Point(ln.X2, ln.Y2));
                    break;

                case Polygon poly:                     // 마름모 — 점이 이미 절대 좌표다
                    foreach (var p in poly.Points) pts.Add(p);
                    if (poly.Points.Count > 0) pts.Add(poly.Points[0]);
                    break;

                case Ellipse el:
                {
                    double a = Num(el.Width) / 2, b = Num(el.Height) / 2;
                    double cx = Num(Canvas.GetLeft(el)) + a, cy = Num(Canvas.GetTop(el)) + b;

                    // 둘레를 반 칸 간격으로 쪼갠다. 이보다 성기면 칸이 건너뛰어져 점선이 된다.
                    double step = Math.Max(0.001, Math.Min(CellW, CellH) / 2);
                    int n = (int)Math.Ceiling(Math.PI * (a + b) / step);
                    n = Math.Clamp(n, 24, 20000);      // 위: 200mm 캔버스에서 점이 무한정 늘지 않게
                    for (int i = 0; i <= n; i++)
                    {
                        double th = 2 * Math.PI * i / n;
                        pts.Add(new Point(cx + a * Math.Cos(th), cy + b * Math.Sin(th)));
                    }
                    break;
                }

                default:                               // 사각형
                {
                    double l = Num(Canvas.GetLeft(s)), t = Num(Canvas.GetTop(s));
                    double w = Num(s.Width), h = Num(s.Height);
                    pts.Add(new Point(l, t));
                    pts.Add(new Point(l + w, t));
                    pts.Add(new Point(l + w, t + h));
                    pts.Add(new Point(l, t + h));
                    pts.Add(new Point(l, t));
                    break;
                }
            }
            return pts;
        }

        /// <summary>윤곽을 픽셀 층에 찍는다. 되돌리기는 펜 획과 똑같이 바뀐 칸만 들고 간다.</summary>
        private int StampOutline(IReadOnlyList<Point> pts)
        {
            if (pts.Count == 0) return 0;

            EnsurePixelLayer();
            _cellSeen.Clear();
            _strokeCells = new List<int>();
            _strokeOn = true;

            if (pts.Count == 1) PaintCellLine(pts[0], pts[0]);
            else for (int i = 1; i < pts.Count; i++) PaintCellLine(pts[i - 1], pts[i]);

            int n = _strokeCells.Count;
            if (n > 0) { _added.Add(new PixelUndo(_strokeCells.ToArray(), true)); _redo.Clear(); }
            _strokeCells = null;
            return n;
        }

        // ── 캔버스 드로잉 ────────────────────────────────────────────
        private void DrawCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // ── 탐색 도구 ─────────────────────────────────────────────
            if (_tool == Tool.Zoom)
            {
                double factor = e.ChangedButton == MouseButton.Right ? 0.8 : 1.25;
                ApplyZoom(factor);
                return;
            }
            if (_tool == Tool.Pan && e.ChangedButton == MouseButton.Left)
            {
                _navDragging = true;
                _navStart = e.GetPosition(CanvasScroller);
                _panOx = CanvasScroller.HorizontalOffset;
                _panOy = CanvasScroller.VerticalOffset;
                DrawCanvas.CaptureMouse();
                return;
            }
            if (_tool == Tool.Select && e.ChangedButton == MouseButton.Left)
            {
                BeginSelect(e);
                return;
            }
            if (_tool == Tool.Crosshair) return;   // 이동 핸들러가 십자선 갱신

            if (e.LeftButton != MouseButtonState.Pressed) return;

            if (_fillPending)
            {
                _fillPending = false;
                Point fp = e.GetPosition(DrawCanvas);
                // 채우기는 이미지 해상도에서 판단한다 — 화면 좌표를 픽셀 번호로 바꿔 넘긴다.
                FloodFillAt((int)(fp.X / DrawCanvas.Width  * _pxW),
                            (int)(fp.Y / DrawCanvas.Height * _pxH));
                return;
            }

            if (_tool == Tool.None) return;

            _start = SnapToCell(e.GetPosition(DrawCanvas));
            _drawing = true;
            DrawCanvas.CaptureMouse();

            // 픽셀 편집에서는 획이 선(Polyline)이 아니라 비트맵의 칸을 켜고 끄는 일이다.
            if (PixelEdit && (_tool == Tool.Pen || _tool == Tool.Eraser))
            {
                _cellSeen.Clear();
                _strokeCells = new List<int>();
                _strokeOn = _tool != Tool.Eraser;
                _lastCellPoint = e.GetPosition(DrawCanvas);
                PaintCellLine(_lastCellPoint, _lastCellPoint);   // 찍기만 해도 한 칸은 바뀐다
                return;
            }

            if (_tool == Tool.Pen || _tool == Tool.Eraser)
            {
                _stroke = new Polyline
                {
                    Stroke = _tool == Tool.Eraser ? Brushes.White : Brushes.Black,
                    StrokeThickness = _tool == Tool.Eraser ? StrokeUnits * 3 : StrokeUnits,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                _stroke.Points.Add(_start);
                DrawCanvas.Children.Add(_stroke);
            }
            else
            {
                _shape = _tool switch
                {
                    Tool.Line => new Line { Stroke = Brushes.Black, StrokeThickness = StrokeUnits, X1 = _start.X, Y1 = _start.Y, X2 = _start.X, Y2 = _start.Y },
                    Tool.Diamond => new Polygon { Stroke = Brushes.Black, StrokeThickness = StrokeUnits, Fill = Brushes.Transparent },
                    Tool.Ellipse => new Ellipse { Stroke = Brushes.Black, StrokeThickness = StrokeUnits, Fill = Brushes.Transparent },
                    _ => new Rectangle { Stroke = Brushes.Black, StrokeThickness = StrokeUnits, Fill = Brushes.Transparent }
                };
                if (_shape is not Line) { Canvas.SetLeft(_shape, _start.X); Canvas.SetTop(_shape, _start.Y); }
                DrawCanvas.Children.Add(_shape);
            }
        }

        private void DrawCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            Point p = e.GetPosition(DrawCanvas);
            StatusInfo.Text = $"{_pxW}x{_pxH}  {_dpi:0}DPI  {_widthMm:0.##}x{_lengthMm:0.##}mm   ({(int)(p.X / DrawCanvas.Width * _pxW)},{(int)(p.Y / DrawCanvas.Height * _pxH)})";

            // ── 탐색 도구 ─────────────────────────────────────────────
            if (_tool == Tool.Crosshair) { UpdateCrosshair(p); return; }
            if (_tool == Tool.Pan && _navDragging)
            {
                Point cur = e.GetPosition(CanvasScroller);
                CanvasScroller.ScrollToHorizontalOffset(_panOx - (cur.X - _navStart.X));
                CanvasScroller.ScrollToVerticalOffset(_panOy - (cur.Y - _navStart.Y));
                return;
            }
            if (_tool == Tool.Select && _navDragging && _selTf != null)
            {
                _selTf.X += p.X - _navStart.X;
                _selTf.Y += p.Y - _navStart.Y;
                _navStart = p;
                UpdateSelectionHighlight();
                return;
            }

            if (!_drawing) return;

            if (_strokeCells != null)
            {
                Point raw = e.GetPosition(DrawCanvas);
                PaintCellLine(_lastCellPoint, raw);
                _lastCellPoint = raw;
                return;
            }

            p = SnapToCell(p);

            if (_stroke != null) { _stroke.Points.Add(p); }
            else if (_shape is Line line) { line.X2 = p.X; line.Y2 = p.Y; }
            else if (_shape is Polygon poly)
            {
                double l = Math.Min(_start.X, p.X), t = Math.Min(_start.Y, p.Y);
                double w = Math.Abs(p.X - _start.X), h = Math.Abs(p.Y - _start.Y);
                Canvas.SetLeft(poly, 0); Canvas.SetTop(poly, 0);
                poly.Points = new PointCollection
                {
                    new Point(l + w / 2, t), new Point(l + w, t + h / 2),
                    new Point(l + w / 2, t + h), new Point(l, t + h / 2)
                };
            }
            else if (_shape != null)
            {
                double l = Math.Min(_start.X, p.X), t = Math.Min(_start.Y, p.Y);
                Canvas.SetLeft(_shape, l); Canvas.SetTop(_shape, t);
                _shape.Width = Math.Abs(p.X - _start.X);
                _shape.Height = Math.Abs(p.Y - _start.Y);
            }
        }

        private void DrawCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_navDragging)
            {
                _navDragging = false;
                DrawCanvas.ReleaseMouseCapture();
                return;
            }

            if (!_drawing) return;
            _drawing = false;
            DrawCanvas.ReleaseMouseCapture();

            if (_strokeCells != null)
            {
                if (_strokeCells.Count > 0)
                {
                    _added.Add(new PixelUndo(_strokeCells.ToArray(), _strokeOn));
                    _redo.Clear();
                    _dirty = true;
                }
                StatusInfo.Text = $"픽셀 {_strokeCells.Count}개 {(_strokeOn ? "켬" : "끔")} " +
                                  $"(붓 {Math.Max(1, (int)Math.Round(LineWidth))}px)";
                _strokeCells = null;
                return;
            }

            // 픽셀 편집이면 도형을 벡터로 남기지 않는다 — 굵기만큼의 칸을 실제로 켜고
            // 미리보기 도형은 지운다. 화면에 보이는 계단이 곧 저장될 픽셀이다.
            if (PixelEdit && _shape != null)
            {
                var pts = OutlinePoints(_shape);
                DrawCanvas.Children.Remove(_shape);
                _shape = null;

                int brush = Math.Max(1, (int)Math.Round(LineWidth));
                int n = StampOutline(pts);
                if (n > 0) _dirty = true;
                StatusInfo.Text = $"{_tool} — 픽셀 {n}개 켬 (붓 {brush}px, {brush * 25.4 / _dpi:0.0000}mm)";
                return;
            }

            UIElement? el = (UIElement?)_stroke ?? _shape;
            if (el != null) { _added.Add(el); _redo.Clear(); _dirty = true; }
            _stroke = null; _shape = null;
        }

        // ── 액션 버튼 ────────────────────────────────────────────────
        private void ApplyDraw_Click(object sender, RoutedEventArgs e)
        {
            RenderCanvas(_pxW, _pxH);
            StatusInfo.Text = $"Apply Draw — 요소 {_added.Count}개, {_pxW}x{_pxH}px 반영";
        }

        private void ClearCanvas_Click(object sender, RoutedEventArgs e)
        {
            if (_added.Count > 0) _dirty = true;
            DrawCanvas.Children.Clear();
            _added.Clear(); _redo.Clear();
            _fillPending = false;

            // 픽셀 층도 같이 비운다 — 캔버스에서 뺐어도 비트는 남아 있어서,
            // 다음에 한 점만 찍으면 지운 그림이 통째로 돌아온다.
            _pixels = null; _pixelImage = null;
            _bits = Array.Empty<byte>();
            _strokeCells = null; _cellSeen.Clear();

            UpdateStatus();
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (_added.Count == 0) return;
            var item = _added[_added.Count - 1];
            _added.RemoveAt(_added.Count - 1);

            if (item is FlattenUndo fu) RestoreFlatten(fu);
            else if (item is PixelUndo pu) ApplyPixelUndo(pu, redo: false);
            else if (item is UIElement el) DrawCanvas.Children.Remove(el);

            _redo.Push(item);
            _dirty = true;
        }

        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            if (_redo.Count == 0) return;
            var item = _redo.Pop();

            if (item is FlattenUndo fu) { ApplyFlatten(fu); _added.Clear(); }
            else if (item is PixelUndo pu) ApplyPixelUndo(pu, redo: true);
            else if (item is UIElement el) DrawCanvas.Children.Add(el);

            _added.Add(item);
            _dirty = true;
        }

        private void Fill_Click(object sender, RoutedEventArgs e)
        {
            _fillPending = true;
            StatusInfo.Text = "Fill — 채울 영역을 캔버스에서 클릭하세요.";
        }

        /// <summary>클릭 지점과 연결된 흰 영역을 검정으로 채움(버킷 필). 좌표는 <b>이미지 픽셀</b>.</summary>
        private void FloodFillAt(int x, int y)
        {
            var m = RasterizeToMatrix(out int w, out int h);
            if (w == 0 || x < 0 || y < 0 || x >= w || y >= h) { StatusInfo.Text = "Fill — 캔버스 안을 클릭하세요."; return; }

            if (m[y, x]) { StatusInfo.Text = $"Fill — ({x},{y}) 는 이미 검정입니다. 비어 있는 안쪽을 클릭하세요."; return; }

            var g = new PixelGrid(1, 1); g.Restore(m);   // Restore 가 복제하므로 m 은 원본으로 남는다
            g.FloodFill(y, x, true);                     // 매트릭스는 [row=y, col=x]
            var filled = g.Snapshot();

            FlattenTo(filled);

            // 경계가 한 군데라도 뚫려 있으면 바깥까지 새어 나간다 — 그 사실을 알려 준다.
            // 예전에는 조용히 캔버스 전체가 까맣게 되어 "반대로 채워졌다"로 보였다.
            StatusInfo.Text = LeakedToEdge(m, filled, w, h)
                ? "Fill — ⚠ 경계가 열려 있어 바깥까지 채워졌습니다. Undo 후 선을 이어 주세요."
                : "Fill — 영역 채움 완료";
        }

        /// <summary>채우기가 캔버스 테두리까지 번졌는지 — 닫힌 도형이면 테두리는 그대로다.</summary>
        private static bool LeakedToEdge(bool[,] before, bool[,] after, int w, int h)
        {
            for (int x = 0; x < w; x++)
                if ((!before[0, x] && after[0, x]) || (!before[h - 1, x] && after[h - 1, x])) return true;
            for (int y = 0; y < h; y++)
                if ((!before[y, 0] && after[y, 0]) || (!before[y, w - 1] && after[y, w - 1])) return true;
            return false;
        }

        private void AutoFill_Click(object sender, RoutedEventArgs e)
        {
            var m = RasterizeToMatrix(out int w, out int h);
            if (w == 0) return;
            var g = new PixelGrid(1, 1); g.Restore(m);
            g.AutoFillBoundary();
            FlattenTo(g.Snapshot());
            StatusInfo.Text = "Auto Fill — 닫힌 영역 자동 채움";
        }

        /// <summary>
        /// Pattern Dithering: 검정 영역을 NxN 사용자 타일 패턴으로 하프톤 변환.
        /// 타일의 각 블록은 Boundary Thickness(px) 크기로 반복된다.
        /// 미리보기에서 아무 블록도 켜지 않았으면 체커보드를 기본 사용.
        /// </summary>
        private void PatternDithering_Click(object sender, RoutedEventArgs e)
        {
            var m = RasterizeToMatrix(out int w, out int h);
            if (w == 0) return;

            int n = Math.Max(1, _gridN);
            int cell = BoundaryThickness;
            bool tileEmpty = IsTileEmpty();

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (m[y, x])
                    {
                        int tr = (y / cell) % n, tc = (x / cell) % n;
                        m[y, x] = tileEmpty ? ((tr + tc) % 2 == 0) : _tile[tr, tc];
                    }

            FlattenTo(m);
            StatusInfo.Text = tileEmpty
                ? $"Pattern Dithering — {n}x{n} 체커보드 / cell {cell}px"
                : $"Pattern Dithering — {n}x{n} 사용자 타일 / cell {cell}px";
        }

        private bool IsTileEmpty()
        {
            foreach (bool b in _tile) if (b) return false;
            return true;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "Are you Sure to Save IMG?", "Save",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                StatusInfo.Text = "저장 취소";
                return;
            }
            SaveImage();
        }

        /// <summary>실제 저장. 저장했으면 true — 취소하거나 실패하면 false(창을 닫으면 안 된다).</summary>
        private bool SaveImage()
        {
            // 빈 레이어에서 열렸으면 그 파일에 그대로 덮어쓴다 — 래스터라이저가 기다리는
            // 자리가 정해져 있는데 다른 데 저장하면 그림이 변환으로 이어지지 않는다.
            string path;
            if (!string.IsNullOrEmpty(_targetPath))
            {
                path = _targetPath!;
            }
            else
            {
                var dlg = new SaveFileDialog
                {
                    Title = "Save BMP",
                    Filter = "BMP (*.bmp)|*.bmp",
                    FileName = $"Pattern_{DateTime.Now:yyMMdd_HHmmss}.bmp"
                };
                if (dlg.ShowDialog() != true) { StatusInfo.Text = "저장 취소"; return false; }
                path = dlg.FileName;
            }

            try
            {
                var rtb = RenderCanvas(_pxW, _pxH);
                var enc = new BmpBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(path)) enc.Save(fs);
                SavedImagePath = path;
                _dirty = false;
                StatusInfo.Text = "저장 완료: " + path;
                return true;
            }
            catch (Exception ex)
            {
                StatusInfo.Text = "저장 실패: " + ex.Message;
                MessageBox.Show(this, "저장하지 못했습니다.\n\n" + ex.Message, "Save",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// 저장하지 않고 닫으면 그린 것이 사라진다 — 묻고 닫는다.
        /// 저장을 고르고도 실패하거나 파일 대화상자를 취소하면 닫지 않는다.
        /// </summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            if (e.Cancel || !_dirty) return;

            var r = MessageBox.Show(this,
                "그린 내용이 아직 저장되지 않았습니다.\n저장하고 닫을까요?",
                "Edit Panel", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.Yes && !SaveImage()) e.Cancel = true;
        }

        // ── 래스터화 / 결과 반영 ─────────────────────────────────────

        /// <summary>
        /// 캔버스를 <b>이미지 해상도</b>로 찍어 흑백 매트릭스로 만든다.
        ///
        /// <para>
        /// 예전에는 화면 크기(760px)로 찍었다. 실제 이미지는 2362px 라 3배 넘게 줄어든
        /// 그림 위에서 Fill 을 한 셈이고, 굵기 1~3px 로 그은 경계는 화면 크기에서는 1픽셀도
        /// 안 돼 <b>사라진다</b>. 경계가 뚫리니 안쪽을 찍어도 바깥까지 번져서, 채우려던
        /// 곳의 반대쪽이 까맣게 되는 것처럼 보였다. 저장 해상도에서 판단해야 맞다.
        /// </para>
        /// <para>
        /// 한 줄씩 읽는 이유: 200mm 캔버스(4724²)를 통째로 잡으면 89MB 다. 이 앱은 x86 이라
        /// 그만한 덩어리를 함부로 못 잡는다(픽셀 층이 1bpp 인 것과 같은 이유).
        /// </para>
        /// </summary>
        private bool[,] RasterizeToMatrix(out int w, out int h)
        {
            RemoveOverlays();
            w = _pxW; h = _pxH;
            if (w <= 0 || h <= 0) { w = h = 0; return new bool[1, 1]; }

            var rtb = RenderCanvas(w, h);

            var m = new bool[h, w];
            int stride = w * 4;
            var row = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                rtb.CopyPixels(new Int32Rect(0, y, w, 1), row, stride, 0);
                for (int x = 0; x < w; x++)
                {
                    int i = x * 4;
                    byte b = row[i], gg = row[i + 1], r = row[i + 2], a = row[i + 3];
                    double lum = a < 10 ? 255 : 0.299 * r + 0.587 * gg + 0.114 * b;
                    m[y, x] = lum < 128;
                }
            }
            return m;
        }

        /// <summary>
        /// 평탄화 되돌리기 — Fill/Auto Fill/Dithering 직전의 캔버스를 통째로 들고 있는다.
        /// 한 장으로 굽기 전의 도형·픽셀 층을 그대로 되살려야 하므로 항목 하나로는 부족하다.
        /// </summary>
        private sealed record FlattenUndo(
            UIElement[] Children, object[] Added,
            WriteableBitmap? Pixels, Image? PixelImage, byte[] Bits, Image Flat);

        /// <summary>
        /// 흑백 매트릭스를 <b>한 장의 이미지로</b> 굽는다(Undo 가능).
        ///
        /// <para>
        /// 예전에는 이 이미지를 기존 자식 <b>위에 얹기만</b> 했다. 이미지가 불투명(흰 바탕)이라
        /// 밑에 있던 픽셀 층이 영영 가려지는데, <see cref="EnsurePixelLayer"/> 는 층이
        /// "있다"고 보고 그대로 썼다 — Fill 을 한 번 누르면 그 뒤로 선을 그어도 화면에
        /// 아무것도 안 나타났다. 그린 것이 묻힌 층에 들어가고 있었다.
        /// 평탄화는 말 그대로 한 장으로 만드는 일이니, 자식을 비우고 이 이미지만 남긴다.
        /// </para>
        /// </summary>
        private void FlattenTo(bool[,] m)
        {
            int h = m.GetLength(0), w = m.GetLength(1);

            // 1bpp — 흑백뿐이라 비트 하나면 된다. 32비트로 잡으면 200mm 캔버스에서 89MB 다.
            int stride = (w + 7) / 8;
            var bits = new byte[stride * h];
            for (int y = 0; y < h; y++)
            {
                int rowBase = y * stride;
                for (int x = 0; x < w; x++)
                    if (m[y, x]) bits[rowBase + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }

            // 0 = 흰색(불투명) — 이 한 장이 캔버스 전체를 대신하므로 바탕까지 들고 있어야 한다.
            var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Indexed1,
                new BitmapPalette(new List<Color> { Colors.White, Colors.Black }));
            wb.WritePixels(new Int32Rect(0, 0, w, h), bits, stride, 0);
            wb.Freeze();

            var img = new Image { Source = wb, Width = DrawCanvas.Width, Height = DrawCanvas.Height };
            // 확대했을 때 픽셀이 뭉개지면 안 된다 — 구운 뒤에도 칸이 칸으로 보여야 한다.
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetEdgeMode(img, EdgeMode.Aliased);
            Canvas.SetLeft(img, 0); Canvas.SetTop(img, 0);

            // 굽기 직전 상태를 통째로 담는다. 이 매트릭스는 캔버스를 그대로 찍은 것이라
            // 픽셀 층 내용도 이미 이 이미지 안에 들어 있다 — 버려도 그림은 남는다.
            var kids = new UIElement[DrawCanvas.Children.Count];
            DrawCanvas.Children.CopyTo(kids, 0);
            var fu = new FlattenUndo(kids, _added.ToArray(), _pixels, _pixelImage, _bits, img);

            ApplyFlatten(fu);
            _added.Clear(); _added.Add(fu);   // 이력은 "평탄화" 한 걸음으로 접힌다
            _redo.Clear();
            _dirty = true;
        }

        /// <summary>캔버스를 구운 이미지 한 장으로 바꾼다. 픽셀 층은 다음 획이 새로 만든다.</summary>
        private void ApplyFlatten(FlattenUndo fu)
        {
            ClearSelection();
            RemoveCrosshair();
            DrawCanvas.Children.Clear();
            DrawCanvas.Children.Add(fu.Flat);

            _pixels = null; _pixelImage = null;
            _bits = Array.Empty<byte>();
            _strokeCells = null; _cellSeen.Clear();
        }

        /// <summary>평탄화 이전으로 되돌린다 — 자식·이력·픽셀 층을 그때 그대로.</summary>
        private void RestoreFlatten(FlattenUndo fu)
        {
            ClearSelection();
            RemoveCrosshair();
            DrawCanvas.Children.Clear();
            foreach (var c in fu.Children) DrawCanvas.Children.Add(c);

            _added.Clear(); _added.AddRange(fu.Added);
            _pixels = fu.Pixels; _pixelImage = fu.PixelImage; _bits = fu.Bits;
            _strokeCells = null; _cellSeen.Clear();
        }

        private RenderTargetBitmap RenderCanvas(int pxW, int pxH)
        {
            RemoveOverlays();
            var rtb = new RenderTargetBitmap(pxW, pxH, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var brush = new VisualBrush(DrawCanvas) { Stretch = Stretch.Fill };
                dc.DrawRectangle(brush, null, new Rect(0, 0, pxW, pxH));
            }
            rtb.Render(visual);
            return rtb;
        }

        // ── 픽셀 눈금 ────────────────────────────────────────────────
        private void Ruler_Toggled(object sender, RoutedEventArgs e)
        {
            if (CanvasRuler == null) return;
            bool on = RulerToggle.IsChecked == true;
            CanvasRuler.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            StatusInfo.Text = on ? PixelEditHint() : "픽셀 눈금 꺼짐 — 자유 곡선으로 그립니다.";
        }

        /// <summary>
        /// 지금 배율에서 픽셀 하나가 화면 몇 px 인지 알려 준다.
        /// 이 값이 작으면 격자에 맞춰 그려도 눈으로는 확인할 수 없다 — 그 사실을 숨기면
        /// "눈금을 켰는데 픽셀처럼 안 보인다"가 된다.
        /// </summary>
        private string PixelEditHint()
        {
            double onScreen = CellW * _zoomTf.ScaleX;
            string state = onScreen >= 4
                ? "픽셀 하나가 보입니다."
                : $"Ctrl+휠로 {Math.Ceiling(4 / Math.Max(1e-9, CellW)):0}배까지 확대하면 픽셀이 보입니다.";
            return $"픽셀 편집 — 그린 곳이 ON. 1픽셀 = 화면 {onScreen:0.00}px · {state} (이미지 {_pxW}x{_pxH})";
        }

        // ── 탐색 도구 구현 (Zoom / Pan / Crosshair / Select) ─────────
        private void ApplyZoom(double factor)
        {
            double old = _zoomTf.ScaleX;
            // 상한이 8 이면 200mm 캔버스에서 이미지 1픽셀이 화면 1.3px 밖에 안 돼 픽셀 편집이 불가능하다.
            double z = Math.Clamp(old * factor, 0.25, 64.0);
            if (Math.Abs(z - old) < 1e-9) return;

            // 확대 전에 화면 한가운데 있던 지점을 확대 후에도 한가운데 둔다.
            // 안 그러면 늘 좌상단 기준으로 커져, 확대할수록 보던 곳이 화면 밖으로 밀려난다
            // (스크롤 막대를 끌어도 엉뚱한 흰 여백만 지나가게 된다).
            double cx = CanvasScroller.HorizontalOffset + CanvasScroller.ViewportWidth  / 2;
            double cy = CanvasScroller.VerticalOffset   + CanvasScroller.ViewportHeight / 2;
            double k  = z / old;

            _zoomTf.ScaleX = _zoomTf.ScaleY = z;
            CanvasScroller.UpdateLayout();        // 새 Extent 가 잡힌 뒤에 스크롤해야 한다

            CanvasScroller.ScrollToHorizontalOffset(cx * k - CanvasScroller.ViewportWidth  / 2);
            CanvasScroller.ScrollToVerticalOffset  (cy * k - CanvasScroller.ViewportHeight / 2);

            StatusInfo.Text = PixelEdit
                ? $"Zoom {z * 100:0}% · " + PixelEditHint()
                : $"Zoom {z * 100:0}%";
        }

        private void CanvasScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // ★ 그냥 굴린 휠은 절대 가로채지 않는다 — 세로 스크롤이다.
            //   예전에는 Zoom 도구가 선택돼 있으면 휠까지 확대에 썼다. 그래서 확대하려고
            //   🔍 를 눌러 둔 채로는 휠을 굴려도 화면이 안 내려가, 세로 스크롤이 죽은 것처럼 보였다.
            //   확대는 Ctrl+휠(또는 🔍 도구로 클릭)로 한다 — 다른 프로그램과 같은 규칙이다.
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                ApplyZoom(e.Delta > 0 ? 1.2 : 1.0 / 1.2);
                e.Handled = true;
                return;
            }

            // Shift+휠 = 가로 스크롤. WPF ScrollViewer 는 기본으로 세로만 굴린다.
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && CanvasScroller.ScrollableWidth > 0)
            {
                CanvasScroller.ScrollToHorizontalOffset(CanvasScroller.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private void UpdateCrosshair(Point p)
        {
            if (_hairV == null)
            {
                _hairV = new Line { Stroke = Brushes.Red, StrokeThickness = 0.6, IsHitTestVisible = false };
                _hairH = new Line { Stroke = Brushes.Red, StrokeThickness = 0.6, IsHitTestVisible = false };
                DrawCanvas.Children.Add(_hairV);
                DrawCanvas.Children.Add(_hairH);
            }
            _hairV!.X1 = p.X; _hairV.X2 = p.X; _hairV.Y1 = 0; _hairV.Y2 = DrawCanvas.Height;
            _hairH!.Y1 = p.Y; _hairH.Y2 = p.Y; _hairH.X1 = 0; _hairH.X2 = DrawCanvas.Width;
        }

        private void RemoveCrosshair()
        {
            if (_hairV != null) DrawCanvas.Children.Remove(_hairV);
            if (_hairH != null) DrawCanvas.Children.Remove(_hairH);
            _hairV = _hairH = null;
        }

        private void BeginSelect(MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not UIElement hit || hit == DrawCanvas || !_added.Contains(hit))
            {
                ClearSelection();
                StatusInfo.Text = "Select — 요소를 클릭하세요.";
                return;
            }
            _selected = hit;
            _selTf = _selected.RenderTransform as TranslateTransform;
            if (_selTf == null) { _selTf = new TranslateTransform(); _selected.RenderTransform = _selTf; }
            _navStart = e.GetPosition(DrawCanvas);
            _navDragging = true;
            DrawCanvas.CaptureMouse();
            ShowSelectionHighlight();
            StatusInfo.Text = "Select — 드래그로 이동, Delete 로 삭제";
        }

        private void ShowSelectionHighlight()
        {
            if (_selHi == null)
            {
                _selHi = new Rectangle
                {
                    Stroke = Brushes.DodgerBlue, StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 2 },
                    Fill = Brushes.Transparent, IsHitTestVisible = false
                };
                DrawCanvas.Children.Add(_selHi);
            }
            UpdateSelectionHighlight();
        }

        private void UpdateSelectionHighlight()
        {
            if (_selected == null || _selHi == null) return;
            Rect b = VisualTreeHelper.GetDescendantBounds(_selected);
            if (b.IsEmpty) return;
            Rect cb = _selected.TransformToAncestor(DrawCanvas).TransformBounds(b);
            Canvas.SetLeft(_selHi, cb.X); Canvas.SetTop(_selHi, cb.Y);
            _selHi.Width = cb.Width; _selHi.Height = cb.Height;
        }

        private void ClearSelection()
        {
            if (_selHi != null) { DrawCanvas.Children.Remove(_selHi); _selHi = null; }
            _selected = null; _selTf = null;
        }

        /// <summary>선택된 요소를 Delete 키로 삭제(Undo용 Redo 스택에 적재).</summary>
        private void EditPanelWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && _selected != null)
            {
                var el = _selected;
                ClearSelection();
                DrawCanvas.Children.Remove(el);
                _added.Remove(el);
                _redo.Push(el);
                _dirty = true;
                StatusInfo.Text = "Select — 요소 삭제";
                e.Handled = true;
            }
        }

        /// <summary>래스터화/렌더 직전에 화면 보조선(십자선·선택 표시)을 제거한다.</summary>
        private void RemoveOverlays()
        {
            RemoveCrosshair();
            if (_selHi != null) { DrawCanvas.Children.Remove(_selHi); _selHi = null; }
        }

        // ── NxN 디더 타일(클릭 편집) ─────────────────────────────────
        private void GridSize_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (GridSizeCombo?.SelectedItem is ComboBoxItem it && it.Content is string s &&
                int.TryParse(s.Split('x')[0], out int n))
            {
                _gridN = Math.Max(1, n);
                _tile = new bool[_gridN, _gridN];   // NxN 흰색 블록으로 초기화
                UpdateSizePreview();
            }
        }

        /// <summary>작은 박스에 NxN 흰색 블록 격자를 표시. 블록을 클릭하면 검정으로 토글된다.</summary>
        private void UpdateSizePreview()
        {
            if (SizePreview == null) return;
            SizePreview.Children.Clear();
            double pw = SizePreview.Width, ph = SizePreview.Height;
            double cw = pw / _gridN, ch = ph / _gridN;
            for (int r = 0; r < _gridN; r++)
                for (int c = 0; c < _gridN; c++)
                {
                    var rect = new Rectangle
                    {
                        Width = cw, Height = ch,
                        Fill = _tile[r, c] ? Brushes.Black : Brushes.White,
                        Stroke = Brushes.Gray, StrokeThickness = 0.5,
                        Tag = (r, c), Cursor = Cursors.Hand
                    };
                    rect.MouseLeftButtonDown += TileCell_Click;
                    Canvas.SetLeft(rect, c * cw); Canvas.SetTop(rect, r * ch);
                    SizePreview.Children.Add(rect);
                }
        }

        /// <summary>미리보기 격자 블록 클릭 → 검정/흰색 토글.</summary>
        private void TileCell_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Rectangle rect && rect.Tag is ValueTuple<int, int> pos)
            {
                _tile[pos.Item1, pos.Item2] = !_tile[pos.Item1, pos.Item2];
                rect.Fill = _tile[pos.Item1, pos.Item2] ? Brushes.Black : Brushes.White;
            }
        }

        // ── Boundary 스피너 / 상태 · 단위 표시 ───────────────────────
        private void BoundaryUp_Click(object sender, RoutedEventArgs e)   { BoundaryBox.Text = (BoundaryThickness + 1).ToString(); }
        private void BoundaryDown_Click(object sender, RoutedEventArgs e) { BoundaryBox.Text = Math.Max(1, BoundaryThickness - 1).ToString(); }

        private void UpdateStatus()
            => StatusInfo.Text = $"{_pxW}x{_pxH}  {_dpi:0}DPI  {_widthMm:0.##}x{_lengthMm:0.##}mm  32-bit RGB";

        // 굵기는 이미지 픽셀 수라 정수여야 한다 — 7.5픽셀짜리 붓 같은 것은 없다.
        private void LineWidthUp_Click(object sender, RoutedEventArgs e)
            => LineWidthBox.Text = Math.Min(999, (int)Math.Round(LineWidth) + 1).ToString();
        private void LineWidthDown_Click(object sender, RoutedEventArgs e)
            => LineWidthBox.Text = Math.Max(1, (int)Math.Round(LineWidth) - 1).ToString();

        private void LineWidth_Changed(object sender, TextChangedEventArgs e) => UpdateLineWidthMm();
        private void Boundary_Changed(object sender, TextChangedEventArgs e) => UpdateBoundaryMm();

        private void UpdateLineWidthMm()
        {
            if (LineWidthMm == null) return;
            double px = double.TryParse(LineWidthBox.Text, out double v) ? v : 0;
            LineWidthMm.Text = $"{px * 25.4 / _dpi:0.0000} mm";
        }

        private void UpdateBoundaryMm()
        {
            if (BoundaryMm == null) return;
            double px = double.TryParse(BoundaryBox.Text, out double v) ? v : 0;
            BoundaryMm.Text = $"{px * 25.4 / _dpi:0.0000} mm";
        }
    }
}
