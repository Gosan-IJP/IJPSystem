using System;
using System.Collections.Generic;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>그리기 도구. (Free Drawing / Eraser / ROI 선택)</summary>
    public enum DrawTool { FreeDraw, Eraser, RoiSelect }

    /// <summary>ROI 형식. 사각(Global Rectangle) / 점(Points).</summary>
    public enum RoiType { Rectangle, Point }

    /// <summary>
    /// LabVIEW "Drawing Panel.vi" 의 토출 패턴 매트릭스 코어(완성판).
    /// Free Drawing(라인폭), ROI 채우기(Fill), 패턴 채우기(Pattern Fill),
    /// 경계 자동 채우기(Auto Fill), Undo/Redo 스냅샷 제공.
    /// true=검정(토출), false=흰색(빈칸).
    /// </summary>
    public sealed class PixelGrid
    {
        public int Rows { get; private set; }
        public int Cols { get; private set; }
        public bool[,] Cells { get; private set; } = new bool[1, 1];

        public PixelGrid(int rows, int cols) => Resize(rows, cols);

        public void Resize(int rows, int cols)
        {
            Rows = Math.Max(1, rows); Cols = Math.Max(1, cols);
            Cells = new bool[Rows, Cols];
        }

        public bool Get(int r, int c) => Cells[r, c];
        public void Clear() => Cells = new bool[Rows, Cols];
        private bool In(int r, int c) => r >= 0 && r < Rows && c >= 0 && c < Cols;

        /// <summary>Free Drawing: (r,c) 중심 lineWidth 정사각 브러시로 칠함.</summary>
        public void PaintBrush(int r, int c, int lineWidth, bool on)
        {
            int half = Math.Max(0, (lineWidth - 1) / 2);
            int extra = (lineWidth - 1) % 2;
            for (int dr = -half; dr <= half + extra; dr++)
                for (int dc = -half; dc <= half + extra; dc++)
                    if (In(r + dr, c + dc)) Cells[r + dr, c + dc] = on;
        }

        /// <summary>두 점 사이를 lineWidth 로 잇는 선 그리기(드래그 보간, 끊김 방지).</summary>
        public void PaintLine(int r0, int c0, int r1, int c1, int lineWidth, bool on)
        {
            int dr = Math.Abs(r1 - r0), dc = Math.Abs(c1 - c0);
            int sr = r0 < r1 ? 1 : -1, sc = c0 < c1 ? 1 : -1;
            int err = dc - dr;
            while (true)
            {
                PaintBrush(r0, c0, lineWidth, on);
                if (r0 == r1 && c0 == c1) break;
                int e2 = 2 * err;
                if (e2 > -dr) { err -= dr; c0 += sc; }
                if (e2 < dc) { err += dc; r0 += sr; }
            }
        }

        /// <summary>Fill: 사각 ROI 내부를 on/off 로 채움.</summary>
        public void FillRoi(int r0, int c0, int r1, int c1, bool on)
        {
            int rt = Math.Min(r0, r1), rb = Math.Max(r0, r1);
            int cl = Math.Min(c0, c1), cr = Math.Max(c0, c1);
            for (int r = rt; r <= rb; r++)
                for (int c = cl; c <= cr; c++)
                    if (In(r, c)) Cells[r, c] = on;
        }

        /// <summary>Pattern Fill: 사각 ROI 내부를 체커보드(격자) 패턴으로 채움. step=간격.</summary>
        public void PatternFillRoi(int r0, int c0, int r1, int c1, int step = 2)
        {
            if (step < 1) step = 1;
            int rt = Math.Min(r0, r1), rb = Math.Max(r0, r1);
            int cl = Math.Min(c0, c1), cr = Math.Max(c0, c1);
            for (int r = rt; r <= rb; r++)
                for (int c = cl; c <= cr; c++)
                    if (In(r, c)) Cells[r, c] = ((r + c) % step == 0);
        }

        /// <summary>Auto Fill: 그린 경계(on) 안쪽 빈 영역을 자동으로 채움(Boundary).</summary>
        public void AutoFillBoundary()
        {
            var outside = new bool[Rows, Cols];
            var st = new Stack<(int, int)>();
            for (int c = 0; c < Cols; c++)
            { if (!Cells[0, c]) st.Push((0, c)); if (!Cells[Rows - 1, c]) st.Push((Rows - 1, c)); }
            for (int r = 0; r < Rows; r++)
            { if (!Cells[r, 0]) st.Push((r, 0)); if (!Cells[r, Cols - 1]) st.Push((r, Cols - 1)); }
            while (st.Count > 0)
            {
                var (cr, cc) = st.Pop();
                if (!In(cr, cc) || Cells[cr, cc] || outside[cr, cc]) continue;
                outside[cr, cc] = true;
                st.Push((cr + 1, cc)); st.Push((cr - 1, cc)); st.Push((cr, cc + 1)); st.Push((cr, cc - 1));
            }
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    if (!Cells[r, c] && !outside[r, c]) Cells[r, c] = true;
        }

        /// <summary>점 기준 플러드필(연결 영역).</summary>
        public void FloodFill(int r, int c, bool target)
        {
            if (!In(r, c) || Cells[r, c] == target) return;
            bool from = Cells[r, c];
            var st = new Stack<(int, int)>(); st.Push((r, c));
            while (st.Count > 0)
            {
                var (cr, cc) = st.Pop();
                if (!In(cr, cc) || Cells[cr, cc] != from) continue;
                Cells[cr, cc] = target;
                st.Push((cr + 1, cc)); st.Push((cr - 1, cc)); st.Push((cr, cc + 1)); st.Push((cr, cc - 1));
            }
        }

        public bool[,] Snapshot() => (bool[,])Cells.Clone();
        public void Restore(bool[,] s) { Rows = s.GetLength(0); Cols = s.GetLength(1); Cells = (bool[,])s.Clone(); }
    }
}
