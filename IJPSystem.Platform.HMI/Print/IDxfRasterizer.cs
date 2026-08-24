using System;
using System.Collections.Generic;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// LabVIEW "Rasterizer_Main.vi" (DXF Rasterizer) 변환 인터페이스.
    /// DXF 도면 → 채움/디더링 → 노즐별 토출 패턴 + BMP 생성.
    /// </summary>
    public interface IDxfRasterizer
    {
        /// <summary>DXF 로드 후 레이어 목록 반환. (drawDXFobjs / Layer.lvclass)</summary>
        IReadOnlyList<string> LoadDxf(string dxfPath);

        /// <summary>
        /// 선택 레이어를 래스터화하여 BMP/패턴 생성.
        /// (fill → IMG Dithering → Samba12/S800 Pattern GEN → Write BMP File)
        /// </summary>
        /// <param name="selectedLayers">변환할 레이어 이름.</param>
        /// <param name="param">DPI/Interval/사용노즐.</param>
        /// <param name="progress">0~1 진행률(Loading Complete 바).</param>
        RasterizeResult Convert(IReadOnlyList<string> selectedLayers,
            ConvertParameters param, IProgress<double>? progress = null);

        /// <summary>
        /// 빈 레이어(흰 캔버스) 생성. (Create Empty Layer)
        /// 경로만 잡는 게 아니라 실제 흰 이미지를 쓴다 — 그려 넣을 대상이 있어야 한다.
        /// </summary>
        RasterizeResult CreateEmptyLayer(ConvertParameters param, double widthMm, double lengthMm);

        /// <summary>
        /// 이미지에서 바로 토출 패턴을 만든다 — DXF 가 없는 경로(Open BMP / Edit Panel).
        /// </summary>
        RasterizeResult ConvertImage(string imagePath, ConvertParameters param,
                                     IProgress<double>? progress = null);

        /// <summary>기존 BMP 열어 미리보기/사용. (Open BMP)</summary>
        RasterizeResult OpenBmp(string bmpPath);

        /// <summary>
        /// 인쇄 데이터 저장. (Save)
        /// 패턴 비트맵 + POS.dat + Print_Para.dat 세 벌을 만든다 — 원본 저장 버튼과 같다.
        /// </summary>
        SavedPrintData Save(RasterizeResult result);
    }
}
