using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using IJPSystem.Platform.Application.Printing;
using IJPSystem.Platform.Infrastructure.Config;
using IJPSystem.Platform.Infrastructure.Print;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// 실제 DXF 래스터라이저 — <see cref="DxfRasterizerStub"/> 를 대체한다.
    /// DXF 벡터를 인쇄 DPI 로 래스터화(닫힌 도형 채움)해 BMP/PNG 를 만들고 미리보기를 제공한다.
    /// 변환 코어는 HW 무관한 <see cref="DxfToBitmap"/>(Platform.Application) 이고,
    /// 여기서는 WPF 미리보기(BitmapSource) 생성과 파일 배치만 담당한다.
    ///
    /// 변환은 두 단계다:
    ///   ① DXF → 채움 비트맵 (무엇을 찍을 것인가)
    ///   ② 비트맵 → 노즐 격자 → 하프톤 → 토출 패턴 (어느 노즐이 언제 무엇을 쏘는가)
    /// ②는 <see cref="PrintPatternBuilder"/> 가 하고 결과는 <see cref="PrintPatternFile"/> 로 남긴다.
    ///
    /// ※ 패턴을 PCC 로 보내는 단계는 아직 없다 — 헤드 연결이 필요하다. 지금은 파일까지다.
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
            progress?.Report(0.7);

            if (!r.Success)
                throw new InvalidOperationException($"DXF 변환 실패: {r.Message}");

            // 채움 비트맵은 "무엇을 찍을 것인가" 일 뿐이다. 실제로 인쇄하려면 그것을 노즐 격자로
            // 옮기고(어느 노즐이 어느 X 를 맡는가) 헤드가 낼 수 있는 방울 단계로 낮춰야 한다.
            var pattern = BuildPattern(r, param, stamp, out string? patternPath, out int steps);
            progress?.Report(0.95);

            var result = new RasterizeResult
            {
                // 인쇄 라인 수 = 패턴 스텝 수(엔코더 한 칸에 한 줄). 패턴을 못 만들었으면
                // 세로 픽셀 수로 대신한다 — 0 을 내보내면 "변환됐는데 0줄"로 보인다.
                LineCount     = steps > 0 ? steps : r.HeightPx,
                RealXLengthMm = r.WidthMm,
                RealYLengthMm = r.HeightMm,
                BmpPath       = r.OutputPath,
                PatternPath   = patternPath,
                PreviewImage  = LoadPreview(r.OutputPath),
            };
            progress?.Report(1.0);
            _lastPattern = pattern;
            return result;
        }

        /// <summary>마지막 변환의 토출 패턴(1패스). 미리보기·전송이 다시 만들지 않고 이걸 쓴다.</summary>
        public PrintPattern? LastPattern => _lastPattern;
        private PrintPattern? _lastPattern;

        /// <summary>마지막 변환의 모든 패스. Interval 이 1 이면 한 개다.</summary>
        public IReadOnlyList<PrintPattern> LastPasses { get; private set; } = Array.Empty<PrintPattern>();

        /// <summary>변환에서 헤드 범위 밖이라 버려진 노즐 번호. 비어 있지 않으면 번호 기준을 의심할 것.</summary>
        public IReadOnlyList<int> LastIgnoredNozzles { get; private set; } = Array.Empty<int>();

        /// <summary>
        /// 채움 비트맵 → 노즐 격자 → 하프톤 → 패턴 파일.
        ///
        /// <para>
        /// 실패해도 변환 전체를 깨지 않는다 — 비트맵은 이미 나왔고 화면에서 확인할 수 있다.
        /// 노즐 미선택처럼 흔한 상황에서 "DXF 변환 실패"가 뜨면 원인을 엉뚱한 데서 찾게 된다.
        /// </para>
        /// </summary>
        private PrintPattern? BuildPattern(DxfRasterResult raster, ConvertParameters param,
                                           string stamp, out string? patternPath, out int steps)
        {
            patternPath = null;
            steps = 0;
            LastIgnoredNozzles = Array.Empty<int>();
            LastPasses         = Array.Empty<PrintPattern>();

            var used = param.UsingNozzles;
            if (used == null || used.Count == 0)
            {
                PatternMessage = "사용 노즐이 지정되지 않아 토출 패턴을 만들지 않았습니다 — Nozzle Select 로 지정하세요.";
                return null;
            }

            try
            {
                var gray = LoadGray(raster.OutputPath!);
                if (gray == null) { PatternMessage = "변환 결과 이미지를 다시 읽지 못했습니다."; return null; }

                var layout = HeadLayout();

                // Interval Change — 방울을 놓는 간격을 이 수로 나눈다. 1 = 노즐 간격 그대로, 2 = ½.
                int div = Math.Max(1, param.Interval);
                double baseStep   = param.ScanStepUm > 0 ? param.ScanStepUm : layout.EffectivePitchUm;
                double scanStep   = baseStep / div;                     // 스캔 방향은 엔코더만 잘게 쓰면 된다
                double passOffset = div > 1 ? layout.EffectivePitchUm / div : 0;

                var settings = new RipSettings
                {
                    DropLevels     = Math.Max(2, param.DropLevels),
                    ScanStepUm     = scanStep,
                    OriginXUm      = 0,
                    BlendHeadSeams = param.BlendHeadSeams,
                };

                double umPxX = 25400.0 / Math.Max(1e-6, param.DropPerInchX);
                double umPxY = 25400.0 / Math.Max(1e-6, param.DropPerInchY);

                // 가로는 노즐 피치가 하드웨어라 한 번 지나가서는 못 좁힌다. 헤드를 피치의 1/div 만큼
                // 옮겨 div 번 지나간다 — 패스 k 는 자기 노즐 X 에서 k×오프셋 만큼 옆의 화소를 읽는다.
                // (OriginXUm 은 "이미지 원점" 이라 헤드가 오른쪽으로 가는 것과 부호가 반대다)
                var passes  = new List<PrintPattern>(div);
                IReadOnlyList<int> ignored = Array.Empty<int>();
                for (int k = 0; k < div; k++)
                {
                    var s = new RipSettings
                    {
                        DropLevels     = settings.DropLevels,
                        ScanStepUm     = settings.ScanStepUm,
                        OriginXUm      = -k * passOffset,
                        BlendHeadSeams = settings.BlendHeadSeams,
                    };
                    passes.Add(PrintPatternBuilder.Build(gray, umPxX, umPxY, layout, used, s, out ignored));
                }

                var pattern = passes[0];
                LastIgnoredNozzles = ignored;
                LastPasses = passes;
                steps = pattern.Steps;

                string folder = Path.Combine(OutputRoot, "IMG_TEMP", stamp);
                PrintPatternFile.Save(folder, passes, new PrintPatternFile.PatternMeta
                {
                    DropLevels     = settings.DropLevels,
                    SourceImage    = raster.OutputPath,
                    SourceDpiX     = param.DropPerInchX,
                    SourceDpiY     = param.DropPerInchY,
                    CreatedAt      = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    IgnoredNozzles = ignored,
                    PassOffsetXUm  = passOffset,
                });
                patternPath = folder;

                string body = $"토출 패턴 {pattern.Steps}스텝 × {pattern.Nozzles}노즐";
                if (div > 1) body += $" · 간격 1/{div} ({div}패스, 패스간 {passOffset:0.##}µm)";
                if (ignored.Count > 0)
                    body += $" (헤드 범위 밖 {ignored.Count}개 제외: {string.Join(",", ignored)})";
                PatternMessage = body;
                return pattern;
            }
            catch (Exception ex)
            {
                PatternMessage = "토출 패턴 생성 실패: " + ex.Message;
                return null;
            }
        }

        /// <summary>마지막 패턴 생성 결과 설명(성공/실패/제외 노즐). 화면 상태줄에 그대로 쓴다.</summary>
        public string? PatternMessage { get; private set; }

        /// <summary>
        /// 헤드 노즐 배열. 수량·열 수는 <see cref="HeadSpec"/>(레시피의 노즐 정보)에서 오고,
        /// 간격은 장비 설정의 노즐 피치를 쓴다 — 화면마다 다른 숫자를 들고 있으면 안 된다.
        /// </summary>
        private static NozzleLayout HeadLayout()
        {
            int rows    = Math.Max(1, HeadSpec.Rows);
            int perRow  = Math.Max(1, HeadSpec.NozzlesPerRow);

            // 장비 설정이 아직 안 열렸으면(도구·테스트 경로) 기본값으로 간다 — HeadSpec 과 같은 규칙.
            // Current 를 그냥 부르면 예외가 나고, 그러면 "패턴 생성 실패"만 뜬 채 원인이 안 보인다.
            double pitch = 0;
            if (MachineSettings.IsReady)
            {
                try { pitch = MachineSettings.Current.GetDouble(MachineSettingsStore.Keys.NozzlePitchUm); }
                catch { pitch = 0; }
            }
            if (pitch <= 0) pitch = DefaultInRowPitchUm;

            // 열 간 오프셋 = 한 열 간격 ÷ 열 수. 열이 엇갈려 실효 간격을 그만큼 좁히는 배열이다.
            return new NozzleLayout(rows, perRow, pitch, pitch / rows,
                                    firstNozzleNumber: HeadSpec.FirstNozzle);
        }

        /// <summary>장비 설정에 노즐 피치가 없을 때 쓸 값 [µm]. S800 한 열 간격.</summary>
        private const double DefaultInRowPitchUm = 254.1;

        /// <summary>
        /// PNG/BMP → [y, x] 농담 배열. <b>값이 클수록 진하다</b>(잉크량 기준).
        /// 도면은 흰 바탕에 검은 잉크라 밝기를 뒤집는다.
        /// </summary>
        private static byte[,]? LoadGray(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            var src = LoadPreview(path);
            if (src == null) return null;

            var gray8 = new FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Gray8, null, 0);
            int w = gray8.PixelWidth, h = gray8.PixelHeight;
            if (w <= 0 || h <= 0) return null;

            int stride = w;
            var buf = new byte[stride * h];
            gray8.CopyPixels(buf, stride, 0);

            var g = new byte[h, w];
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++) g[y, x] = (byte)(255 - buf[row + x]);   // 어두울수록 진하다
            }
            return g;
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

        // 미리보기는 저장 파일 그대로다 — 흰 바탕(비인쇄) + 검은 잉크(인쇄).
        // 색을 바꿔 보여주면 "화면에서 본 것"과 "파일에 든 것"이 달라져, 인쇄가 뒤집혔을 때
        // 화면을 믿을 수 없게 된다. 범례 쪽을 파일에 맞췄다.

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
