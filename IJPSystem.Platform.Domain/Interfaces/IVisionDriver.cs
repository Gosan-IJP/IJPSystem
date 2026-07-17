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
        Task<VisionImage> CaptureAsync(string cameraId, bool saveToDisk = true);
        Task<VisionImage> WaitForHardwareTriggerAsync(string cameraId, CancellationToken ct);

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
