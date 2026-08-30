using System;
using System.Globalization;
using System.IO;

namespace IJPSystem.Platform.Application.Printing
{
    /// <summary>3축 좌표. (LabVIEW: Print Origin.ctl = X/Y/Z Origin double 클러스터)</summary>
    public readonly struct AxisPoint : IEquatable<AxisPoint>
    {
        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        public AxisPoint(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static AxisPoint operator +(AxisPoint a, AxisPoint b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static AxisPoint operator -(AxisPoint a, AxisPoint b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public bool Equals(AxisPoint o) => X.Equals(o.X) && Y.Equals(o.Y) && Z.Equals(o.Z);
        public override bool Equals(object? o) => o is AxisPoint p && Equals(p);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public override string ToString() => $"X={X:F3}, Y={Y:F3}, Z={Z:F3}";
    }

    /// <summary>
    /// 현재 스테이지 위치 제공자. (LabVIEW: Motion VAL.lvlib\Motion_info 의 GetPos)
    /// HMI 가 라이브 축 위치를 이 인터페이스로 넘긴다 — 관리자는 모션 드라이버를 직접 모른다(HW 무관).
    /// </summary>
    public interface IStagePosition
    {
        AxisPoint GetCurrentPosition();
    }

    /// <summary>
    /// 인쇄 원점을 어디에 두는가.
    ///
    /// <para><b>주인은 레시피의 PRINT ORIGIN 티칭값이다.</b> 원점을 따로 파일에 두면 같은 값이
    /// 두 군데 생기고, 티칭 화면에서 PRINT ORIGIN 를 옮긴 날 둘이 갈라진다 — 화면에는 옛 원점이
    /// 뜨는데 인쇄는 새 자리에서 시작한다.</para>
    ///
    /// <para>쓰는 축은 <b>X·Y 뿐</b>이다. 인쇄 시작 위치에서 T 는 움직이지 않고, Z 는 헤드
    /// 높이라 원점이 아니다.</para>
    /// </summary>
    public interface IPrintOriginStore
    {
        /// <summary>지금 원점. 아직 없으면 false — 그때는 현재값을 그대로 둔다.</summary>
        bool TryRead(out AxisPoint origin);

        /// <summary>원점을 적는다. 실패하면 false 와 이유.</summary>
        bool Write(AxisPoint origin, out string message);
    }

    /// <summary>
    /// 인쇄 원점 관리자.
    /// (LabVIEW "21_Screen_Set Print Origin.vi" 의 상태 + 파일 영속화)
    ///
    /// "Set Print Origin 시 Glass View Camera 가 보고 있는 위치의 중앙이 인쇄 원점" —
    /// 즉 현재 스테이지 위치를 원점으로 캡처한다.
    ///
    /// Camera Offset(HeadOrigin = CameraAligned + Offset)은 이번 범위에서 제외.
    /// 첨부 코드에는 있으나 현 화면에 관련 UI/정렬 절차가 없어 넣지 않는다(향후 별도 추가).
    /// </summary>
    public sealed class PrintOriginManager
    {
        private readonly IStagePosition _stage;
        private readonly string? _dataDir;
        private readonly IPrintOriginStore? _store;

        /// <summary>현재 인쇄 원점.</summary>
        public AxisPoint PrintOrigin { get; private set; }

        /// <summary>기본 원점(Reset 대상). 보통 0 이나, 장비별 기준을 주입할 수 있다.</summary>
        public AxisPoint DefaultOrigin { get; }

        /// <summary>마지막 저장이 왜 실패했는지. 성공했으면 빈 문자열.</summary>
        public string LastError { get; private set; } = "";

        public event EventHandler? PrintOriginChanged;

        /// <param name="stage">현재 위치 제공자(GetPos).</param>
        /// <param name="dataDir">원점 저장 폴더. null 이면 저장 생략(메모리에만 유지).</param>
        /// <param name="defaultOrigin">Reset 시 되돌릴 기본 원점.</param>
        public PrintOriginManager(IStagePosition stage, string? dataDir = null, AxisPoint defaultOrigin = default)
        {
            _stage        = stage ?? throw new ArgumentNullException(nameof(stage));
            _dataDir      = dataDir;
            DefaultOrigin = defaultOrigin;
            PrintOrigin   = defaultOrigin;
        }

        /// <summary>
        /// 원점을 레시피의 PRINT ORIGIN 티칭값에 두는 구성.
        ///
        /// <para>파일 저장은 하지 않는다 — 같은 값을 두 군데 두면 언젠가 갈라진다.</para>
        /// </summary>
        public PrintOriginManager(IStagePosition stage, IPrintOriginStore store, AxisPoint defaultOrigin = default)
        {
            _stage        = stage ?? throw new ArgumentNullException(nameof(stage));
            _store        = store ?? throw new ArgumentNullException(nameof(store));
            DefaultOrigin = defaultOrigin;
            PrintOrigin   = defaultOrigin;
        }

        /// <summary>현재 스테이지 위치를 스냅으로 조회(모달의 Current Position 표시용).</summary>
        public AxisPoint GetCurrentPosition() => _stage.GetCurrentPosition();

        /// <summary>
        /// 현재 스테이지 위치를 인쇄 원점으로 확정. (Set Print Origin)
        ///
        /// <para><b>X·Y 만 잡는다.</b> 인쇄 시작 위치에서 T 는 움직이지 않고, Z 는 헤드 높이라
        /// 원점이 아니다 — 여기서 Z 까지 덮어쓰면 티칭해 둔 헤드 높이가 현재 위치로 밀린다.</para>
        /// </summary>
        public AxisPoint SetPrintOrigin()
        {
            var now = _stage.GetCurrentPosition();
            PrintOrigin = new AxisPoint(now.X, now.Y, PrintOrigin.Z);
            Save();
            PrintOriginChanged?.Invoke(this, EventArgs.Empty);
            return PrintOrigin;
        }

        /// <summary>인쇄 원점을 기본값으로 되돌림. (Reset Origin to Default)</summary>
        public void ResetToDefault()
        {
            PrintOrigin = DefaultOrigin;
            Save();
            PrintOriginChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>저장된 인쇄 원점 로드. 없으면 false(현재값 유지).</summary>
        public bool Load()
        {
            if (_store != null)
            {
                if (!_store.TryRead(out var fromStore)) return false;
                PrintOrigin = fromStore;
                PrintOriginChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }

            if (TryLoad(out var p)) { PrintOrigin = p; PrintOriginChanged?.Invoke(this, EventArgs.Empty); return true; }
            return false;
        }

        // ── 영속화 (Save file path and name.vi / Find most recent file.vi 대응) ──
        // 탭 구분 3값 텍스트. InvariantCulture 로 소수점 로케일 문제를 피한다.
        private const string FileName = "PrintOrigin.dat";

        private void Save()
        {
            LastError = "";

            if (_store != null)
            {
                if (!_store.Write(PrintOrigin, out string why)) LastError = why;
                return;
            }

            if (string.IsNullOrEmpty(_dataDir)) return;
            try
            {
                Directory.CreateDirectory(_dataDir);
                File.WriteAllText(Path.Combine(_dataDir, FileName),
                    string.Format(CultureInfo.InvariantCulture, "{0}\t{1}\t{2}",
                                  PrintOrigin.X, PrintOrigin.Y, PrintOrigin.Z));
            }
            catch (Exception ex) { LastError = ex.Message; }   // 메모리 값은 유효하므로 작업은 계속된다
        }

        private bool TryLoad(out AxisPoint p)
        {
            p = default;
            if (string.IsNullOrEmpty(_dataDir)) return false;
            string path = Path.Combine(_dataDir, FileName);
            if (!File.Exists(path)) return false;

            try
            {
                var parts = File.ReadAllText(path)
                                .Split(new[] { '\t', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) return false;
                p = new AxisPoint(
                    double.Parse(parts[0], CultureInfo.InvariantCulture),
                    double.Parse(parts[1], CultureInfo.InvariantCulture),
                    double.Parse(parts[2], CultureInfo.InvariantCulture));
                return true;
            }
            catch { return false; }   // 손상 파일 — 기본값으로 진행
        }
    }
}
