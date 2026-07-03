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

        /// <summary>토출/토 간격 변경값 (Interval Change). 헤드 사양에 맞게 정의.</summary>
        public int Interval { get; set; } = 1;

        /// <summary>사용 노즐 목록 (Nozzle Select 결과).</summary>
        public IReadOnlyList<int> UsingNozzles { get; set; } = new List<int>();
    }

    /// <summary>Using Nozzles 표시용 셀 하나(노즐 1개). Row 스트립에서 사용 여부를 색으로 표시.</summary>
    public sealed class NozzleCell : INotifyPropertyChanged
    {
        /// <summary>노즐 번호(1부터).</summary>
        public int Index { get; set; }

        private bool _isUsed;
        /// <summary>사용 노즐이면 true(초록 표시).</summary>
        public bool IsUsed
        {
            get => _isUsed;
            set { _isUsed = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
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
