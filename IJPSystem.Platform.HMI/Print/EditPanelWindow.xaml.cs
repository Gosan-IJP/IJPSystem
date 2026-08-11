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

        private readonly List<UIElement> _added = new();
        private readonly Stack<UIElement> _redo = new();

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

        public EditPanelWindow(double widthMm, double lengthMm, double dpi)
        {
            InitializeComponent();
            _widthMm = widthMm; _lengthMm = lengthMm; _dpi = dpi <= 0 ? 600 : dpi;

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

        private System.Windows.Shapes.Path? _cellPath;   // 픽셀 칠하기 한 획 = 요소 하나(Undo 단위)
        private GeometryGroup? _cellGeo;
        private readonly HashSet<long> _cellSeen = new();
        private Point _lastCellPoint;

        /// <summary>
        /// (from → to) 구간에서 켜지는 픽셀 칸을 사각형으로 얹는다.
        /// 어느 칸이 켜지는지는 <see cref="PixelCells"/> 가 정한다 — 화면 없이 검증할 수 있게 떼어 뒀다.
        /// </summary>
        private void PaintCellLine(Point from, Point to)
        {
            if (_cellGeo == null) return;

            int brush = Math.Max(1, (int)Math.Round(LineWidth));
            double cw = CellW, ch = CellH;

            foreach (var c in PixelCells.Stroke(from.X, from.Y, to.X, to.Y,
                                                cw, ch, brush, _pxW, _pxH, _cellSeen))
            {
                var (ox, oy) = PixelCells.Origin(c.X, c.Y, cw, ch);
                _cellGeo.Children.Add(new RectangleGeometry(new Rect(ox, oy, cw, ch)));
            }
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
                FloodFillAt((int)fp.X, (int)fp.Y);
                return;
            }

            if (_tool == Tool.None) return;

            _start = SnapToCell(e.GetPosition(DrawCanvas));
            _drawing = true;
            DrawCanvas.CaptureMouse();

            // 픽셀 편집에서는 획이 선(Polyline)이 아니라 켜진 칸의 모음이다.
            if (PixelEdit && (_tool == Tool.Pen || _tool == Tool.Eraser))
            {
                _cellSeen.Clear();
                _cellGeo  = new GeometryGroup();
                _cellPath = new System.Windows.Shapes.Path
                {
                    Data = _cellGeo,
                    Fill = _tool == Tool.Eraser ? Brushes.White : Brushes.Black,
                };
                DrawCanvas.Children.Add(_cellPath);
                _lastCellPoint = e.GetPosition(DrawCanvas);
                PaintCellLine(_lastCellPoint, _lastCellPoint);   // 찍기만 해도 한 점은 켜진다
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

            if (_cellGeo != null)
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

            if (_cellPath != null)
                StatusInfo.Text = $"픽셀 {_cellSeen.Count}개 " +
                                  (_tool == Tool.Eraser ? "끔" : "켬") + $" (붓 {Math.Max(1, (int)Math.Round(LineWidth))}px)";

            UIElement? el = (UIElement?)_cellPath ?? (UIElement?)_stroke ?? _shape;
            if (el != null) { _added.Add(el); _redo.Clear(); }
            _stroke = null; _shape = null; _cellPath = null; _cellGeo = null;
        }

        // ── 액션 버튼 ────────────────────────────────────────────────
        private void ApplyDraw_Click(object sender, RoutedEventArgs e)
        {
            RenderCanvas(_pxW, _pxH);
            StatusInfo.Text = $"Apply Draw — 요소 {_added.Count}개, {_pxW}x{_pxH}px 반영";
        }

        private void ClearCanvas_Click(object sender, RoutedEventArgs e)
        {
            DrawCanvas.Children.Clear();
            _added.Clear(); _redo.Clear();
            _fillPending = false;
            UpdateStatus();
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (_added.Count == 0) return;
            var el = _added[_added.Count - 1];
            _added.RemoveAt(_added.Count - 1);
            DrawCanvas.Children.Remove(el);
            _redo.Push(el);
        }

        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            if (_redo.Count == 0) return;
            var el = _redo.Pop();
            DrawCanvas.Children.Add(el);
            _added.Add(el);
        }

        private void Fill_Click(object sender, RoutedEventArgs e)
        {
            _fillPending = true;
            StatusInfo.Text = "Fill — 채울 영역을 캔버스에서 클릭하세요.";
        }

        /// <summary>클릭 지점과 연결된 같은 색 영역을 검정으로 채움(버킷 필).</summary>
        private void FloodFillAt(int x, int y)
        {
            var m = RasterizeToMatrix(out int w, out int h);
            if (w == 0 || x < 0 || y < 0 || x >= w || y >= h) { StatusInfo.Text = "Fill — 캔버스 안을 클릭하세요."; return; }
            var g = new PixelGrid(1, 1); g.Restore(m);
            g.FloodFill(y, x, true);          // 매트릭스는 [row=y, col=x]
            FlattenTo(g.Snapshot());
            StatusInfo.Text = "Fill — 영역 채움 완료";
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
            if (MessageBox.Show("Are you Sure to Save IMG?", "Save",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                StatusInfo.Text = "저장 취소";
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "Save BMP",
                Filter = "BMP (*.bmp)|*.bmp",
                FileName = $"Pattern_{DateTime.Now:yyMMdd_HHmmss}.bmp"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var rtb = RenderCanvas(_pxW, _pxH);
                var enc = new BmpBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(dlg.FileName);
                enc.Save(fs);
                StatusInfo.Text = "저장 완료: " + dlg.FileName;
            }
            catch (Exception ex) { StatusInfo.Text = "저장 실패: " + ex.Message; }
        }

        // ── 래스터화 / 결과 반영 ─────────────────────────────────────
        private bool[,] RasterizeToMatrix(out int w, out int h)
        {
            RemoveOverlays();
            w = (int)Math.Round(DrawCanvas.Width);
            h = (int)Math.Round(DrawCanvas.Height);
            if (w <= 0 || h <= 0) { w = h = 0; return new bool[1, 1]; }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(DrawCanvas);

            int stride = w * 4;
            var px = new byte[h * stride];
            rtb.CopyPixels(px, stride, 0);

            var m = new bool[h, w];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * stride + x * 4;
                    byte b = px[i], gg = px[i + 1], r = px[i + 2], a = px[i + 3];
                    double lum = a < 10 ? 255 : 0.299 * r + 0.587 * gg + 0.114 * b;
                    m[y, x] = lum < 128;
                }
            return m;
        }

        /// <summary>흑백 매트릭스를 캔버스 위 한 장의 이미지로 얹어 평탄화(Undo 가능).</summary>
        private void FlattenTo(bool[,] m)
        {
            int h = m.GetLength(0), w = m.GetLength(1);
            var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            int stride = w * 4;
            var px = new byte[h * stride];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * stride + x * 4;
                    byte v = (byte)(m[y, x] ? 0 : 255);
                    px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255;
                }
            wb.WritePixels(new Int32Rect(0, 0, w, h), px, stride, 0);
            wb.Freeze();

            var img = new Image { Source = wb, Width = DrawCanvas.Width, Height = DrawCanvas.Height };
            Canvas.SetLeft(img, 0); Canvas.SetTop(img, 0);
            DrawCanvas.Children.Add(img);
            _added.Add(img); _redo.Clear();
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
