using System;
using System.Collections.Generic;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>그리기 모드. Draw=그리기, Erase=지우기, RoiFill=ROI 영역 채우기.</summary>
    public enum DrawMode { Draw, Erase, RoiFill }

    /// <summary>
    /// LabVIEW "Drawing Panel.vi" 의 NxN 불리언 매트릭스(토출 패턴) 코어.
    /// 펜 브러시 드로잉, ROI 영역 채우기, 닫힌영역 자동채움, Undo/Redo 스냅샷 제공.
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
            Rows = Math.Max(1, rows);
            Cols = Math.Max(1, cols);
            Cells = new bool[Rows, Cols];
        }

        public bool Get(int r, int c) => Cells[r, c];
        public void Clear() => Cells = new bool[Rows, Cols];
        private bool InRange(int r, int c) => r >= 0 && r < Rows && c >= 0 && c < Cols;

        /// <summary>
        /// 펜 브러시로 (r,c) 중심 penWidth×penWidth 정사각을 칠한다.
        /// penWidth=1 이면 단일 셀. 드래그 시 지점마다 호출.
        /// </summary>
        public void PaintBrush(int r, int c, int penWidth, bool on)
        {
            int half = Math.Max(0, (penWidth - 1) / 2);
            int extra = (penWidth - 1) % 2;
            for (int dr = -half; dr <= half + extra; dr++)
                for (int dc = -half; dc <= half + extra; dc++)
                {
                    int rr = r + dr, cc = c + dc;
                    if (InRange(rr, cc)) Cells[rr, cc] = on;
                }
        }

        /// <summary>ROI 영역 채우기(Fill). 사각 ROI 내부를 on/off. 점 ROI면 단일 셀.</summary>
        public void FillRoi(int r0, int c0, int r1, int c1, bool on)
        {
            int rt = Math.Min(r0, r1), rb = Math.Max(r0, r1);
            int cl = Math.Min(c0, c1), cr = Math.Max(c0, c1);
            for (int r = rt; r <= rb; r++)
                for (int c = cl; c <= cr; c++)
                    if (InRange(r, c)) Cells[r, c] = on;
        }

        /// <summary>4방향 플러드필(연결된 같은 값 영역).</summary>
        public void FloodFill(int r, int c, bool target)
        {
            if (!InRange(r, c) || Cells[r, c] == target) return;
            bool from = Cells[r, c];
            var stack = new Stack<(int, int)>();
            stack.Push((r, c));
            while (stack.Count > 0)
            {
                var (cr, cc) = stack.Pop();
                if (!InRange(cr, cc) || Cells[cr, cc] != from) continue;
                Cells[cr, cc] = target;
                stack.Push((cr + 1, cc)); stack.Push((cr - 1, cc));
                stack.Push((cr, cc + 1)); stack.Push((cr, cc - 1));
            }
        }

        /// <summary>Auto Fill: 그린 경계(on) 안쪽 빈 영역을 자동으로 채움.</summary>
        public void AutoFillEnclosed()
        {
            var outside = new bool[Rows, Cols];
            var stack = new Stack<(int, int)>();
            for (int c = 0; c < Cols; c++)
            {
                if (!Cells[0, c]) stack.Push((0, c));
                if (!Cells[Rows - 1, c]) stack.Push((Rows - 1, c));
            }
            for (int r = 0; r < Rows; r++)
            {
                if (!Cells[r, 0]) stack.Push((r, 0));
                if (!Cells[r, Cols - 1]) stack.Push((r, Cols - 1));
            }
            while (stack.Count > 0)
            {
                var (cr, cc) = stack.Pop();
                if (!InRange(cr, cc) || Cells[cr, cc] || outside[cr, cc]) continue;
                outside[cr, cc] = true;
                stack.Push((cr + 1, cc)); stack.Push((cr - 1, cc));
                stack.Push((cr, cc + 1)); stack.Push((cr, cc - 1));
            }
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    if (!Cells[r, c] && !outside[r, c]) Cells[r, c] = true;
        }

        public bool[,] Snapshot() => (bool[,])Cells.Clone();

        public void Restore(bool[,] snap)
        {
            Rows = snap.GetLength(0);
            Cols = snap.GetLength(1);
            Cells = (bool[,])snap.Clone();
        }
    }
}
