using IJPSystem.Platform.Common.Utilities;
using System;
using System.IO;
using System.Text.Json;

namespace IJPSystem.Platform.Infrastructure.Vision
{
    /// <summary>
    /// µm/px 교정값 보관 — <c>Config\AlignCalibration.json</c>.
    ///
    /// <para><b>레시피가 아니라 장비 설정이다.</b> 카메라와 렌즈가 그대로면 글라스 품종이 바뀌어도
    /// 배율은 그대로다. 반대로 렌즈를 만지거나 카메라를 다시 달면 모든 레시피에서 다시 재야 한다.</para>
    ///
    /// <para>파일이 없으면 <b>교정 전</b>이다 — 정렬은 그 상태에서 아무것도 움직이면 안 된다.
    /// 사람이 열어 볼 수 있게 JSON 한 장으로 두고, 잰 날짜와 사양 대비 오차도 같이 적는다.</para>
    /// </summary>
    public sealed class AlignCalibration
    {
        /// <summary>픽셀 → 기계 mm 변환(2×2). 네 값이 모두 0 이면 교정 전이다.</summary>
        public double Kxu { get; set; }
        public double Kxv { get; set; }
        public double Kyu { get; set; }
        public double Kyv { get; set; }

        /// <summary>잰 날짜. 렌즈를 만진 날과 대조하려고 남긴다.</summary>
        public DateTime MeasuredAt { get; set; }

        /// <summary>잴 때 쓴 이동 거리[mm]. 다시 잴 때 같은 조건인지 보려고 남긴다.</summary>
        public double MoveXMm { get; set; }
        public double MoveYMm { get; set; }

        /// <summary>사람이 읽는 기록 — 다시 계산되는 값이라 참고용이다.</summary>
        public string Note { get; set; } = "";

        public PixelToStage ToMatrix() => new() { Kxu = Kxu, Kxv = Kxv, Kyu = Kyu, Kyv = Kyv };

        public static AlignCalibration From(PixelToStage k, double moveXMm, double moveYMm, DateTime measuredAt)
            => new()
            {
                Kxu = k.Kxu, Kxv = k.Kxv, Kyu = k.Kyu, Kyv = k.Kyv,
                MeasuredAt = measuredAt,
                MoveXMm = moveXMm,
                MoveYMm = moveYMm,
                Note = $"{k.MicronPerPxX:F3} / {k.MicronPerPxY:F3} µm/px · 카메라 {k.CameraAngleDeg:+0.00;-0.00}°",
            };
    }

    /// <summary>교정값 읽기·쓰기.</summary>
    public static class AlignCalibrationStore
    {
        public const string FileName = "AlignCalibration.json";

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public static string DefaultPath => PathUtils.GetConfigPath(FileName);

        /// <summary>없거나 깨졌으면 null — 교정 전으로 본다. 여기서 예외를 던지면 화면이 안 뜬다.</summary>
        public static AlignCalibration? Load(string? path = null)
        {
            try
            {
                string p = path ?? DefaultPath;
                if (!File.Exists(p)) return null;

                var cal = JsonSerializer.Deserialize<AlignCalibration>(File.ReadAllText(p));
                return cal != null && cal.ToMatrix().IsCalibrated ? cal : null;
            }
            catch { return null; }
        }

        /// <summary>임시 파일에 쓰고 바꿔치기한다 — 쓰다 죽어도 옛 교정이 남는다.</summary>
        public static void Save(AlignCalibration cal, string? path = null)
        {
            if (cal == null) throw new ArgumentNullException(nameof(cal));

            string p = path ?? DefaultPath;
            string? dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cal, Json));
            File.Move(tmp, p, overwrite: true);
        }
    }
}
