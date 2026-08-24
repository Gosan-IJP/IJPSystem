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

        /// <summary>
        /// 인쇄 산출물이 쌓이는 자리. 변환은 <c>AW_IMG_Data</c>(그림)와
        /// <c>IMG_TEMP\&lt;시각&gt;</c>(패턴·인쇄 데이터)에 남긴다.
        ///
        /// <para>인쇄 화면이 "가장 최근 저장물"을 찾을 때도 같은 자리를 본다 — 두 화면이
        /// 서로 다른 폴더를 보면 방금 저장한 것이 안 보인다.</para>
        /// </summary>
        public static string DefaultOutputRoot { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                         "GS_Inkjet");

        /// <summary>인쇄 데이터(패턴 폴더)가 쌓이는 자리.</summary>
        public static string PatternRoot => Path.Combine(DefaultOutputRoot, "IMG_TEMP");

        public string OutputRoot { get; set; } = DefaultOutputRoot;

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
            var pattern = BuildPattern(r.OutputPath!, param, stamp, out string? patternPath, out int steps);
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

        /// <summary>
        /// 이미 이미지가 있을 때의 변환 — DXF 단계를 건너뛰고 토출 패턴만 만든다.
        ///
        /// <para>
        /// DXF 는 "무엇을 찍을 것인가"를 그림으로 만드는 ①단계에만 쓰인다. BMP 를 열었거나
        /// Edit Panel 로 직접 그렸다면 ①은 이미 끝나 있으므로 ②(노즐 격자 → 하프톤)부터 하면 된다.
        /// 원본에서도 빈 레이어에 그려 저장하는 것이 정상 흐름이었다(기본 파일명 "Empty BMP_…").
        /// </para>
        /// </summary>
        public RasterizeResult ConvertImage(string imagePath, ConvertParameters param,
                                            IProgress<double>? progress = null)
        {
            if (!File.Exists(imagePath))
                throw new FileNotFoundException("이미지 파일이 없습니다.", imagePath);

            progress?.Report(0.2);
            var src = LoadPreview(imagePath)
                ?? throw new InvalidOperationException("이미지를 읽지 못했습니다: " + Path.GetFileName(imagePath));

            int wpx = src.PixelWidth, hpx = src.PixelHeight;
            string stamp = DateTime.Now.ToString("yyMMdd_HHmmss");

            progress?.Report(0.4);
            var pattern = BuildPattern(imagePath, param, stamp, out string? patternPath, out int steps);
            progress?.Report(0.95);

            // 이미지에는 치수가 없다 — 화소 수와 DPI 로 되짚는다. DXF 처럼 도면 단위가
            // 있는 게 아니라서, DPI 를 잘못 두면 여기서 바로 크기가 틀어진다.
            var result = new RasterizeResult
            {
                LineCount     = steps > 0 ? steps : hpx,
                RealXLengthMm = wpx * 25.4 / Math.Max(1e-6, param.DropPerInchX),
                RealYLengthMm = hpx * 25.4 / Math.Max(1e-6, param.DropPerInchY),
                BmpPath       = imagePath,
                PatternPath   = patternPath,
                PreviewImage  = src,
            };
            progress?.Report(1.0);
            _lastPattern = pattern;
            return result;
        }

        /// <summary>마지막 변환의 토출 패턴(1패스). 미리보기·전송이 다시 만들지 않고 이걸 쓴다.</summary>
        public PrintPattern? LastPattern => _lastPattern;
        private PrintPattern? _lastPattern;

        // Save 가 만들 인쇄 데이터(.dat)에 들어갈 값들 — 변환 시점의 것을 그대로 들고 있어야 한다.
        // 저장할 때 화면 값을 다시 읽으면, 변환 뒤에 DPI 를 만진 경우 패턴과 파라미터가 어긋난다.
        private ConvertParameters? _lastParam;
        private NozzleLayout?      _lastLayout;
        private double             _lastScanStepUm;

        /// <summary>마지막 변환에 쓰인 노즐 배열. 미리보기가 같은 값으로 보여 줘야 한다.</summary>
        public NozzleLayout? LastLayout => _lastLayout;

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
        private PrintPattern? BuildPattern(string imagePath, ConvertParameters param,
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
                var gray = LoadGray(imagePath);
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

                _lastParam      = param;
                _lastLayout     = layout;
                _lastScanStepUm = scanStep;

                string folder = Path.Combine(OutputRoot, "IMG_TEMP", stamp);
                PrintPatternFile.Save(folder, passes, new PrintPatternFile.PatternMeta
                {
                    DropLevels     = settings.DropLevels,
                    SourceImage    = imagePath,
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

        /// <summary>
        /// 빈 캔버스를 <b>실제 파일로</b> 만든다. (Create Empty Layer)
        ///
        /// <para>
        /// 예전에는 경로만 정해 놓고 파일을 안 만들었다. 그러면 Edit Panel 로 그림을 그려도
        /// 래스터라이저가 읽을 것이 없어 변환도 저장도 못 한다 — 빈 레이어 흐름이 통째로 막혔다.
        /// 흰 이미지를 실제로 써 두면 그 뒤는 BMP 를 연 것과 똑같이 흘러간다.
        /// </para>
        /// </summary>
        public RasterizeResult CreateEmptyLayer(ConvertParameters param, double widthMm, double lengthMm)
        {
            string stamp = DateTime.Now.ToString("yyMMdd_HHmmss");
            string dir   = Path.Combine(OutputRoot, "AW_IMG_Data");
            Directory.CreateDirectory(dir);
            string path  = Path.Combine(dir, $"Empty_{stamp}.bmp");

            int w = (int)Math.Round(widthMm  * Math.Max(1e-6, param.DropPerInchX) / 25.4);
            int h = (int)Math.Round(lengthMm * Math.Max(1e-6, param.DropPerInchY) / 25.4);
            w = Math.Max(1, w);
            h = Math.Max(1, h);

            WriteWhiteBmp(path, w, h);

            return new RasterizeResult
            {
                RealXLengthMm = widthMm,
                RealYLengthMm = lengthMm,
                BmpPath       = path,
                PatternPath   = null,     // 아직 아무것도 안 그렸다 — 변환해야 패턴이 생긴다
                PreviewImage  = LoadPreview(path),
            };
        }

        /// <summary>흰 8비트 BMP 한 장. 빈 캔버스라 화소가 전부 255 다.</summary>
        private static void WriteWhiteBmp(string path, int w, int h)
        {
            var bmp = new System.Windows.Media.Imaging.WriteableBitmap(
                w, h, 96, 96, System.Windows.Media.PixelFormats.Gray8, null);
            var row = new byte[w];
            for (int x = 0; x < w; x++) row[x] = 255;
            for (int y = 0; y < h; y++)
                bmp.WritePixels(new System.Windows.Int32Rect(0, y, w, 1), row, w, 0);
            bmp.Freeze();

            var enc = new System.Windows.Media.Imaging.BmpBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            enc.Save(fs);
        }

        public RasterizeResult OpenBmp(string bmpPath)
        {
            if (!File.Exists(bmpPath))
                throw new FileNotFoundException("BMP 파일이 없습니다.", bmpPath);
            return new RasterizeResult { BmpPath = bmpPath, PreviewImage = LoadPreview(bmpPath) };
        }

        /// <summary>
        /// 인쇄 데이터를 굳힌다. (LabVIEW 저장 버튼 대응)
        ///
        /// <para>
        /// 변환은 이미 <c>pattern.json</c> + <c>pattern.bin</c> 을 남겼다. 저장이 따로 있는 이유는
        /// 원본이 그랬듯 <b>인쇄기가 읽을 형태</b>로 한 벌 더 내보내기 때문이다 —
        /// 패턴 비트맵(눈으로 확인) + POS.dat(노즐 위치) + Print_Para.dat(인쇄 파라미터).
        /// </para>
        /// <para>
        /// 원본은 이 셋을 <c>AW_IMG_Data</c> 에 흩어 놓았지만 여기서는 패턴 폴더 안에 같이 둔다.
        /// 한 번의 변환에서 나온 것들이 한 폴더에 모여 있어야 나중에 무엇으로 찍었는지 되짚을 수 있다.
        /// </para>
        /// </summary>
        public SavedPrintData Save(RasterizeResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            var pattern = _lastPattern;
            if (pattern == null || _lastParam == null || _lastLayout == null)
                throw new InvalidOperationException(
                    "저장할 토출 패턴이 없습니다 — Convert 를 먼저 실행하세요. " +
                    "(BMP 만 연 경우에도 패턴은 만들어지지 않습니다)");

            string folder = result.PatternPath
                ?? throw new InvalidOperationException("패턴 폴더 경로가 없습니다.");

            var layout = _lastLayout;
            int dropLevels = Math.Max(2, _lastParam.DropLevels);

            // 인쇄물 크기는 이미지가 아니라 패턴에서 구한다 — 실제로 찍히는 것은 노즐이 닿는
            // 범위와 스텝 수이지, 원본 도면의 크기가 아니다.
            double widthMm = 0;
            if (pattern.Columns.Count > 0)
            {
                double min = double.MaxValue, max = double.MinValue;
                foreach (var c in pattern.Columns)
                {
                    if (c.XUm < min) min = c.XUm;
                    if (c.XUm > max) max = c.XUm;
                }
                widthMm = (max - min) / 1000.0;
            }
            double heightMm = pattern.Steps * _lastScanStepUm / 1000.0;

            var para = new PrintDataSet.PrintPara
            {
                DpiX          = _lastParam.DropPerInchX,
                DpiY          = _lastParam.DropPerInchY,
                WidthPx       = pattern.Nozzles,
                HeightPx      = pattern.Steps,
                WidthMm       = widthMm,
                HeightMm      = heightMm,
                HeadCount     = layout.HeadCount,
                NozzlePerHead = layout.Rows * layout.NozzlesPerRow,
                BitsPerPixel  = dropLevels <= 2 ? 1 : 8,
                HeadPack      = 0,
                Overlap       = _lastParam.BlendHeadSeams ? 1 : 0,
                SubPixelX     = Math.Max(1, _lastParam.Interval),
                SubPixelY     = Math.Max(1, _lastParam.Interval),
            };

            string baseName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(baseName)) baseName = "Pattern";

            var (bmp, pos, parp) = PrintDataSet.Save(folder, baseName, pattern, para, dropLevels);
            return new SavedPrintData(folder, bmp, pos, parp, pattern.Steps, pattern.Nozzles);
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
