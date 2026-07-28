using System;
using System.Collections.Generic;
using System.Windows.Media;
using IJPSystem.Platform.HMI.Models;

namespace IJPSystem.Platform.HMI.Common
{
    public class WaveformSeries
    {
        public string Name      { get; set; } = "";
        public string FilePath  { get; set; } = "";
        public bool   IsVisible { get; set; } = true;
        public Brush  Stroke    { get; set; } = Brushes.White;
        public DoubleCollection? DashArray       { get; set; }
        public double StrokeThickness { get; set; } = 1.5;

        public List<(double T, double V)> Points { get; set; } = new();

        public void LoadFromFile(WaveformFile file, int repeats = 3)
        {
            Points = Compute(file, repeats);
            FilePath = file.FilePath;
        }

        public void SetFlat(double voltage, double totalTime)
        {
            Points = new List<(double, double)> { (0, voltage), (totalTime, voltage) };
        }

        private static List<(double T, double V)> Compute(WaveformFile file, int repeats)
        {
            var pts = new List<(double, double)>();
            if (file.Pulses.Count == 0) return pts;

            double t = 0;

            // MetWaveEpson 과 동일하게: 파일 내 모든 펄스([Pulse0],[Pulse1] …)를
            // 정의된 순서대로 시간축에 이어 붙인다. (Meteor 파형은 다중 펄스가
            // 연속되는 하나의 구동 파형 — Pulse0 만 그리면 실제와 달라 보인다.)
            // repeats 는 "전체 펄스 시퀀스"를 몇 번 반복할지.
            for (int r = 0; r < repeats; r++)
            {
                foreach (var pulse in file.Pulses)
                {
                    foreach (var seg in pulse.Segments)
                    {
                        // 이전 끝 전압과 현재 시작 전압이 다르면 수직 점프 추가
                        // (펄스 경계에서도 동일하게 적용 — 보통 같은 전압이라 점프 없음)
                        if (pts.Count > 0 && Math.Abs(pts[^1].Item2 - seg.StartVoltage) > 0.001)
                            pts.Add((t, seg.StartVoltage));
                        else if (pts.Count == 0)
                            pts.Add((t, seg.StartVoltage));

                        // Slew 구간
                        if (Math.Abs(seg.SlewRate) > 0.001)
                        {
                            double slewTime = Math.Abs(seg.EndVoltage - seg.StartVoltage) / Math.Abs(seg.SlewRate);
                            t += slewTime;
                        }
                        pts.Add((t, seg.EndVoltage));

                        // Hold 구간
                        if (seg.HoldTime > 0)
                        {
                            t += seg.HoldTime;
                            pts.Add((t, seg.EndVoltage));
                        }
                    }
                }
            }

            return pts;
        }
    }
}
