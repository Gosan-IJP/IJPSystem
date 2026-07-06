using IJPSystem.Platform.Domain.Interfaces;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace IJPSystem.Platform.HMI.Vision
{
    /// <summary>
    /// Visual Monitor 뷰 소스(카메라 뷰). 프리젠테이션 계층 추상화로 최신 프레임을
    /// BitmapSource 로 제공한다. (드라이버 아님 — WPF 이미지에 결합되어 HMI 에 둔다)
    /// </summary>
    public interface IImageSource : IDisposable
    {
        string Name { get; }
        void Open();
        /// <summary>최신 프레임(BitmapSource). 프레임이 없으면 null.</summary>
        Task<BitmapSource?> GrabFrameAsync();
        void Close();
        bool IsOpen { get; }
    }

    /// <summary>가상 뷰 소스 — 카메라 없이 회색 배경 + 노이즈 더미 프레임 생성. (Virtual 모드용)</summary>
    public sealed class VirtualImageSource : IImageSource
    {
        private readonly int _w, _h;
        private readonly Random _rnd = new();
        public string Name { get; }
        public bool IsOpen { get; private set; }

        public VirtualImageSource(string name = "Virtual", int width = 640, int height = 480)
        { Name = name; _w = width; _h = height; }

        public void Open() => IsOpen = true;
        public void Close() => IsOpen = false;

        public Task<BitmapSource?> GrabFrameAsync()
        {
            int stride = _w * 4;
            var px = new byte[_h * stride];
            for (int i = 0; i < px.Length; i += 4) { px[i] = 200; px[i + 1] = 200; px[i + 2] = 200; px[i + 3] = 255; }
            for (int n = 0; n < _w * _h / 300; n++)   // 랜덤 점(액적/글라스 뷰 흉내)
            {
                int x = _rnd.Next(_w), y = _rnd.Next(_h);
                int i = y * stride + x * 4;
                px[i] = px[i + 1] = px[i + 2] = 60;
            }
            var bmp = new WriteableBitmap(_w, _h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, _w, _h), px, stride, 0);
            bmp.Freeze();
            return Task.FromResult<BitmapSource?>(bmp);
        }
        public void Dispose() => Close();
    }

    /// <summary>
    /// 공용 IVisionDriver 를 감싼 뷰 소스(카메라 스택 통일).
    /// 캡쳐 결과 파일을 BitmapSource 로 로드해 제공한다.
    /// </summary>
    public sealed class VisionDriverImageSource : IImageSource
    {
        private readonly IVisionDriver _vision;
        private readonly string _cameraId;
        public string Name { get; }
        public bool IsOpen { get; private set; }

        public VisionDriverImageSource(string name, IVisionDriver vision, string cameraId)
        { Name = name; _vision = vision; _cameraId = cameraId; }

        public void Open() => IsOpen = true;
        public void Close() => IsOpen = false;

        public async Task<BitmapSource?> GrabFrameAsync()
        {
            var img = await _vision.CaptureAsync(_cameraId);
            if (!img.IsValid || string.IsNullOrEmpty(img.FilePath) || !File.Exists(img.FilePath))
                return null;
            return Load(img.FilePath);
        }

        private static BitmapSource Load(string path)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption   = BitmapCacheOption.OnLoad;              // 파일 잠금 방지
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;  // 매 프레임 새로 로드
            bmp.UriSource     = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        public void Dispose() => Close();
    }
}
