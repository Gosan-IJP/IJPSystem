using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace IJPSystem.Platform.Infrastructure.Print.Waveform
{
    /// <summary>
    /// 편집한 파형을 <c>.ComA</c> / <c>.ComB</c> 파일로 쓴다.
    ///
    /// <para>이 파일은 <b>PCC 가 그대로 읽는 파일</b>이다. 그래서 두 가지를 지킨다:</para>
    /// <list type="bullet">
    ///   <item>저장 직전에 다시 계산한다 — 화면 그래프와 파일 내용이 어긋나면 안 된다.</item>
    ///   <item>임시 파일에 쓰고 옮긴다 — 쓰다가 죽어도 반쯤 쓰인 파형이 남지 않는다.</item>
    /// </list>
    ///
    /// <para>세그먼트는 화면의 (기울기, 도달 전압)에서 파일의
    /// (시작 전압, 부호 있는 기울기, 끝 전압, 유지 시간)으로 되돌린다. 시작 전압은
    /// 직전 세그먼트에서 따라오고, 첫 세그먼트는 Vst 에서 시작한다.</para>
    /// </summary>
    public static class EpsonWaveformWriter
    {
        /// <summary>두 채널 파일을 쓴다. 쓴 파일 경로를 돌려준다.</summary>
        /// <param name="basePath">확장자를 뺀 경로. <c>{basePath}.ComA</c> 등으로 쓴다.</param>
        public static IReadOnlyList<string> Save(EpsonWaveformDocument doc, string basePath)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (string.IsNullOrWhiteSpace(basePath)) throw new ArgumentException("경로가 비었습니다.", nameof(basePath));
            if (doc.ComA.Pulses.Count == 0)
                throw new InvalidOperationException("ComA 에 펄스가 없습니다 — 토출이 일어나지 않는 파형은 저장하지 않습니다.");

            // 화면 값과 파일 값이 갈라지지 않게 저장 직전에 다시 푼다.
            EpsonWaveformCalculator.ResolveDocument(doc);

            var written = new List<string>();
            written.Add(WriteChannel(doc, ComChannelId.ComA, basePath + ".ComA"));

            // ComB 가 비어 있으면 파일을 만들지 않는다 — 빈 파일을 남기면 다음 로드에서
            // "ComB 가 있는 파형"으로 보인다.
            if (doc.ComB.Pulses.Count > 0)
                written.Add(WriteChannel(doc, ComChannelId.ComB, basePath + ".ComB"));
            else
                DeleteIfExists(basePath + ".ComB");

            return written;
        }

        private static string WriteChannel(EpsonWaveformDocument doc, ComChannelId id, string path)
        {
            string text = Build(doc, id);

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmp, path, overwrite: true);
            return path;
        }

        /// <summary>파일 본문을 만든다(테스트가 파일 없이 확인할 수 있게 분리).</summary>
        public static string Build(EpsonWaveformDocument doc, ComChannelId id)
        {
            var ch = doc.ChannelOf(id);
            var sb = new StringBuilder();

            sb.AppendLine("; -- IJPSystem 파형 편집기가 저장한 파일.");
            sb.AppendLine($"; -- \"{Name(id)}\" 채널. 세그먼트: 시작 전압, 기울기(V/us), 끝 전압, 유지 시간(us)");
            sb.AppendLine();

            sb.AppendLine("[generic]");
            sb.AppendLine(Kv("HeadType", $"\"{doc.HeadType}\""));
            sb.AppendLine(Kv("Version", doc.Version.ToString(CultureInfo.InvariantCulture)));
            sb.AppendLine(Kv("WaveformType", $"\"{Name(id).ToUpperInvariant()}\""));
            sb.AppendLine();

            for (int p = 0; p < ch.Pulses.Count; p++)
            {
                var pulse = ch.Pulses[p];
                int mask = GreyLevelMask(doc, id, p);

                sb.AppendLine($"[Pulse{p}]");
                sb.AppendLine(Kv("GLMask_A", Hex(mask)));
                sb.AppendLine(Kv("GLMask_B", Hex(mask)));
                sb.AppendLine(Kv("TempCompMask", Hex(pulse.TempCompMask)));

                double start = doc.Vst;
                for (int s = 0; s < pulse.Segments.Count; s++)
                {
                    var seg = pulse.Segments[s];
                    sb.AppendLine(Kv($"Seg{s}", SegmentLine(start, seg)));
                    start = seg.HoldVoltage;
                }
                sb.AppendLine();
            }

            var tc = doc.TempComp;
            sb.AppendLine("[TemperatureCompensation]");
            sb.AppendLine(Kv("Enabled", tc.Enabled ? "1" : "0"));
            sb.AppendLine(Kv("TCompLow", Num(tc.TCompLow)));
            sb.AppendLine(Kv("TCompHigh", Num(tc.TCompHigh)));
            sb.AppendLine(Kv("VCompStart", Num(tc.VCompStart)));
            sb.AppendLine(Kv("VCompEnd", Num(tc.VCompEnd)));
            sb.AppendLine(Kv("VTCoef", Num(tc.VTCoef)));

            return sb.ToString();
        }

        /// <summary>
        /// 한 세그먼트를 파일 한 줄로. 기울기 부호는 <b>전압이 오르는지 내리는지</b>가 정한다 —
        /// 화면은 크기만 다루므로 여기서 방향을 되살린다.
        /// </summary>
        private static string SegmentLine(double startVolts, EpsonWaveformSegment seg)
        {
            double delta = seg.HoldVoltage - startVolts;
            double slew  = Math.Abs(delta) < 1e-9 ? 0 : Math.Sign(delta) * Math.Abs(seg.Slew);

            return string.Join(",", Num(startVolts), Num(slew), Num(seg.HoldVoltage), Num(seg.HoldTimeUs));
        }

        /// <summary>
        /// 이 채널·이 펄스가 담당하는 그레이 레벨 비트(비트 g = GL g).
        /// 파일에는 노즐 행 A/B 두 벌이 있지만 화면 배정표는 행을 구분하지 않으므로 같은 값을 쓴다.
        /// </summary>
        private static int GreyLevelMask(EpsonWaveformDocument doc, ComChannelId id, int pulseIndex)
        {
            var want = id == ComChannelId.ComA ? GreyLevelAssign.ComA : GreyLevelAssign.ComB;

            int mask = 0;
            for (int g = 0; g < GreyLevelMatrix.Levels; g++)
                if (doc.GreyLevels[g, pulseIndex] == want) mask |= 1 << g;
            return mask;
        }

        private static string Name(ComChannelId id) => id == ComChannelId.ComA ? "ComA" : "ComB";

        private static string Kv(string key, string value) => $"{key,-24} = {value}";

        private static string Hex(int v) => "0x" + v.ToString("X", CultureInfo.InvariantCulture);

        private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
