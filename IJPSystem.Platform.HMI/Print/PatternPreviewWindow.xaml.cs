using IJPSystem.Platform.Infrastructure.Print;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// 이미지 → RIP → 발사 지도를 <b>찍기 전에</b> 눈으로 확인하는 창.
    ///
    /// <para>
    /// 노즐 배열 값(열 수·간격·번호 규약)을 화면에서 바꿀 수 있게 둔 이유: 이 값들이 아직
    /// 실물로 확인되지 않았다. 스핏 한 줄을 쏴 보고 여기서 같은 값을 넣어 그림이 맞는지
    /// 대조하면 규약이 확정된다. 확정되면 장비 설정(MachineData.db)으로 옮기고 이 칸은 지운다.
    /// </para>
    /// </summary>
    public partial class PatternPreviewWindow : Window
    {
        private readonly IReadOnlyList<int> _usedNozzles;
        private string? _imagePath;
        private byte[,]? _gray;

        /// <param name="imagePath">미리 불러올 이미지(없으면 창에서 고른다).</param>
        /// <param name="usedNozzles">사용 노즐 번호. 비어 있으면 배열 전체를 쓴다.</param>
        public PatternPreviewWindow(string? imagePath, IReadOnlyList<int>? usedNozzles)
        {
            InitializeComponent();
            _usedNozzles = usedNozzles ?? Array.Empty<int>();

            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath)) LoadImage(imagePath!);
        }

        /// <summary>
        /// 이미 변환된 패턴을 바로 보여 준다.
        ///
        /// <para>여기가 중요하다 — 창을 열어 놓고 [패턴 만들기]를 누르면 이 화면의 입력값으로
        /// <b>다시</b> RIP 을 한다. 그 값이 변환 때와 다르면, 저장한 것과 다른 그림을 보면서
        /// 맞다고 판단하게 된다. 변환 결과를 그대로 받아 띄우면 그럴 일이 없다.</para>
        /// </summary>
        public PatternPreviewWindow(PrintPattern pattern, NozzleLayout? layout,
                                    IReadOnlyList<int>? ignored, string sourceLabel)
        {
            InitializeComponent();
            _usedNozzles = Array.Empty<int>();

            // 입력칸을 실제로 쓰인 값으로 맞춰 둔다 — 여기서 다시 만들어도 같은 결과가 나오도록.
            if (layout != null)
            {
                RowsBox.Text       = layout.Rows.ToString(CultureInfo.InvariantCulture);
                PerRowBox.Text     = layout.NozzlesPerRow.ToString(CultureInfo.InvariantCulture);
                InRowPitchBox.Text = layout.InRowPitchUm.ToString("0.###", CultureInfo.InvariantCulture);
                RowOffsetBox.Text  = layout.RowOffsetUm.ToString("0.###", CultureInfo.InvariantCulture);
                OrderBox.SelectedIndex = layout.Order == NozzleLayout.NozzleOrder.RowByRow ? 1 : 0;
                UmPerPxBox.Text    = layout.EffectivePitchUm.ToString("0.###", CultureInfo.InvariantCulture);
            }
            ScanStepBox.Text = pattern.ScanStepUm.ToString("0.###", CultureInfo.InvariantCulture);

            SourceText.Text = sourceLabel;
            ShowPattern(pattern, layout, ignored ?? Array.Empty<int>());
        }

        private void OpenImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "인쇄 이미지 선택",
                Filter = "이미지 파일|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|모든 파일|*.*",
            };
            if (dlg.ShowDialog() == true) LoadImage(dlg.FileName);
        }

        /// <summary>이미지를 8비트 회색 배열로. 값이 클수록 잉크가 많이 나가야 하는 자리다.</summary>
        private void LoadImage(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption   = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource     = new Uri(path);
                bmp.EndInit();

                var g8 = new FormatConvertedBitmap(bmp, PixelFormats.Gray8, null, 0);
                int w = g8.PixelWidth, h = g8.PixelHeight;
                var px = new byte[w * h];
                g8.CopyPixels(px, w, 0);

                var gray = new byte[h, w];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        gray[y, x] = px[y * w + x];

                _gray = gray;
                _imagePath = path;
                SourceText.Text = $"{Path.GetFileName(path)}  ({w}×{h})";
                SummaryText.Text = "이미지를 불러왔습니다. [패턴 만들기]를 누르세요.";
            }
            catch (Exception ex)
            {
                _gray = null;
                SourceText.Text = "(불러오기 실패)";
                SummaryText.Text = $"이미지를 읽지 못했습니다 — {ex.Message}";
            }
        }

        private void Build_Click(object sender, RoutedEventArgs e)
        {
            if (_gray == null)
            {
                SummaryText.Text = "먼저 이미지를 불러오세요.";
                return;
            }

            try
            {
                var layout = new NozzleLayout(
                    rows:          Int(RowsBox.Text, 1),
                    nozzlesPerRow: Int(PerRowBox.Text, 1),
                    inRowPitchUm:  Num(InRowPitchBox.Text, 0.001),
                    rowOffsetUm:   Num(RowOffsetBox.Text, 0),
                    order: OrderBox.SelectedIndex == 1
                        ? NozzleLayout.NozzleOrder.RowByRow
                        : NozzleLayout.NozzleOrder.Interleaved);

                // 사용 노즐을 못 받았으면 전체를 쓴다 — 배열 자체를 확인하려는 목적이므로
                // "선택이 없어서 아무것도 안 나온다"가 되면 창을 연 의미가 없다.
                var used = _usedNozzles.Count > 0
                    ? _usedNozzles
                    : Enumerable.Range(1, layout.TotalNozzles).ToList();

                double umPerPx = Num(UmPerPxBox.Text, 0.001);
                var settings = new RipSettings
                {
                    DropLevels     = Math.Max(2, Int(LevelsBox.Text, 2)),
                    ScanStepUm     = Math.Max(0, Num(ScanStepBox.Text, 0)),
                    BlendHeadSeams = BlendBox.IsChecked == true,
                };

                var pattern = PrintPatternBuilder.Build(_gray, umPerPx, umPerPx, layout, used,
                                                        settings, out var ignored);
                ShowPattern(pattern, layout, ignored);
            }
            catch (Exception ex)
            {
                Preview.Pattern = null;
                SummaryText.Text = $"패턴을 만들지 못했습니다 — {ex.Message}";
            }
        }

        /// <summary>패턴을 그리고 요약을 쓴다. 새로 만든 것이든 변환에서 받아온 것이든 같은 자리다.</summary>
        private void ShowPattern(PrintPattern pattern, NozzleLayout? layout, IReadOnlyList<int> ignored)
        {
            Preview.Pattern = pattern;

            long drops = 0;
            for (int s = 0; s < pattern.Steps; s++)
                for (int c = 0; c < pattern.Nozzles; c++)
                    if (pattern.Levels[s, c] > 0) drops++;

            double widthMm = pattern.Nozzles > 1
                ? (pattern.Columns[^1].XUm - pattern.Columns[0].XUm) / 1000.0 : 0;

            string layoutPart = layout == null ? "" :
                $"노즐 {pattern.Nozzles}개 / 전체 {layout.TotalNozzles}   ·   ";
            string pitchPart = layout == null ? "" :
                $"실효 간격 {layout.EffectivePitchUm:F2}µm ({layout.EffectiveDpi:F0} dpi)   ·   ";

            SummaryText.Text =
                layoutPart +
                $"스텝 {pattern.Steps} × {pattern.ScanStepUm:F1}µm = {pattern.Steps * pattern.ScanStepUm / 1000.0:F1}mm   ·   " +
                $"인쇄 폭 {widthMm:F1}mm   ·   " +
                pitchPart +
                $"방울 {drops:N0}개" +
                (ignored.Count > 0 ? $"   ·   ⚠ 범위 밖 노즐 {ignored.Count}개 무시" : "");
        }

        // 입력이 비었거나 이상하면 최소값으로 — 창이 예외로 닫히지 않게 한다.
        private static int Int(string s, int min) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? Math.Max(min, v) : min;

        private static double Num(string s, double min) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? Math.Max(min, v) : min;
    }
}
