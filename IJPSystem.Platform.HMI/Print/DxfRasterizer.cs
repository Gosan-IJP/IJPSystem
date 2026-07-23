using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using IJPSystem.Platform.Application.Printing;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// 실제 DXF 래스터라이저 — <see cref="DxfRasterizerStub"/> 를 대체한다.
    /// DXF 벡터를 인쇄 DPI 로 래스터화(닫힌 도형 채움)해 BMP/PNG 를 만들고 미리보기를 제공한다.
    /// 변환 코어는 HW 무관한 <see cref="DxfToBitmap"/>(Platform.Application) 이고,
    /// 여기서는 WPF 미리보기(BitmapSource) 생성과 파일 배치만 담당한다.
    ///
    /// ※ 아직 미구현: 하프톤 디더링, 노즐별 토출 패턴(Samba12/S800 Pattern GEN) 생성.
    ///   현재는 "면 채움 비트맵"까지다. 패턴 생성은 헤드 프로토콜이 확정돼야 한다.
    /// </summary>
    public sealed class DxfRasterizer : IDxfRasterizer
    {
        private string? _dxfPath;

        public string OutputRoot { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                         "GS_Inkjet");

        /// <summary>닫힌 도형 내부를 채울지. (사용자 선택: 채움)</summary>
        public bool Fill { get; set; } = true;

        /// <summary>DXF 도면 단위 → mm. 도면이 mm 면 1.0.</summary>
        public double UnitToMm { get; set; } = 1.0;

        public IReadOnlyList<string> LoadDxf(string dxfPath)
        {
            if (!File.Exists(dxfPath))
                throw new FileNotFoundException("DXF 파일이 없습니다.", dxfPath);
            _dxfPath = dxfPath;

            var layers = DxfToBitmap.GetLayers(dxfPath);
            // 레이어 정보가 없더라도(모두 기본 레이어) 변환은 가능해야 하므로 기본 레이어명을 넣는다.
            return layers.Count > 0 ? layers : new[] { "0" };
        }

        public RasterizeResult Convert(IReadOnlyList<string> selectedLayers,
            ConvertParameters param, IProgress<double>? progress = null)
        {
            if (_dxfPath == null) throw new InvalidOperationException("DXF 를 먼저 Load 하세요.");
            progress?.Report(0.1);

            string bmpDir = Path.Combine(OutputRoot, "AW_IMG_Data");
            string stamp  = DateTime.Now.ToString("yyMMdd_HHmmss");
            string outPng = Path.Combine(bmpDir, $"BMP_{stamp}.png");

            var opt = new DxfRasterOptions
            {
                Dpi         = Math.Max(1, (int)Math.Round(param.DropPerInchX)),
                DpiY        = Math.Max(1, (int)Math.Round(param.DropPerInchY)),
                UnitToMm    = UnitToMm,
                Fill        = Fill,
                LayerFilter = selectedLayers is { Count: > 0 }
                    ? new HashSet<string>(selectedLayers, StringComparer.OrdinalIgnoreCase)
                    : null,
            };

            progress?.Report(0.4);
            var r = DxfToBitmap.Convert(_dxfPath, outPng, opt);
            progress?.Report(0.9);

            if (!r.Success)
                throw new InvalidOperationException($"DXF 변환 실패: {r.Message}");

            var result = new RasterizeResult
            {
                // 인쇄 라인 수 = 세로 픽셀(스캔 라인 1줄 = 1픽셀 행). 패턴 생성 전 근사값.
                LineCount     = r.HeightPx,
                RealXLengthMm = r.WidthMm,
                RealYLengthMm = r.HeightMm,
                BmpPath       = r.OutputPath,
                PatternPath   = null,   // 토출 패턴 생성은 미구현
                PreviewImage  = LoadPreview(r.OutputPath),
            };
            progress?.Report(1.0);
            return result;
        }

        public RasterizeResult CreateEmptyLayer(ConvertParameters param)
        {
            // 빈 레이어(빈 캔버스) — DXF 없이 흰 배경만. Edit Panel 로 직접 그릴 용도.
            // 크기는 호출부(CanvasSizeDialog)가 정하지만, 여기선 결과 골격만 만든다.
            string stamp = DateTime.Now.ToString("yyMMdd_HHmmss");
            return new RasterizeResult
            {
                BmpPath     = Path.Combine(OutputRoot, "AW_IMG_Data", $"Empty_{stamp}.png"),
                PatternPath = null,
            };
        }

        public RasterizeResult OpenBmp(string bmpPath)
        {
            if (!File.Exists(bmpPath))
                throw new FileNotFoundException("BMP 파일이 없습니다.", bmpPath);
            return new RasterizeResult { BmpPath = bmpPath, PreviewImage = LoadPreview(bmpPath) };
        }

        public void Save(RasterizeResult result)
        {
            // 변환 시 이미 파일로 저장되므로(BmpPath) 별도 동작 없음.
            // 다른 경로로 내보내기가 필요하면 여기서 복사한다.
            if (result == null) throw new ArgumentNullException(nameof(result));
        }

        // 파일 잠금을 피하려고 전부 읽어 Freeze — 변환 직후 재변환/삭제가 막히지 않게 한다.
        private static BitmapSource? LoadPreview(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption   = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource     = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }
}
