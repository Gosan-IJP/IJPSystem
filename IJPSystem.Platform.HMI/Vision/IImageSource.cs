using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Vision;
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

    /// <summary>
    /// 라이브 프리뷰용 프레임 버퍼. 같은 WriteableBitmap 을 계속 재사용해 픽셀만 덮어쓴다.
    ///
    /// 프레임마다 BitmapSource.Create 로 새로 만들면 1280×1024 기준 1.3MB 가 매번 대형 객체 힙에
    /// 잡히고(85KB 초과), 초당 5장이면 Gen2 수집이 잦아져 화면이 끊긴다. 재사용하면 할당이 0 이다.
    ///
    /// ※ UI 스레드 전용. WriteableBitmap 은 Freeze 할 수 없고 만든 스레드에서만 쓸 수 있다 —
    ///   DispatcherTimer 틱에서 호출할 것(await 뒤에도 UI 컨텍스트로 돌아온다).
    /// </summary>
    public sealed class LiveFrameBuffer
    {
        private WriteableBitmap? _bmp;
        private int _w, _h, _bpp;

        /// <summary>픽셀을 써 넣고 화면에 바인딩할 소스를 돌려준다. 버퍼/포맷이 맞지 않으면 null.</summary>
        public BitmapSource? Write(VisionImage img)
        {
            if (img.PixelData == null || img.Width <= 0 || img.Height <= 0) return null;

            var fmt = img.BitsPerPixel switch
            {
                8  => System.Windows.Media.PixelFormats.Gray8,
                24 => System.Windows.Media.PixelFormats.Bgr24,
                32 => System.Windows.Media.PixelFormats.Bgra32,
                _  => default(System.Windows.Media.PixelFormat),
            };
            if (fmt == default) return null;

            int stride = img.Width * (img.BitsPerPixel / 8);
            if (img.PixelData.Length < stride * img.Height) return null;   // 버퍼 부족 — 포맷 불일치

            // 해상도나 포맷이 바뀌면(ROI 변경 등) 새로 만든다.
            if (_bmp == null || _w != img.Width || _h != img.Height || _bpp != img.BitsPerPixel)
            {
                _bmp = new WriteableBitmap(img.Width, img.Height, 96, 96, fmt, null);
                _w = img.Width; _h = img.Height; _bpp = img.BitsPerPixel;
            }

            _bmp.WritePixels(new Int32Rect(0, 0, img.Width, img.Height), img.PixelData, stride, 0);
            return _bmp;
        }
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
    /// 파일에서 읽은 정지 이미지 뷰 소스 — "Load Image" 로 불러온 인쇄 이미지 표시용.
    /// 카메라와 같은 소스 목록에 넣어 두면 타이머/줌/크로스라인 로직을 그대로 쓸 수 있다.
    /// (매 틱 같은 프레임을 돌려주므로 화면은 정지 상태)
    /// </summary>
    public sealed class StaticImageSource : IImageSource
    {
        private BitmapSource? _frame;
        public string Name { get; }
        public bool IsOpen { get; private set; }
        public string? FilePath { get; private set; }

        public StaticImageSource(string name) => Name = name;

        /// <summary>표시할 이미지 교체. 다시 Load 하면 같은 소스의 내용만 바뀐다.</summary>
        public void SetImage(BitmapSource frame, string filePath)
        { _frame = frame; FilePath = filePath; }

        public void Open() => IsOpen = true;
        public void Close() => IsOpen = false;
        public Task<BitmapSource?> GrabFrameAsync() => Task.FromResult(_frame);
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
            // saveToDisk:false — 미리보기는 초당 5장 캡처하므로 파일로 남기면 디스크가 순식간에 찬다.
            // 픽셀 버퍼로 직접 화면에 그린다(디스크 왕복도 없음).
            var img = await _vision.CaptureAsync(_cameraId, saveToDisk: false);
            if (!img.IsValid) return null;

            var fromPixels = FromPixels(img);
            if (fromPixels != null) return fromPixels;

            // 버퍼가 없는 드라이버/경로면 파일로 폴백
            if (string.IsNullOrEmpty(img.FilePath) || !File.Exists(img.FilePath)) return null;
            return Load(img.FilePath);
        }

        /// <summary>VisionImage 의 픽셀 버퍼를 BitmapSource 로 변환. 버퍼/크기가 없으면 null.</summary>
        public static BitmapSource? FromPixels(VisionImage img)
        {
            if (img.PixelData == null || img.Width <= 0 || img.Height <= 0) return null;

            var fmt = img.BitsPerPixel switch
            {
                8  => System.Windows.Media.PixelFormats.Gray8,
                24 => System.Windows.Media.PixelFormats.Bgr24,
                32 => System.Windows.Media.PixelFormats.Bgra32,
                _  => default(System.Windows.Media.PixelFormat),
            };
            if (fmt == default) return null;

            int stride = img.Width * (img.BitsPerPixel / 8);
            if (img.PixelData.Length < stride * img.Height) return null;   // 버퍼 부족 — 포맷 불일치

            var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, fmt, null, img.PixelData, stride);
            bmp.Freeze();
            return bmp;
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
