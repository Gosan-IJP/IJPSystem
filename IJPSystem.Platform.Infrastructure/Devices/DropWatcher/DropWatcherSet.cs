using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Vision;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    /// <summary>
    /// 드랍와쳐 세트 오케스트레이터.
    /// 헤드 토출 → NI-DAQ 펄스 분주(트리거) → iCore 지연·발광 → 카메라 촬영 → 측정.
    /// 촬영은 공용 IVisionDriver 를 주입받아 사용한다(앱이 소유·연결하는 카메라를 공유).
    /// </summary>
    public sealed class DropWatcherSet : IDisposable
    {
        private readonly ISpit _spit;
        private readonly IPulseReducer _reducer;     // ① 트리거
        private readonly IStrobeController _strobe;  // ② 조명
        private readonly IVisionDriver _camera;      // ③ 촬영(공용 카메라 드라이버)
        private readonly IDwMeasurer _measurer;      // ④ 측정
        private readonly string _cameraId;

        public DropWatcherSet(ISpit spit, IPulseReducer reducer, IStrobeController strobe,
            IVisionDriver camera, IDwMeasurer measurer, string cameraId = "CAM_DW")
        {
            _spit = spit; _reducer = reducer; _strobe = strobe;
            _camera = camera; _measurer = measurer; _cameraId = cameraId;
        }

        /// <summary>세트 기동: 스트로브 초기화 + 토출 + 분주 트리거 시작.
        /// (카메라는 공용 IVisionDriver 로 앱 시작 시 이미 연결됨 — 여기선 다루지 않음)</summary>
        public void Start(IReadOnlyList<int> nozzles, double spitFreqHz, int divider)
        {
            _strobe.Init();
            _strobe.Enable(true);

            _reducer.Divider = divider;
            _reducer.Open();

            _spit.Start(nozzles, spitFreqHz);
            _reducer.Start();
        }

        /// <summary>지정 지연에서 1회 측정: 지연 적용 → 발광 안정 대기 → 촬영 → 측정.</summary>
        public async Task<DwReading> MeasureAtAsync(double delayMicroseconds)
        {
            _strobe.SetDelayMicroseconds(delayMicroseconds);
            await Task.Delay(30);                          // 발광 안정 대기
            VisionImage frame = await _camera.CaptureAsync(_cameraId);
            return _measurer.Measure(frame);
        }

        /// <summary>위상 스윕: 지연을 start~end 로 훑으며 측정 → 액적 궤적/속도 산출용 데이터.</summary>
        public async Task<IReadOnlyList<(double delayUs, DwReading reading)>> PhaseSweepAsync(
            double startUs, double endUs, double stepUs)
        {
            var list = new List<(double, DwReading)>();
            for (double d = startUs; d <= endUs; d += stepUs)
                list.Add((d, await MeasureAtAsync(d)));
            return list;
        }

        /// <summary>세트 정지: 트리거·토출·조명 순차 정지. (카메라는 공용 자원이라 닫지 않음)</summary>
        public void Stop()
        {
            try { _reducer.Stop(); } catch { /* 정지 오류 무시 */ }
            try { _spit.Stop(); } catch { }
            try { _strobe.Enable(false); } catch { }
        }

        public void Dispose()
        {
            Stop();
            _reducer?.Dispose(); _strobe?.Dispose();
        }
    }
}
