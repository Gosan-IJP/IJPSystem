using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media.Imaging;
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
        public Action<double, double>? OpenEditPanel { get; set; }

        public DxfRasterizerViewModel(IDxfRasterizer rasterizer)
        {
            _rip = rasterizer ?? throw new ArgumentNullException(nameof(rasterizer));

            LoadDxfCommand          = new RelayCommand(_ => LoadDxf());
            IntervalChangeCommand   = new RelayCommand(_ => IntervalChange());
            NozzleSelectCommand     = new RelayCommand(_ => NozzleSelect());
            ConvertCommand          = new RelayCommand(_ => Convert(), _ => Layers.Any(l => l.IsSelected));
            CreateEmptyLayerCommand = new RelayCommand(_ => CreateEmptyLayer());
            OpenBmpCommand          = new RelayCommand(_ => OpenBmp());
            SaveCommand             = new RelayCommand(_ => Save(), _ => _lastResult != null);
            ZoomToFitCommand        = new RelayCommand(_ => ZoomToFitRequested?.Invoke(this, EventArgs.Empty));
            ToggleGridCommand       = new RelayCommand(_ => ShowGrid = !ShowGrid);
        }

        // ---- Convert Parameters ----
        private double _dpiX = 600, _dpiY = 600;
        public double DropPerInchX { get => _dpiX; set { _dpiX = value; OnPropertyChanged(); } }
        public double DropPerInchY { get => _dpiY; set { _dpiY = value; OnPropertyChanged(); } }

        private int _interval = 1;
        public int Interval { get => _interval; set { _interval = value; OnPropertyChanged(); } }

        // ---- 읽기 표시값 ----
        private int _lineCount;
        public int LineCount { get => _lineCount; set { _lineCount = value; OnPropertyChanged(); } }

        private double _realX, _realY;
        public double RealXLengthMm { get => _realX; set { _realX = value; OnPropertyChanged(); } }
        public double RealYLengthMm { get => _realY; set { _realY = value; OnPropertyChanged(); } }

        // ---- Using Nozzles / Row 표시 ----
        private int _usingNozzleCount;
        public int UsingNozzleCount { get => _usingNozzleCount; set { _usingNozzleCount = value; OnPropertyChanged(); } }
        private IReadOnlyList<int> _usingNozzles = new List<int>();

        /// <summary>외부(전역 선택)에서 사용 노즐을 초기화한다(창 열 때).</summary>
        public void InitUsingNozzles(IReadOnlyList<int> nozzles)
        {
            _usingNozzles = nozzles ?? new List<int>();
            UsingNozzleCount = _usingNozzles.Count;
        }

        // ---- Length Measure ----
        private bool _lengthMeasure = true;
        public bool LengthMeasureEnabled { get => _lengthMeasure; set { _lengthMeasure = value; OnPropertyChanged(); } }
        private double _measuredLength = 53.207;
        public double MeasuredLengthMm { get => _measuredLength; set { _measuredLength = value; OnPropertyChanged(); } }

        // ---- 미리보기 ----
        private bool _showGrid;
        public bool ShowGrid { get => _showGrid; set { _showGrid = value; OnPropertyChanged(); } }
        private object? _previewImage;
        public object? PreviewImage { get => _previewImage; set { _previewImage = value; OnPropertyChanged(); } }

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

            DxfPath = dlg.FileName;
            Layers.Clear();
            foreach (string name in _rip.LoadDxf(DxfPath))
                Layers.Add(new LayerItem { Name = name, IsSelected = true });
            LoadingProgress = 1.0;
            StatusText = $"DXF 로드: 레이어 {Layers.Count}개";
        }

        private void IntervalChange()
        {
            // 토출/토 간격 토글 (예: 1↔2). 실제 규칙에 맞게 구현.
            Interval = Interval == 1 ? 2 : 1;
            StatusText = $"Interval = {Interval}";
        }

        private void NozzleSelect()
        {
            _usingNozzles = NozzleSelectAction?.Invoke() ?? _usingNozzles;
            UsingNozzleCount = _usingNozzles.Count;
            StatusText = $"사용 노즐 {UsingNozzleCount}개";
        }

        private ConvertParameters BuildParams() => new ConvertParameters
        {
            DropPerInchX = DropPerInchX,
            DropPerInchY = DropPerInchY,
            Interval = Interval,
            UsingNozzles = _usingNozzles
        };

        private void Convert()
        {
            try
            {
                var progress = new Progress<double>(p => LoadingProgress = p);
                var selected = Layers.Where(l => l.IsSelected).Select(l => l.Name).ToList();
                _lastResult = _rip.Convert(selected, BuildParams(), progress);
                ApplyResult("변환 완료");
            }
            catch (Exception ex) { StatusText = "변환 실패: " + ex.Message; }
        }

        private void CreateEmptyLayer()
        {
            // 1) 캔버스 크기 입력 (Set size of canvas). 취소 시 중단.
            var size = RequestCanvasSize?.Invoke();
            if (size == null) return;
            var (w, l) = size.Value;

            try
            {
                // 2) 빈 레이어 결과 생성 + Layer Select 목록에 등록
                _lastResult = _rip.CreateEmptyLayer(BuildParams());
                Layers.Add(new LayerItem { Name = $"Empty ({w:0.#}×{l:0.#}mm)", IsSelected = true });
                ApplyResult($"빈 레이어 생성 ({w:0.#}×{l:0.#}mm)");

                // 3) Edit Panel(Drawing Panel) 편집 창 열기
                OpenEditPanel?.Invoke(w, l);
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
                _lastResult = _rip.OpenBmp(dlg.FileName);
                PreviewImage = LoadBitmap(dlg.FileName);   // Image Preview 갱신
                ApplyResult("BMP 열기: " + Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex) { StatusText = "실패: " + ex.Message; }
        }

        /// <summary>이미지 파일을 잠금 없이 로드(OnLoad+Freeze)하여 미리보기 소스로 반환.</summary>
        private static BitmapImage LoadBitmap(string path)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        private void Save()
        {
            try { _rip.Save(_lastResult!); StatusText = "저장 완료"; }
            catch (Exception ex) { StatusText = "저장 실패: " + ex.Message; }
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
