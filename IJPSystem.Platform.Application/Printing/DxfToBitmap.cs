using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using netDxf;
using netDxf.Entities;
using IJPSystem.Platform.Common.Utilities;

namespace IJPSystem.Platform.Application.Printing
{
    /// <summary>DXF → 비트맵 변환 옵션.</summary>
    public sealed class DxfRasterOptions
    {
        /// <summary>
        /// 인쇄 DPI. DXF 실측 단위(mm 가정)를 이 해상도로 래스터화하므로
        /// <b>비트맵 1픽셀 = 1드롭</b>이 되어 인쇄와 정합한다.
        /// </summary>
        public int Dpi { get; set; } = 600;

        /// <summary>DXF 도면 단위 → mm 환산 계수. 도면이 mm 면 1.0, inch 면 25.4.</summary>
        public double UnitToMm { get; set; } = 1.0;

        /// <summary>도형 둘레 여백[mm].</summary>
        public double MarginMm { get; set; } = 0.5;

        /// <summary>닫힌 도형 내부를 채울지(false 면 외곽선만).</summary>
        public bool Fill { get; set; } = true;

        /// <summary>선/외곽선 두께[px].</summary>
        public float StrokePx { get; set; } = 1f;

        /// <summary>결과 비트맵 최대 변[px] — 실수로 거대 이미지를 만드는 것을 막는 안전장치.</summary>
        public int MaxDimensionPx { get; set; } = 20000;

        /// <summary>잉크(도형) 색 — 어두운 값. 배경은 흰색.</summary>
        public bool InkIsBlack { get; set; } = true;

        /// <summary>변환할 레이어 이름(대소문자 무시). null 또는 비어 있으면 전체 레이어.</summary>
        public System.Collections.Generic.ISet<string>? LayerFilter { get; set; }

        /// <summary>X/Y DPI 를 따로 줄 때 사용. 0 이면 <see cref="Dpi"/> 를 X·Y 공통으로 쓴다.</summary>
        public int DpiY { get; set; } = 0;
    }

    /// <summary>DXF → 비트맵 변환 결과(메타 포함).</summary>
    public sealed class DxfRasterResult
    {
        public bool   Success { get; set; }
        public string Message { get; set; } = "";
        public string? OutputPath { get; set; }

        public int    WidthPx  { get; set; }
        public int    HeightPx { get; set; }
        public double WidthMm  { get; set; }
        public double HeightMm { get; set; }
        public int    EntityCount { get; set; }
    }

    /// <summary>
    /// DXF 도면을 인쇄용 비트맵으로 래스터화한다.
    /// (LabVIEW 계열의 "패턴 이미지 준비" — 벡터 도면을 드롭 격자 비트맵으로)
    ///
    /// <b>스케일 원칙</b>: DXF 는 실측 벡터(mm)다. 인쇄 DPI 로 래스터화하면 픽셀 간격 = 드롭 간격이
    /// 되어, 만든 비트맵을 그대로 인쇄 데이터로 쓸 수 있다. 임의 크기로 맞추면(fit) 실측이 깨져
    /// 인쇄물 치수가 틀어지므로 하지 않는다.
    ///
    /// 지원: Line, Circle, Arc, Ellipse, Polyline2D(닫힘=채움/열림=선), Hatch(채움 영역).
    /// 미지원 엔티티(문자·치수·3D 등)는 건너뛴다 — 인쇄 패턴은 2D 기하만 의미가 있다.
    /// System.Drawing 사용(net8-windows). 좌표계: DXF 는 Y 상향, 비트맵은 Y 하향 → Y 뒤집음.
    /// </summary>
    public static class DxfToBitmap
    {
        /// <summary>DXF 파일을 비트맵으로 변환해 <paramref name="outputPath"/>(png)에 저장한다.</summary>
        public static DxfRasterResult Convert(string dxfPath, string outputPath, DxfRasterOptions? options = null)
        {
            var opt = options ?? new DxfRasterOptions();
            if (string.IsNullOrEmpty(dxfPath) || !File.Exists(dxfPath))
                return Fail($"DXF 파일을 찾을 수 없습니다: {dxfPath}");
            if (opt.Dpi <= 0) return Fail("DPI 는 0보다 커야 합니다.");

            DxfDocument doc;
            try { doc = DxfDocument.Load(dxfPath); }
            catch (Exception ex) { return Fail($"DXF 로드 실패: {ex.Message}"); }
            if (doc == null) return Fail("DXF 를 읽을 수 없습니다(형식 오류).");

            // 1) 도면 범위 산출 — 지원 기하의 경계(선택 레이어만).
            var geom = CollectGeometry(doc, opt.LayerFilter);
            if (geom.Count == 0)
                return Fail(opt.LayerFilter is { Count: > 0 }
                    ? "선택한 레이어에 변환할 2D 기하가 없습니다."
                    : "변환할 2D 기하가 없습니다(문자·치수·3D 만 있거나 빈 도면).");

            var bounds = ComputeBounds(geom);
            if (bounds.width <= 0 || bounds.height <= 0)
                return Fail("도면 범위가 0 입니다(모든 요소가 한 점).");

            // 2) mm → px. DXF 단위를 mm 로 바꾼 뒤 DPI 로 픽셀화. X/Y DPI 를 따로 줄 수 있다.
            int dpiY = opt.DpiY > 0 ? opt.DpiY : opt.Dpi;
            double pxPerMmX = opt.Dpi / DpiConverter.InchToMm;   // px/mm
            double pxPerMmY = dpiY     / DpiConverter.InchToMm;
            double marginUnit = opt.MarginMm / opt.UnitToMm;

            double drawW = (bounds.width  + 2 * marginUnit) * opt.UnitToMm;   // mm
            double drawH = (bounds.height + 2 * marginUnit) * opt.UnitToMm;
            int wPx = (int)Math.Ceiling(drawW * pxPerMmX);
            int hPx = (int)Math.Ceiling(drawH * pxPerMmY);

            if (wPx < 1 || hPx < 1) return Fail("계산된 비트맵 크기가 0 입니다.");
            if (wPx > opt.MaxDimensionPx || hPx > opt.MaxDimensionPx)
                return Fail($"비트맵 크기 {wPx}×{hPx}px 가 상한({opt.MaxDimensionPx})을 넘습니다. " +
                            "DPI 를 낮추거나 도면 단위(UnitToMm)를 확인하세요.");

            // 3) DXF 좌표 → 픽셀 좌표 변환.
            //    px = (coord - min + margin) * unitToMm * pxPerMm,  Y 는 상하 반전.
            double sX = opt.UnitToMm * pxPerMmX;
            double sY = opt.UnitToMm * pxPerMmY;
            double ox = (-bounds.minX + marginUnit) * sX;
            double oy = (-bounds.minY + marginUnit) * sY;
            PointF ToPx(double x, double y) => new((float)(x * sX + ox), (float)(hPx - (y * sY + oy)));

            try
            {
                using var bmp = new Bitmap(wPx, hPx, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    Color bg  = opt.InkIsBlack ? Color.White : Color.Black;
                    Color ink = opt.InkIsBlack ? Color.Black : Color.White;
                    g.Clear(bg);
                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    using var pen   = new Pen(ink, Math.Max(0.1f, opt.StrokePx));
                    using var brush = new SolidBrush(ink);
                    Render(g, geom, ToPx, opt, pen, brush);
                }

                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                bmp.Save(outputPath, ImageFormat.Png);

                return new DxfRasterResult
                {
                    Success     = true,
                    Message     = "OK",
                    OutputPath  = outputPath,
                    WidthPx     = wPx,
                    HeightPx    = hPx,
                    WidthMm     = drawW,
                    HeightMm    = drawH,
                    EntityCount = geom.Count,
                };
            }
            catch (Exception ex) { return Fail($"래스터화 실패: {ex.Message}"); }
        }

        // ── 기하 수집 ─────────────────────────────────────────────────────────
        // netDxf 엔티티를 렌더러가 다루기 쉬운 중립 형태로 모은다.
        private abstract class Shape { }
        private sealed class Seg      : Shape { public List<PointF2> Pts = new(); public bool Closed; }
        private sealed class Circ     : Shape { public double Cx, Cy, R; }
        private sealed class ArcSeg   : Shape { public double Cx, Cy, R, StartDeg, EndDeg; }
        private sealed class Elip     : Shape { public double Cx, Cy, Rx, Ry; }
        private struct PointF2 { public double X, Y; public PointF2(double x, double y){X=x;Y=y;} }

        /// <summary>DXF 안에서 지원 기하가 있는 레이어 이름 목록(변환 대상 후보).</summary>
        public static IReadOnlyList<string> GetLayers(string dxfPath)
        {
            try
            {
                var doc = DxfDocument.Load(dxfPath);
                if (doc == null) return Array.Empty<string>();
                var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                void Add(EntityObject e) { if (e.Layer?.Name is { Length: > 0 } n) names.Add(n); }
                foreach (var e in doc.Entities.Lines)       Add(e);
                foreach (var e in doc.Entities.Circles)     Add(e);
                foreach (var e in doc.Entities.Arcs)        Add(e);
                foreach (var e in doc.Entities.Ellipses)    Add(e);
                foreach (var e in doc.Entities.Polylines2D) Add(e);
                foreach (var e in doc.Entities.Hatches)     Add(e);
                return names.ToList();
            }
            catch { return Array.Empty<string>(); }
        }

        private static List<Shape> CollectGeometry(DxfDocument doc, ISet<string>? layers)
        {
            var list = new List<Shape>();
            bool Keep(EntityObject e) => layers is not { Count: > 0 } || layers.Contains(e.Layer?.Name ?? "");

            foreach (var l in doc.Entities.Lines)
            {
                if (!Keep(l)) continue;
                list.Add(new Seg { Pts = { new PointF2(l.StartPoint.X, l.StartPoint.Y), new PointF2(l.EndPoint.X, l.EndPoint.Y) } });
            }

            foreach (var c in doc.Entities.Circles)
            {
                if (!Keep(c)) continue;
                list.Add(new Circ { Cx = c.Center.X, Cy = c.Center.Y, R = c.Radius });
            }

            foreach (var a in doc.Entities.Arcs)
            {
                if (!Keep(a)) continue;
                list.Add(new ArcSeg { Cx = a.Center.X, Cy = a.Center.Y, R = a.Radius, StartDeg = a.StartAngle, EndDeg = a.EndAngle });
            }

            foreach (var e in doc.Entities.Ellipses)
            {
                if (!Keep(e)) continue;
                // netDxf: MajorAxis/MinorAxis 는 각각 장축·단축의 <b>전체 길이</b>(비율 아님).
                // 회전(Rotation)은 근사에서 무시(대부분 축정렬).
                double rx = e.MajorAxis / 2.0;
                double ry = e.MinorAxis / 2.0;
                list.Add(new Elip { Cx = e.Center.X, Cy = e.Center.Y, Rx = rx, Ry = ry });
            }

            foreach (var p in doc.Entities.Polylines2D)
            {
                if (!Keep(p)) continue;
                var seg = new Seg { Closed = p.IsClosed };
                foreach (var v in p.Vertexes) seg.Pts.Add(new PointF2(v.Position.X, v.Position.Y));
                if (seg.Pts.Count >= 2) list.Add(seg);
            }

            // Hatch: 이미 채움 영역. 경계 폴리라인의 꼭짓점을 닫힌 폴리곤으로 취급.
            foreach (var h in doc.Entities.Hatches)
            {
                if (!Keep(h)) continue;
                foreach (var bp in h.BoundaryPaths)
                {
                    var seg = new Seg { Closed = true };
                    foreach (var edge in bp.Edges)
                    {
                        var v = edge.ConvertTo();   // 폴리라인 정점으로 근사
                        // Edge → 시작점만 취해 폴리곤 근사(곡선 경계는 다각형화)
                        if (v is netDxf.Entities.Line ln) seg.Pts.Add(new PointF2(ln.StartPoint.X, ln.StartPoint.Y));
                    }
                    if (seg.Pts.Count >= 3) list.Add(seg);
                }
            }

            return list;
        }

        private static (double minX, double minY, double width, double height) ComputeBounds(List<Shape> shapes)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            void Acc(double x, double y) { if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y; }

            foreach (var sh in shapes)
                switch (sh)
                {
                    case Seg s:    foreach (var p in s.Pts) Acc(p.X, p.Y); break;
                    case Circ c:   Acc(c.Cx - c.R, c.Cy - c.R); Acc(c.Cx + c.R, c.Cy + c.R); break;
                    case ArcSeg a: Acc(a.Cx - a.R, a.Cy - a.R); Acc(a.Cx + a.R, a.Cy + a.R); break;   // 보수적으로 원 경계
                    case Elip e:   Acc(e.Cx - e.Rx, e.Cy - e.Ry); Acc(e.Cx + e.Rx, e.Cy + e.Ry); break;
                }

            if (minX > maxX) return (0, 0, 0, 0);
            return (minX, minY, maxX - minX, maxY - minY);
        }

        private static void Render(Graphics g, List<Shape> shapes, Func<double, double, PointF> toPx,
                                   DxfRasterOptions opt, Pen pen, Brush brush)
        {
            foreach (var sh in shapes)
                switch (sh)
                {
                    case Seg s when s.Pts.Count >= 2:
                    {
                        var pts = s.Pts.Select(p => toPx(p.X, p.Y)).ToArray();
                        if (s.Closed && opt.Fill && pts.Length >= 3) g.FillPolygon(brush, pts);
                        else if (s.Closed) { var poly = pts.Append(pts[0]).ToArray(); g.DrawLines(pen, poly); }
                        else g.DrawLines(pen, pts);
                        break;
                    }
                    case Circ c:
                    {
                        var rect = EllipseRect(toPx, c.Cx, c.Cy, c.R, c.R);
                        if (opt.Fill) g.FillEllipse(brush, rect); else g.DrawEllipse(pen, rect);
                        break;
                    }
                    case Elip e:
                    {
                        var rect = EllipseRect(toPx, e.Cx, e.Cy, e.Rx, e.Ry);
                        if (opt.Fill) g.FillEllipse(brush, rect); else g.DrawEllipse(pen, rect);
                        break;
                    }
                    case ArcSeg a:
                    {
                        // 호는 열린 곡선 → 항상 선. DXF 각도는 반시계(CCW, Y상향).
                        // 비트맵은 Y 하향이라 뒤집힘을 반영: sweep 부호와 시작각을 화면 좌표계로 변환.
                        var rect = EllipseRect(toPx, a.Cx, a.Cy, a.R, a.R);
                        float start = (float)(-a.StartDeg);
                        float sweep = (float)(-(a.EndDeg - a.StartDeg));
                        if (Math.Abs(sweep) < 0.01f) sweep = 360f;
                        g.DrawArc(pen, rect, start, sweep);
                        break;
                    }
                }
        }

        // 중심(월드) + 반경 → 픽셀 사각형. Y 반전 때문에 상/하 픽셀이 뒤바뀌므로 min 으로 정규화.
        private static RectangleF EllipseRect(Func<double, double, PointF> toPx, double cx, double cy, double rx, double ry)
        {
            var p0 = toPx(cx - rx, cy - ry);
            var p1 = toPx(cx + rx, cy + ry);
            float x = Math.Min(p0.X, p1.X), y = Math.Min(p0.Y, p1.Y);
            return new RectangleF(x, y, Math.Abs(p1.X - p0.X), Math.Abs(p1.Y - p0.Y));
        }

        private static DxfRasterResult Fail(string msg) => new() { Success = false, Message = msg };
    }
}
