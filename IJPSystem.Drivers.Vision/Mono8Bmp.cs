using System.IO;

namespace IJPSystem.Drivers.Vision
{
    /// <summary>
    /// Mono8 버퍼를 8bpp 그레이스케일 BMP 로 저장한다.
    /// 드라이버(IMAQdx/eBUS)가 공통으로 쓰므로 한 곳에 둔다 — 저장 포맷이 갈리면
    /// 나중에 이미지를 읽는 쪽(드랍와처 분석·로그 뷰어)에서 드라이버별 분기가 생긴다.
    /// </summary>
    internal static class Mono8Bmp
    {
        public static void Save(string path, byte[] mono, int w, int h)
        {
            int rowStride    = ((w + 3) / 4) * 4;    // 4바이트 정렬
            int pixelBytes   = rowStride * h;
            int paletteBytes = 256 * 4;
            int offset       = 14 + 40 + paletteBytes;
            int fileSize     = offset + pixelBytes;

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);

            // File Header
            bw.Write((byte)'B'); bw.Write((byte)'M');
            bw.Write(fileSize); bw.Write(0); bw.Write(offset);

            // DIB Header (BITMAPINFOHEADER)
            bw.Write(40); bw.Write(w); bw.Write(-h);   // 음수 높이 = top-down
            bw.Write((short)1); bw.Write((short)8);    // 8bpp
            bw.Write(0); bw.Write(pixelBytes);
            bw.Write(2835); bw.Write(2835);            // 72 DPI
            bw.Write(256); bw.Write(0);                // 팔레트 256색

            // Grayscale 팔레트
            for (int i = 0; i < 256; i++) { bw.Write((byte)i); bw.Write((byte)i); bw.Write((byte)i); bw.Write((byte)0); }

            // Pixel Data
            var row = new byte[rowStride];
            for (int y = 0; y < h; y++)
            {
                int src = y * w;
                for (int x = 0; x < w; x++) row[x] = (src + x < mono.Length) ? mono[src + x] : (byte)0;
                for (int x = w; x < rowStride; x++) row[x] = 0;
                bw.Write(row);
            }
        }
    }
}
