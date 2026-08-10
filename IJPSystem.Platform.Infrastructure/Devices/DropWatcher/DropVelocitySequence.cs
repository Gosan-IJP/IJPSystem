using IJPSystem.Platform.Domain.Models.Vision;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    /// <summary>노즐 1개의 2점 측정 결과.</summary>
    public sealed class NozzleVelocity
    {
        /// <summary>노즐 인덱스(프레임 좌→우 순서).</summary>
        public int Index { get; set; }
        /// <summary>두 프레임 액적 중심의 평균 X[px] — 오버레이/차트 정렬용.</summary>
        public double CentroidXPixel { get; set; }
        /// <summary>Time1 → Time2 사이 낙하 거리[µm].</summary>
        public double FallDistanceUm { get; set; }
        /// <summary>속도[m/s] (= µm/µs).</summary>
        public double VelocityMps { get; set; }
        /// <summary>두 시점 등가원 직경 평균[µm].</summary>
        public double DiameterUm { get; set; }
        /// <summary>구형 가정 부피[pL].</summary>
        public double VolumePl { get; set; }
    }

    /// <summary>2점 지연 측정 전체 결과.</summary>
    public sealed class DropVelocityResult
    {
        public bool   Success { get; set; }
        public string Message { get; set; } = "";

        public double Time1Us { get; set; }
        public double Time2Us { get; set; }

        /// <summary>노즐별 결과(좌→우). 짝이 맞은 노즐만 포함된다.</summary>
        public IReadOnlyList<NozzleVelocity> Nozzles { get; set; } = Array.Empty<NozzleVelocity>();

        /// <summary>Time1 / Time2 프레임에서 검출된 액적 수(짝짓기 전).</summary>
        public int DetectedAt1 { get; set; }
        public int DetectedAt2 { get; set; }

        /// <summary>Time2 시점 프레임과 그 액적들 — 화면 오버레이 표시용(실패해도 null 아닐 수 있음).</summary>
        public VisionImage? Frame2 { get; set; }
        public IReadOnlyList<DropletInfo> DropsAt2 { get; set; } = Array.Empty<DropletInfo>();

        /// <summary>두 프레임의 품질 측정 결과.</summary>
        public FrameQualityResult? Quality1 { get; set; }
        public FrameQualityResult? Quality2 { get; set; }

        /// <summary>
        /// 측정값을 신뢰하기 어렵게 만드는 사항(품질 저하, 두 프레임 검출 수 불일치 등).
        /// 측정 자체는 성공해도 여기 내용이 있으면 결과를 참고값으로 봐야 한다.
        /// </summary>
        public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();

        /// <summary>노즐 번호 매핑 결과(기대 노즐 목록이 주어졌을 때만). 불토출 노즐이 여기 담긴다.</summary>
        public NozzleGridResult? Grid { get; set; }

        // ── 대표값(노즐 평균) ──
        public double VelocityMps { get; set; }
        public double DiameterUm  { get; set; }
        public double VolumePl    { get; set; }
        public double DistanceUm  { get; set; }

        /// <summary>노즐 간 속도 편차(최대−최소)[m/s]. 토출 균일도 판정용.</summary>
        public double VelocitySpreadMps { get; set; }

        /// <summary>목표 속도대(기본 4~6 m/s) 안인지.</summary>
        public bool InTargetRange(double lo = 4.0, double hi = 6.0)
            => VelocityMps >= lo && VelocityMps <= hi;
    }

    /// <summary>
    /// 2점 지연 방식 액적 속도·체적 측정 시퀀스.
    /// (LabVIEW "5_WIZ_Set Nozzle and WF with DW.vi" — Time1 → Time2 → Measure → CAL Volume)
    ///
    ///   ① 스트로브 Delay = Time1 → 안정 대기 → Grab → 액적 검출
    ///   ② 스트로브 Delay = Time2 → 안정 대기 → Grab → 액적 검출
    ///   ③ 같은 노즐끼리 짝지어 ΔY 산출 → v = 낙하거리 / (Time2 − Time1)  [µm/µs = m/s]
    ///   ④ 등가원 직경 → 구형 부피(pL)
    ///
    /// <b>단일 프레임 측정(Measure Velocity)과의 차이</b>: 단일 프레임은 노즐면(NozzleYPixel)을
    /// 기준으로 낙하거리를 재므로 노즐면 Y 교정값이 틀리면 속도가 통째로 틀어진다. 2점 측정은
    /// 두 시점의 <b>차이</b>만 쓰므로 노즐면 좌표에 의존하지 않는다 — 절대 기준이 없어도 옳다.
    /// 대신 µm/px 스케일과 지연 스케일(RegisterScale)에는 그대로 비례한다.
    ///
    /// 촬영은 공용 IVisionDriver 를 주입받은 델리게이트로 받아 벤더 SDK 와 분리한다.
    /// </summary>
    public sealed class DropVelocitySequence
    {
        private readonly IStrobeController _strobe;
        private readonly Func<CancellationToken, Task<VisionImage>> _grabFrame;
        private readonly DropWatcherProcessor _proc;
        private readonly DropWatcherProcessorConfig _cfg;

        /// <summary>지연 변경 후 발광이 안정될 때까지 대기[ms].</summary>
        public int SettleMs { get; set; } = 50;

        /// <summary>
        /// 두 프레임에서 같은 노즐로 볼 X 허용 오차[px].
        /// 노즐 피치의 절반보다 작아야 옆 노즐과 잘못 짝지어지지 않는다. 0 이하면 자동(피치의 40%).
        /// </summary>
        public double PairToleranceXPixel { get; set; } = 0;

        /// <summary>
        /// 토출을 지시한 노즐 번호 목록. 설정하면 결과에 <see cref="DropVelocityResult.Grid"/> 가 채워져
        /// 액적이 실제 노즐 번호로 매핑되고 불토출 노즐이 식별된다. 비어 있으면 매핑을 건너뛴다.
        /// </summary>
        public IReadOnlyList<int> ExpectedNozzles { get; set; } = Array.Empty<int>();

        /// <summary>노즐 피치[µm] — 격자 매핑에 필요. 0 이면 매핑을 건너뛴다.</summary>
        public double NozzlePitchUm { get; set; } = 0;

        /// <param name="strobe">스트로브 지연 컨트롤러(iCore Modbus 또는 가상).</param>
        /// <param name="grabFrame">한 프레임 취득 델리게이트(예: ct => vision.CaptureAsync("CAM_DW")).</param>
        /// <param name="processor">액적 검출기.</param>
        /// <param name="config">µm/px 등 계측 파라미터(processor 와 같은 인스턴스여야 한다).</param>
        public DropVelocitySequence(IStrobeController strobe,
                                    Func<CancellationToken, Task<VisionImage>> grabFrame,
                                    DropWatcherProcessor processor,
                                    DropWatcherProcessorConfig config)
        {
            _strobe    = strobe    ?? throw new ArgumentNullException(nameof(strobe));
            _grabFrame = grabFrame ?? throw new ArgumentNullException(nameof(grabFrame));
            _proc      = processor ?? throw new ArgumentNullException(nameof(processor));
            _cfg       = config    ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// Time1, Time2 두 지연에서 촬영해 노즐별 속도·직경·부피를 산출한다.
        /// 예외를 던지지 않고 <see cref="DropVelocityResult.Success"/>/Message 로 실패를 알린다
        /// (측정 실패는 화면에 사유를 보여줘야 하는 정상 흐름이라 예외로 올리지 않는다).
        /// </summary>
        public async Task<DropVelocityResult> MeasureVelocityAsync(
            double time1Us, double time2Us, CancellationToken ct = default)
        {
            double dtUs = Math.Abs(time2Us - time1Us);
            if (dtUs < 1e-6)
                return Fail("Delay 1 과 Delay 2 가 같습니다. 서로 다른 지연을 설정하세요.", time1Us, time2Us);
            if (_cfg.MicronsPerPixel <= 0)
                return Fail("µm/px 스케일이 설정되지 않았습니다. 캘리브레이션을 먼저 하세요.", time1Us, time2Us);

            VisionImage f1, f2;
            IReadOnlyList<DropletInfo> d1, d2;
            FrameQualityResult q1, q2;
            try
            {
                (f1, d1, q1) = await CaptureDropsAsync(time1Us, ct).ConfigureAwait(false);
                (f2, d2, q2) = await CaptureDropsAsync(time2Us, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Fail($"촬영/지연 설정 실패: {ex.Message}", time1Us, time2Us);
            }
            _ = f1;   // Time1 프레임은 판정에 쓰지 않는다(표시는 나중 시점인 Time2 가 직관적).

            if (d1.Count == 0) return Fail($"Delay 1({time1Us:F1}us) 프레임에서 액적을 검출하지 못했습니다.", time1Us, time2Us);
            if (d2.Count == 0) return Fail($"Delay 2({time2Us:F1}us) 프레임에서 액적을 검출하지 못했습니다.", time1Us, time2Us);

            var nozzles = PairByX(d1, d2, time2Us - time1Us);
            if (nozzles.Count == 0)
                return Fail("두 프레임의 액적을 같은 노즐로 짝지을 수 없습니다(X 편차 과다). 토출 상태를 확인하세요.",
                            time1Us, time2Us);

            // ── 신뢰도 경고 수집 — 측정은 성공해도 값을 곧이 믿으면 안 되는 경우들 ──
            var warnings = new List<string>();
            if (!string.IsNullOrEmpty(q1.Summary)) warnings.Add($"Delay1 프레임: {q1.Summary}");
            if (!string.IsNullOrEmpty(q2.Summary)) warnings.Add($"Delay2 프레임: {q2.Summary}");

            // 두 프레임의 검출 수가 다르면 그 사이에 촬영 조건이 흔들린 것이다.
            // (이 값들은 원래도 담고 있었지만 아무도 비교하지 않아 그냥 지나쳤다)
            if (d1.Count != d2.Count)
                warnings.Add($"두 프레임 검출 수 불일치({d1.Count} vs {d2.Count}) — 짝지어진 {nozzles.Count}개만 반영");

            // 측정창에 걸린 액적 — 면적이 잘려 직경·부피가 작게 나온다. 속도(ΔY)는 덜 영향받지만
            // 같은 프레임의 부피를 함께 읽으므로 두 값을 나란히 보는 사람이 오해한다.
            if (DropWatcherProcessor.ClippedWarning(d1) is string c1) warnings.Add($"Delay1 프레임: {c1}");
            if (DropWatcherProcessor.ClippedWarning(d2) is string c2) warnings.Add($"Delay2 프레임: {c2}");

            // ── 노즐 번호 매핑 — 리스트 순번이 아니라 실제 노즐 번호를 배정 ──
            NozzleGridResult? grid = null;
            if (ExpectedNozzles.Count > 0 && NozzlePitchUm > 0)
            {
                grid = NozzleGrid.Map(d2, ExpectedNozzles, NozzlePitchUm, _cfg.MicronsPerPixel);
                if (grid.MissingNozzles.Count > 0)
                    warnings.Add(grid.AbsoluteMappingConfident
                        ? $"불토출 노즐 {grid.MissingNozzles.Count}개: {string.Join(",", grid.MissingNozzles)}"
                        : $"불토출 {grid.MissingNozzles.Count}개 추정(번호는 참고값)");
                if (!string.IsNullOrEmpty(grid.Ambiguity))
                    warnings.Add(grid.Ambiguity!);
            }

            var vel = nozzles.Select(v => v.VelocityMps).ToArray();
            return new DropVelocityResult
            {
                Quality1 = q1,
                Quality2 = q2,
                Warnings = warnings,
                Grid     = grid,
                Success           = true,
                Message           = "OK",
                Time1Us           = time1Us,
                Time2Us           = time2Us,
                Nozzles           = nozzles,
                DetectedAt1       = d1.Count,
                DetectedAt2       = d2.Count,
                Frame2            = f2,
                DropsAt2          = d2,
                VelocityMps       = vel.Average(),
                VelocitySpreadMps = vel.Max() - vel.Min(),
                DistanceUm        = nozzles.Average(v => v.FallDistanceUm),
                DiameterUm        = nozzles.Average(v => v.DiameterUm),
                VolumePl          = nozzles.Average(v => v.VolumePl),
            };
        }

        /// <summary>지연 설정 → 발광 안정 대기 → 촬영 → 품질 측정 + 액적 검출.</summary>
        private async Task<(VisionImage frame, IReadOnlyList<DropletInfo> drops, FrameQualityResult quality)>
            CaptureDropsAsync(double delayUs, CancellationToken ct)
        {
            _strobe.SetDelayMicroseconds(delayUs);
            if (SettleMs > 0) await Task.Delay(SettleMs, ct).ConfigureAwait(false);

            var frame = await _grabFrame(ct).ConfigureAwait(false);
            // 검출·품질측정 모두 CPU 부하가 있어 UI 스레드에서 벗어난다(HMI 가 이 시퀀스를 직접 await 한다).
            return await Task.Run(() =>
            {
                var quality = _proc.AnalyzeQuality(frame);
                var drops   = _proc.DetectDroplets(frame);
                return (frame, (IReadOnlyList<DropletInfo>)drops, quality);
            }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 두 프레임의 액적을 X 좌표로 짝지어 노즐별 결과를 만든다.
        /// 실측 DW 프레임은 노즐이 가로로 늘어서므로 X 는 노즐 식별자이고(지연이 바뀌어도 거의 불변),
        /// Y 만 낙하로 변한다. 그래서 X 최근접 매칭이 곧 같은 노즐 매칭이다.
        /// </summary>
        /// <param name="dtSignedUs">Time2 − Time1 (부호 유지). Time1 &gt; Time2 로 넣어도 속도 부호가 뒤집히지 않는다.</param>
        private List<NozzleVelocity> PairByX(IReadOnlyList<DropletInfo> d1, IReadOnlyList<DropletInfo> d2, double dtSignedUs)
        {
            double tol = PairToleranceXPixel > 0 ? PairToleranceXPixel : AutoTolerance(d1);
            double umPerPx = _cfg.MicronsPerPixel;

            var used = new bool[d2.Count];
            var list = new List<NozzleVelocity>(d1.Count);

            for (int i = 0; i < d1.Count; i++)
            {
                int best = -1; double bestDx = double.MaxValue;
                for (int j = 0; j < d2.Count; j++)
                {
                    if (used[j]) continue;
                    double dx = Math.Abs(d2[j].CentroidXPixel - d1[i].CentroidXPixel);
                    if (dx < bestDx) { bestDx = dx; best = j; }
                }
                if (best < 0 || bestDx > tol) continue;   // 짝 없음 → 이 노즐은 제외(불토출 등)
                used[best] = true;

                var a = d1[i];
                var b = d2[best];
                // ΔY 와 Δt 의 부호를 함께 쓰면 Time1/Time2 순서에 무관하게 낙하 속도가 양수로 나온다.
                double dyUm = (b.CentroidYPixel - a.CentroidYPixel) * umPerPx;
                double vMps = dyUm / dtSignedUs;             // µm/µs == m/s
                double diaUm = 0.5 * (a.DiameterMicron + b.DiameterMicron);
                double rUm   = diaUm / 2.0;

                list.Add(new NozzleVelocity
                {
                    Index          = list.Count,
                    CentroidXPixel = 0.5 * (a.CentroidXPixel + b.CentroidXPixel),
                    FallDistanceUm = Math.Abs(dyUm),
                    VelocityMps    = vMps,
                    DiameterUm     = diaUm,
                    VolumePl       = 4.0 / 3.0 * Math.PI * rUm * rUm * rUm * 1e-3,
                });
            }
            return list;
        }

        /// <summary>인접 액적 X 간격(=노즐 피치)의 40% 를 매칭 허용오차로 쓴다. 액적 1개면 넉넉히 허용.</summary>
        private static double AutoTolerance(IReadOnlyList<DropletInfo> drops)
        {
            if (drops.Count < 2) return double.MaxValue;
            double sum = 0;
            for (int i = 1; i < drops.Count; i++)
                sum += drops[i].CentroidXPixel - drops[i - 1].CentroidXPixel;
            return Math.Abs(sum / (drops.Count - 1)) * 0.4;
        }

        private static DropVelocityResult Fail(string msg, double t1, double t2)
            => new DropVelocityResult { Success = false, Message = msg, Time1Us = t1, Time2Us = t2 };
    }
}
