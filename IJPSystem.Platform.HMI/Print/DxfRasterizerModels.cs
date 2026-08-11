using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// 변환 파라미터 (화면 우측 "Convert Parameters").
    /// </summary>
    public sealed class ConvertParameters
    {
        /// <summary>X 방향 해상도 [DPI] = Drop per Inch X.</summary>
        public double DropPerInchX { get; set; } = 600;

        /// <summary>Y 방향 해상도 [DPI] = Drop per Inch Y.</summary>
        public double DropPerInchY { get; set; } = 600;

        /// <summary>
        /// 방울을 놓는 간격의 <b>분할 수</b> (Interval Change). 1 = 노즐 실효 간격 그대로, 2 = ½.
        ///
        /// <para>
        /// 세로(스캔)는 엔코더를 잘게 쓰면 되므로 스캔 스텝이 그만큼 줄어든다 — 라인 수가 배로 는다.
        /// 가로(크로스스캔)는 노즐 피치가 하드웨어라 한 번에 못 좁힌다. 헤드를 피치의 1/N 만큼
        /// 옮겨 <b>N 번 지나간다</b>(멀티패스). 그래서 이 값은 곧 패스 수이기도 하다.
        /// </para>
        /// </summary>
        public int Interval { get; set; } = 1;

        /// <summary>사용 노즐 목록 (Nozzle Select 결과).</summary>
        public IReadOnlyList<int> UsingNozzles { get; set; } = new List<int>();

        /// <summary>
        /// 방울 크기 단계 수. 2 = 찍거나 안 찍거나(이진), 그 이상이면 그레이스케일 토출.
        /// 하프톤(오차확산)이 이 단계로 낮춘다.
        /// </summary>
        public int DropLevels { get; set; } = 2;

        /// <summary>
        /// 스캔 방향 한 스텝 이동량 [µm]. 0 이면 노즐 실효 간격과 같게 둬 정사각 격자를 만든다.
        /// </summary>
        public double ScanStepUm { get; set; }

        /// <summary>헤드 이음새 섞기. 헤드가 1개면 영향 없다.</summary>
        public bool BlendHeadSeams { get; set; } = true;
    }

    /// <summary>Layer Select 목록 항목. 체크 토글 가능.</summary>
    public sealed class LayerItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";

        private bool _isSelected = true;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>래스터화(Convert) 결과.</summary>
    public sealed class RasterizeResult
    {
        /// <summary>생성된 인쇄 라인 수 (Line count).</summary>
        public int LineCount { get; set; }

        /// <summary>실제 인쇄물 가로 길이 [mm] (Real X Length).</summary>
        public double RealXLengthMm { get; set; }

        /// <summary>실제 인쇄물 세로 길이 [mm] (Real Y Length).</summary>
        public double RealYLengthMm { get; set; }

        /// <summary>결과 BMP 파일 경로.</summary>
        public string? BmpPath { get; set; }

        /// <summary>생성된 토출 패턴 폴더 경로.</summary>
        public string? PatternPath { get; set; }

        /// <summary>미리보기용 이미지(WPF BitmapSource 등). 구현체에서 채움.</summary>
        public object? PreviewImage { get; set; }
    }
}
