using IJPSystem.Platform.Domain.Models.Vision;

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    /// <summary>
    /// ④ 측정: 액적 부피/직경/속도. (Make Clear Mask → Process and Result → CAL Volume)
    /// 촬영은 공용 <see cref="IJPSystem.Platform.Domain.Interfaces.IVisionDriver"/> 가 담당하고,
    /// 측정기는 그 결과 프레임(VisionImage)만 받아 처리한다.
    /// (드랍와쳐 전용 카메라 인터페이스는 제거 — Drivers.Vision 과 역할 중복이라 통일)
    /// </summary>
    public interface IDwMeasurer
    {
        DwReading Measure(VisionImage frame);
    }

    /// <summary>측정 결과.</summary>
    public sealed class DwReading
    {
        public double VolumePicoLiter { get; set; }
        public double DiameterMicron { get; set; }
        public double VelocityMetersPerSecond { get; set; } = double.NaN;
        public double CentroidYPixel { get; set; }     // 위상 스윕의 궤적 계산용
        public int ParticleCount { get; set; }
        public bool IsValid => ParticleCount > 0 && VolumePicoLiter > 0;
    }

    /// <summary>측정 스텁 — 실제 알고리즘(DropWatcherProcessor) 연결 전까지 빈 결과.</summary>
    public sealed class DwMeasurerStub : IDwMeasurer
    {
        // TODO: DW Vision 의 액적 검출(blob) → 부피/직경/속도 산출 연결.
        public DwReading Measure(VisionImage frame) => new DwReading();
    }
}
