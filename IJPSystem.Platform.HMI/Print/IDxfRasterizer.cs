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

        /// <summary>빈 레이어(빈 BMP) 생성. (Create Empty Layer)</summary>
        RasterizeResult CreateEmptyLayer(ConvertParameters param);

        /// <summary>기존 BMP 열어 미리보기/사용. (Open BMP)</summary>
        RasterizeResult OpenBmp(string bmpPath);

        /// <summary>결과(BMP + 패턴) 저장. (Save)</summary>
        void Save(RasterizeResult result);
    }
}
