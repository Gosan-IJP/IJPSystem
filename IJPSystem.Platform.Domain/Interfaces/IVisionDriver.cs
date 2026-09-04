using IJPSystem.Platform.Domain.Models.Vision;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Domain.Interfaces
{
    public interface IVisionDriver
    {
        // ── 1. 연결 / 초기화 — Disconnect 가 통신 종료 + 자원 해제 모두 수행 ──
        bool Connect();
        void Disconnect();
        bool IsConnected { get; }
        void Initialize(List<CameraDeviceInfo> configs);

        // ── 2. 상태 조회 ──
        CameraStatus GetStatus(string cameraId);
        List<CameraStatus> GetAllStatus();

        // ── 3. 촬영 ──
        /// <param name="saveToDisk">
        /// false 면 이미지를 파일로 남기지 않고 픽셀 버퍼(VisionImage.PixelData)만 채운다.
        /// 라이브 미리보기처럼 초당 수 장씩 반복 캡처하는 경로는 반드시 false — true 로 두면
        /// 프레임마다 BMP 가 쌓여 디스크가 순식간에 찬다(5fps × 656KB ≈ 12GB/시간).
        /// </param>
        /// <param name="timeoutMs">
        /// 프레임 한 장을 기다리는 한계 [ms]. 0 = 드라이버 기본값.
        ///
        /// <para><b>라이브는 짧게, 재는 촬상은 길게</b> 잡아야 한다. 기본값(1초)으로 라이브를
        /// 돌리면 프레임을 한 번 놓칠 때마다 화면이 1초 멈춰 "끊긴다"고 느껴진다 — 라이브는
        /// 한 장 건너뛰는 편이 낫다. 반대로 정렬 촬상은 그 한 장이 없으면 판이 실패하므로
        /// 넉넉히 기다려야 한다.</para>
        ///
        /// <para>기다림이 없는 드라이버(가상·파일 기반)는 무시한다.</para>
        /// </param>
        Task<VisionImage> CaptureAsync(string cameraId, bool saveToDisk = true, int timeoutMs = 0);
        Task<VisionImage> WaitForHardwareTriggerAsync(string cameraId, CancellationToken ct);

        /// <summary>
        /// 카메라를 하드웨어 트리거 모드로 전환하거나 자유 실행으로 되돌린다.
        ///
        /// <para><b>왜 켜고 끄는가</b> — 트리거 모드에서는 카메라가 트리거가 올 때만 프레임을
        /// 내보내므로, 켜 둔 채로 라이브뷰를 열면 화면이 멎는다. 트리거 체인이 도는 동안만 켠다.</para>
        ///
        /// <para>설정(VisionConfig 의 <c>TriggerSource</c>)이 비어 있거나 기종이 지원하지 않으면
        /// <b>조용히 자유 실행으로 남는다</b> — 예외를 던지지 않는다. 촬영 자체는 유효하고,
        /// 다만 스트로브와 동기되지 않았다는 사실이 로그에 남는다.</para>
        /// </summary>
        void SetHardwareTrigger(string cameraId, bool on);

        // ── 4. 검사 ──
        Task<InspectionResult> InspectAsync(string cameraId, VisionImage image);
        Task<InspectionResult> CaptureAndInspectAsync(string cameraId);

        // ── 5. 조명 제어 ──
        void SetLight(string cameraId, bool on);
        void SetLightIntensity(string cameraId, int intensity);   // 0 ~ 255

        // ── 6. 카메라 파라미터 ──
        void   SetExposure(string cameraId, double ms);
        void   SetGain(string cameraId, double gain);
        double GetExposure(string cameraId);
        double GetGain(string cameraId);
    }
}
