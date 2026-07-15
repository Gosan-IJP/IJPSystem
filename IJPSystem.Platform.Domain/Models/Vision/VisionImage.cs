using System;

namespace IJPSystem.Platform.Domain.Models.Vision
{
    public class VisionImage
    {
        public string   CameraId    { get; set; } = string.Empty;
        public DateTime CaptureTime { get; set; } = DateTime.Now;
        public int      Width       { get; set; }
        public int      Height      { get; set; }
        public string?  FilePath    { get; set; }   // 저장된 이미지 경로 (옵션)
        public bool     IsValid     { get; set; } = true;

        // 원본 픽셀 버퍼(옵션). 있으면 분석(OpenCV 등)이 디스크 재로드 없이 in-memory 로 처리한다.
        // 고속 드랍와쳐(0.5ms 노출·위상 스윕)에서 프레임당 파일 왕복을 피하기 위함.
        // 레이아웃: 상단→하단 행 우선, 채널당 BitsPerPixel(8=Mono8, 16=Mono16). null 이면 FilePath 사용.
        public byte[]?  PixelData    { get; set; }
        public int      BitsPerPixel { get; set; } = 8;

        public static VisionImage Invalid(string cameraId) =>
            new() { CameraId = cameraId, IsValid = false };
    }
}
