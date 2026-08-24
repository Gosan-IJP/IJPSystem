using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using IJPSystem.Platform.Infrastructure.Print;
using Microsoft.Win32;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// "DXF Rasterizer" 화면(Rasterizer_Main.vi) 로직 (MVVM ViewModel).
    /// </summary>
    public sealed class DxfRasterizerViewModel : INotifyPropertyChanged
    {
        private readonly IDxfRasterizer _rip;

        /// <summary>Nozzle Select 화면 호출 동작. 외부 주입(20_Screen_Nozzle Select 대응).</summary>
        public Func<IReadOnlyList<int>>? NozzleSelectAction { get; set; }

        /// <summary>캔버스 크기 입력 다이얼로그 호출. (widthMm,lengthMm) 반환, 취소 시 null. 외부 주입.</summary>
        public Func<(double widthMm, double lengthMm)?>? RequestCanvasSize { get; set; }

        /// <summary>지정 크기로 Edit Panel(Drawing Panel) 편집 창 열기. 외부 주입.</summary>
        /// <summary>
        /// 알림 상자(제목, 본문). 창이 채워 준다 — 이 창은 모달이라 소유자를 제대로 잡아야
        /// 상자가 뒤로 숨지 않는다. 그래서 VM 이 직접 MessageBox 를 띄우지 않는다.
        /// </summary>
        public Action<string, string>? Notify { get; set; }

        /// <summary>
        /// 편집 창을 띄운다. 인자는 (가로mm, 세로mm, 저장할 자리) — 그린 그림이 저장된 경로를 돌려준다.
        /// 돌려받지 못하면(취소) 빈 캔버스 그대로 남는다.
        /// </summary>
        public Func<double, double, string?, string?>? OpenEditPanel { get; set; }

        public DxfRasterizerViewModel(IDxfRasterizer rasterizer)
        {
            _rip = rasterizer ?? throw new ArgumentNullException(nameof(rasterizer));

            LoadDxfCommand          = new RelayCommand(_ => LoadDxf());
            IntervalChangeCommand   = new RelayCommand(_ => IntervalChange());
            NozzleSelectCommand     = new RelayCommand(_ => NozzleSelect());
            ConvertCommand          = new RelayCommand(_ => Convert(), _ => CanConvert);
            CreateEmptyLayerCommand = new RelayCommand(_ => CreateEmptyLayer());
            OpenBmpCommand          = new RelayCommand(_ => OpenBmp());
            SaveCommand             = new RelayCommand(_ => Save(), _ => _lastResult?.PatternPath != null);
            ZoomToFitCommand        = new RelayCommand(_ => ZoomToFitRequested?.Invoke(this, EventArgs.Empty));
            ToggleGridCommand       = new RelayCommand(_ => ShowGrid = !ShowGrid);
        }

        // ---- Convert Parameters ----
        private double _dpiX = 600, _dpiY = 600;
        public double DropPerInchX { get => _dpiX; set { _dpiX = value; OnPropertyChanged(); UpdateMeasuredLength(); } }
        public double DropPerInchY { get => _dpiY; set { _dpiY = value; OnPropertyChanged(); UpdateMeasuredLength(); } }

        /// <summary>
        /// 방울 간격 분할 수. 1 = 노즐 간격 그대로, 2 = ½(2패스).
        /// 값의 뜻은 <see cref="ConvertParameters.Interval"/> 참고.
        /// </summary>
        private int _interval = 1;
        public int Interval
        {
            get => _interval;
            set
            {
                _interval = Math.Max(1, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IntervalCaption));
                OnPropertyChanged(nameof(IntervalText));
            }
        }

        /// <summary>버튼 글자 — 지금 누르면 어떻게 되는지를 보여준다.</summary>
        public string IntervalCaption => _interval == 1 ? "½↓   Interval Change" : "×2↑   Interval Change";

        /// <summary>버튼 아래 설명 — 지금 어떤 상태인지.</summary>
        public string IntervalText => _interval == 1
            ? "방울 간격 = 노즐 간격 (1패스)"
            : $"방울 간격 = 노즐 간격 ÷ {_interval} ({_interval}패스)";

        // ---- 읽기 표시값 ----
        private int _lineCount;
        public int LineCount { get => _lineCount; set { _lineCount = value; OnPropertyChanged(); } }

        private double _realX, _realY;
        public double RealXLengthMm { get => _realX; set { _realX = value; OnPropertyChanged(); } }
        public double RealYLengthMm { get => _realY; set { _realY = value; OnPropertyChanged(); } }

        // ---- Using Nozzles 표시 ----
        // 줄 수·노즐 수는 헤드 사양(HeadSpec, 레시피의 노즐 정보)에서만 온다.
        // 예전에는 여기서 "Row 1 / Row 2" 두 줄을 만들고 화면에는 한 줄 400칸을 박아 놨었다.
        // 4열 헤드로 바꾸자 한 줄이 200개가 되어 막대가 절반만 차고 401~800 은 아예 사라졌다.
        // 인스턴스 속성인 이유: WPF 바인딩은 DataContext 의 정적 속성을 찾지 못한다.
        /// <summary>막대를 몇 줄로 그릴지 = 헤드 열 수.</summary>
        public int NozzleRows   => Platform.Infrastructure.Config.HeadSpec.Rows;
        public int FirstNozzle  => Platform.Infrastructure.Config.HeadSpec.FirstNozzle;
        public int TotalNozzles => Platform.Infrastructure.Config.HeadSpec.Count;

        private int _usingNozzleCount;
        public int UsingNozzleCount { get => _usingNozzleCount; set { _usingNozzleCount = value; OnPropertyChanged(); } }
        private IReadOnlyList<int> _usingNozzles = new List<int>();

        private IReadOnlyCollection<int> _usingNozzleSet = Array.Empty<int>();
        /// <summary>막대가 초록으로 칠할 사용 노즐.</summary>
        public IReadOnlyCollection<int> UsingNozzleSet
        {
            get => _usingNozzleSet;
            private set { _usingNozzleSet = value; OnPropertyChanged(); }
        }

        /// <summary>외부(전역 선택)에서 사용 노즐을 초기화한다(창 열 때).</summary>
        public void InitUsingNozzles(IReadOnlyList<int> nozzles)
        {
            _usingNozzles = nozzles ?? new List<int>();
            UsingNozzleCount = _usingNozzles.Count;
            UsingNozzleSet = new HashSet<int>(_usingNozzles);
        }

        // ---- Length Measure ----
        private bool _lengthMeasure = true;
        public bool LengthMeasureEnabled { get => _lengthMeasure; set { _lengthMeasure = value; OnPropertyChanged(); } }

        /// <summary>
        /// 화면에 그려진 대각선의 실제 길이 [mm].
        ///
        /// <para>
        /// 예전에는 상수 53.207 을 박아 두고 320×320 고정 격자에 선을 그었다. 그림도 숫자도
        /// 실제 도면과 무관해서, 11×7mm 를 변환해도 53.207mm 라고 나왔다.
        /// 지금은 미리보기 픽셀 수 ÷ DPI 로 계산한다 — 화면에 보이는 그 선의 길이다.
        /// </para>
        /// </summary>
        private double _measuredLength;
        public double MeasuredLengthMm { get => _measuredLength; private set { _measuredLength = value; OnPropertyChanged(); } }

        private void UpdateMeasuredLength()
        {
            double xMm = _dpiX > 0 ? PreviewWidthPx  / _dpiX * 25.4 : 0;
            double yMm = _dpiY > 0 ? PreviewHeightPx / _dpiY * 25.4 : 0;
            MeasuredLengthMm = Math.Sqrt(xMm * xMm + yMm * yMm);
        }

        // ---- 미리보기 ----
        private bool _showGrid;
        public bool ShowGrid { get => _showGrid; set { _showGrid = value; OnPropertyChanged(); } }

        private object? _previewImage;
        public object? PreviewImage
        {
            get => _previewImage;
            set
            {
                _previewImage = value;

                // 측정선을 이미지와 같은 좌표계에 두려면 픽셀 크기를 화면이 알아야 한다.
                var src = value as BitmapSource;
                PreviewWidthPx  = src?.PixelWidth  ?? 0;
                PreviewHeightPx = src?.PixelHeight ?? 0;
                UpdateMeasuredLength();

                // ★ 알림은 맨 마지막이다. 화면은 이 알림을 받고 Zoom To Fit 을 하는데,
                //   먼저 알리면 그때 크기가 아직 0 이라 맞출 것이 없다고 보고 그냥 빠져나간다.
                //   (그러면 첫 변환 결과가 100% 배율로 왼쪽 위에 붙어서 나온다)
                OnPropertyChanged();
            }
        }

        /// <summary>미리보기 이미지의 픽셀 크기. 측정선이 이미지 모서리를 정확히 잇도록 화면이 쓴다.</summary>
        private double _previewW, _previewH;
        public double PreviewWidthPx  { get => _previewW; private set { _previewW = value; OnPropertyChanged(); } }
        public double PreviewHeightPx { get => _previewH; private set { _previewH = value; OnPropertyChanged(); } }

        // ---- 레이어 ----
        public ObservableCollection<LayerItem> Layers { get; } = new ObservableCollection<LayerItem>();

        // ---- 경로/상태 ----
        private string _dxfPath = "", _bmpPath = "", _patternPath = "", _status = "";
        public string DxfPath { get => _dxfPath; set { _dxfPath = value; OnPropertyChanged(); } }
        public string BmpPath { get => _bmpPath; set { _bmpPath = value; OnPropertyChanged(); } }
        public string PatternPath { get => _patternPath; set { _patternPath = value; OnPropertyChanged(); } }
        public string StatusText { get => _status; set { _status = value; OnPropertyChanged(); } }

        private double _progress;
        public double LoadingProgress { get => _progress; set { _progress = value; OnPropertyChanged(); } }

        private RasterizeResult? _lastResult;


        // ---- 미리보기 ----
        // 미리보기는 변환 결과를 그대로 보여야 한다. 이미지에서 다시 만들면 그 창의 입력값으로
        // RIP 이 한 번 더 돌아, 저장한 것과 다른 그림을 보면서 맞다고 판단하게 된다.
        public PrintPattern? LastPattern => (_rip as DxfRasterizer)?.LastPattern;
        public NozzleLayout? LastLayout  => (_rip as DxfRasterizer)?.LastLayout;
        public IReadOnlyList<int> LastIgnoredNozzles =>
            (_rip as DxfRasterizer)?.LastIgnoredNozzles ?? Array.Empty<int>();
        // ---- 커맨드 ----
        public ICommand LoadDxfCommand { get; }
        public ICommand IntervalChangeCommand { get; }
        public ICommand NozzleSelectCommand { get; }
        public ICommand ConvertCommand { get; }
        public ICommand CreateEmptyLayerCommand { get; }
        public ICommand OpenBmpCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand ZoomToFitCommand { get; }
        public ICommand ToggleGridCommand { get; }

        /// <summary>View 가 구독하여 미리보기 줌 처리.</summary>
        public event EventHandler? ZoomToFitRequested;

        // ---- 동작 ----
        private void LoadDxf()
        {
            var dlg = new OpenFileDialog { Title = "Load DXF", Filter = "DXF (*.dxf)|*.dxf|All (*.*)|*.*" };
            if (dlg.ShowDialog() != true) return;
            LoadDxfFrom(dlg.FileName);
        }

        /// <summary>
        /// 파일 대화상자 없이 DXF 를 연다. 창을 열 때 넘겨받은 경로에도 쓴다 —
        /// 경로만 칸에 채워 두면 레이어가 비어 Convert 가 잠긴 채로 남아, 왜 안 되는지 알 수 없다.
        /// </summary>
        public void LoadDxfFrom(string path)
        {
            try
            {
                var names = _rip.LoadDxf(path);
                _hasDxf = true;
                DxfPath = path;
                Layers.Clear();
                foreach (string name in names)
                    Layers.Add(new LayerItem { Name = name, IsSelected = true });
                LoadingProgress = 1.0;
                StatusText = $"DXF 로드: 레이어 {Layers.Count}개";
            }
            catch (Exception ex)
            {
                DxfPath = path;
                StatusText = "DXF 로드 실패: " + ex.Message;
            }
        }

        private void IntervalChange()
        {
            // ½ ↔ 1배 토글. 다시 변환해야 실제로 적용된다 — 값만 바뀌고 끝나면
            // "눌렀으니 반영됐겠지" 하고 예전 패턴을 그대로 인쇄로 들고 간다.
            Interval = Interval == 1 ? 2 : 1;
            StatusText = _lastResult == null
                ? IntervalText
                : IntervalText + " — Convert 를 다시 눌러야 패턴에 반영됩니다.";
        }

        private void NozzleSelect()
        {
            InitUsingNozzles(NozzleSelectAction?.Invoke() ?? _usingNozzles);
            StatusText = $"사용 노즐 {UsingNozzleCount}개";
        }

        private ConvertParameters BuildParams() => new ConvertParameters
        {
            DropPerInchX = DropPerInchX,
            DropPerInchY = DropPerInchY,
            Interval = Interval,
            UsingNozzles = _usingNozzles,
            DropLevels   = DropLevels,
        };

        /// <summary>
        /// 방울 크기 단계 수. 2 = 찍/안찍(이진), 그 이상이면 그레이스케일 토출.
        /// 헤드가 실제로 낼 수 있는 단계보다 크게 두면 패턴에 못 쏘는 값이 들어간다.
        /// </summary>
        private int _dropLevels = 2;
        public int DropLevels
        {
            get => _dropLevels;
            set { _dropLevels = Math.Max(2, value); OnPropertyChanged(); }
        }

        /// <summary>
        /// 변환. DXF 를 열었으면 도면부터, 아니면 지금 이미지에서 토출 패턴만 만든다.
        ///
        /// <para>Open BMP·Edit Panel 로 들어온 그림도 변환할 수 있어야 한다 — DXF 는 ①단계에만
        /// 필요하고, 그림이 이미 있으면 ②(노즐 격자 → 하프톤)부터 하면 된다.</para>
        /// </summary>
        private void Convert()
        {
            try
            {
                var progress = new Progress<double>(p => LoadingProgress = p);

                if (HasDxf)
                {
                    var selected = Layers.Where(l => l.IsSelected).Select(l => l.Name).ToList();
                    _lastResult = _rip.Convert(selected, BuildParams(), progress);
                }
                else
                {
                    string? img = _lastResult?.BmpPath;
                    if (string.IsNullOrEmpty(img))
                    {
                        StatusText = "변환할 것이 없습니다 — DXF 를 열거나 BMP 를 여세요.";
                        return;
                    }
                    _lastResult = _rip.ConvertImage(img!, BuildParams(), progress);
                }

                // 패턴 생성 결과를 함께 띄운다 — 노즐 미선택 등으로 패턴만 빠져도 화면에는
                // "변환 완료"로만 보여, 인쇄 직전에야 패턴이 없다는 걸 알게 된다.
                string msg = (_rip as DxfRasterizer)?.PatternMessage is string p and { Length: > 0 }
                           ? $"변환 완료 — {p}"
                           : "변환 완료";
                ApplyResult(msg);
            }
            catch (Exception ex) { StatusText = "변환 실패: " + ex.Message; }
        }

        /// <summary>DXF 를 열어 레이어를 고를 수 있는 상태인가.</summary>
        private bool HasDxf => _hasDxf && Layers.Any(l => l.IsSelected);
        private bool _hasDxf;

        /// <summary>변환할 거리가 있는가 — 도면이든 그림이든.</summary>
        private bool CanConvert =>
            HasDxf || (!string.IsNullOrEmpty(_lastResult?.BmpPath) && File.Exists(_lastResult!.BmpPath!));

        private void CreateEmptyLayer()
        {
            // 1) 캔버스 크기 입력 (Set size of canvas). 취소 시 중단.
            var size = RequestCanvasSize?.Invoke();
            if (size == null) return;
            var (w, l) = size.Value;

            try
            {
                // 2) 흰 캔버스를 실제 파일로 만든다. DXF 가 아니므로 레이어 선택은 쓰지 않는다.
                _hasDxf = false;
                Layers.Clear();
                _lastResult = _rip.CreateEmptyLayer(BuildParams(), w, l);
                ApplyResult($"빈 레이어 생성 ({w:0.#}×{l:0.#}mm) — 그린 뒤 Convert 하세요");

                // 3) Edit Panel — 그린 그림은 방금 만든 그 파일에 덮어쓴다.
                string? drawn = OpenEditPanel?.Invoke(w, l, _lastResult.BmpPath);
                if (string.IsNullOrEmpty(drawn)) { StatusText = "빈 캔버스 그대로입니다 — 그리지 않았습니다."; return; }

                _lastResult = _rip.OpenBmp(drawn!);
                ApplyResult("그림 반영: " + Path.GetFileName(drawn!) + " — Convert 하면 패턴이 만들어집니다");
            }
            catch (Exception ex) { StatusText = "실패: " + ex.Message; }
        }

        private void OpenBmp()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Open BMP",
                Filter = "Image (*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff)|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|All (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                // 미리보기는 래스터라이저가 범례 색으로 칠해서 준다 — 여기서 원본을 따로 읽으면
                // 열었을 때와 변환했을 때 같은 파일이 다른 색으로 보인다.
                // 그림을 직접 열었으면 도면 경로가 아니다 — 레이어가 남아 있으면 변환이 DXF 쪽으로 간다.
                _hasDxf = false;
                Layers.Clear();
                _lastResult = _rip.OpenBmp(dlg.FileName);
                ApplyResult("BMP 열기: " + Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex) { StatusText = "실패: " + ex.Message; }
        }

        /// <summary>
        /// 인쇄 데이터를 굳힌다 — 원본 저장 버튼과 같은 자리다.
        ///
        /// <para>변환만으로도 패턴 파일은 남지만, 인쇄기가 읽는 세 벌(.bmp · POS.dat ·
        /// Print_Para.dat)은 여기서 만들어진다. 그래서 설정을 바꿨으면 <b>다시 변환하고
        /// 다시 저장</b>해야 새 인쇄 데이터가 된다 — 저장만 눌러서는 옛 패턴이 그대로 나간다.</para>
        /// </summary>
        private void Save()
        {
            if (_lastResult == null) { StatusText = "저장할 것이 없습니다 — 먼저 Convert 하세요."; return; }

            try
            {
                var saved = _rip.Save(_lastResult);
                PatternPath = saved.Folder;

                string files = $"{Path.GetFileName(saved.BmpPath)}\n" +
                               $"{Path.GetFileName(saved.NozzlePosPath)}\n" +
                               $"{Path.GetFileName(saved.PrintParaPath)}";

                StatusText = $"저장 완료 — {saved.Steps}스텝 × {saved.Nozzles}노즐 · " +
                             files.Replace("\n", " + ");

                Notify?.Invoke("Save",
                    $"인쇄 데이터를 저장했습니다.\n\n" +
                    $"패턴  {saved.Steps}스텝 × {saved.Nozzles}노즐\n\n" +
                    $"저장 위치\n{saved.Folder}\n\n" +
                    $"파일\n{files}");
            }
            catch (Exception ex)
            {
                StatusText = "저장 실패: " + ex.Message;
                Notify?.Invoke("Save", "저장하지 못했습니다.\n\n" + ex.Message);
            }
        }

        private void ApplyResult(string msg)
        {
            if (_lastResult == null) return;
            LineCount = _lastResult.LineCount;
            RealXLengthMm = _lastResult.RealXLengthMm;
            RealYLengthMm = _lastResult.RealYLengthMm;
            BmpPath = _lastResult.BmpPath ?? BmpPath;
            PatternPath = _lastResult.PatternPath ?? PatternPath;
            if (_lastResult.PreviewImage != null) PreviewImage = _lastResult.PreviewImage;
            StatusText = msg;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
