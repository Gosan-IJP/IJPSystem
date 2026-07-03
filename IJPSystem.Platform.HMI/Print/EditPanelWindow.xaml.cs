using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// "Edit Panel" (Drawing Panel.vi) — NxN 그리드에 펜/ROI 로 패턴을 그려 BMP 를 만든다.
    /// Create Empty Layer → 캔버스 크기 다이얼로그 → 이 창.
    /// 그리기/렌더는 DrawingPanelView + DrawingPanelViewModel(MVVM)이 담당하고,
    /// 여기서는 크기 계산 · 저장(픽셀 렌더링) · 단위 표시만 담당한다.
    /// </summary>
    public partial class EditPanelWindow : Window
    {
        private readonly double _widthMm, _lengthMm, _dpi;
        private readonly int _pxW, _pxH;
        private readonly DrawingPanelViewModel _vm;

        public EditPanelWindow(double widthMm, double lengthMm, double dpi)
        {
            InitializeComponent();
            _widthMm = widthMm; _lengthMm = lengthMm; _dpi = dpi <= 0 ? 600 : dpi;

            _pxW = (int)Math.Round(_widthMm * _dpi / 25.4);
            _pxH = (int)Math.Round(_lengthMm * _dpi / 25.4);

            // 표시 크기(비율 유지, 긴 변 720)
            const double maxDisp = 720.0;
            double aspect = _widthMm / _lengthMm;
            if (aspect >= 1) { Panel.Width = maxDisp; Panel.Height = maxDisp / aspect; }
            else { Panel.Height = maxDisp; Panel.Width = maxDisp * aspect; }

            _vm = new DrawingPanelViewModel(5)
            {
                StatusText = $"{_pxW}x{_pxH}  {_dpi:0}DPI  {_widthMm:0.##}x{_lengthMm:0.##}mm  32-bit RGB"
            };
            _vm.OnSave = SaveMatrixToBmp;
            DataContext = _vm;   // Panel 및 우측 버튼이 상속받음

            UpdateLineWidthMm();
            UpdateBoundaryMm();
        }

        // ── Save: NxN 패턴 매트릭스를 목표 픽셀(mm×DPI) 크기의 BMP 로 렌더 ──
        private void SaveMatrixToBmp(bool[,] matrix)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Save BMP",
                Filter = "BMP (*.bmp)|*.bmp",
                FileName = $"Pattern_{DateTime.Now:yyMMdd_HHmmss}.bmp"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                int rows = matrix.GetLength(0), cols = matrix.GetLength(1);
                var rtb = new RenderTargetBitmap(_pxW, _pxH, 96, 96, PixelFormats.Pbgra32);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, _pxW, _pxH));
                    double cw = (double)_pxW / cols, ch = (double)_pxH / rows;
                    for (int r = 0; r < rows; r++)
                        for (int c = 0; c < cols; c++)
                            if (matrix[r, c])
                                dc.DrawRectangle(Brushes.Black, null, new Rect(c * cw, r * ch, cw, ch));
                }
                rtb.Render(visual);

                var enc = new BmpBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(dlg.FileName);
                enc.Save(fs);

                _vm.StatusText = "저장 완료: " + dlg.FileName;
            }
            catch (Exception ex)
            {
                _vm.StatusText = "저장 실패: " + ex.Message;
            }
        }

        private void PatternDittering_Click(object sender, RoutedEventArgs e)
            => _vm.StatusText = "Pattern Dittering — 디더링/패턴 변환(구현 예정)";

        // ── 입력 변환 표시(px → mm) ─────────────────────────────────
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
