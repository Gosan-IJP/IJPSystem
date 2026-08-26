using IJPSystem.Platform.Application.Printing;
using IJPSystem.Platform.Common.Enums;
using IJPSystem.Platform.Application.Sequences;
using IJPSystem.Platform.HMI.ViewModels;
using System;
using System.Collections.Generic;

namespace IJPSystem.Platform.HMI.Services
{
    /// <summary>
    /// 인쇄 원점 = 레시피 티칭의 <b>PRINT START</b> 자리.
    ///
    /// <para>예전에는 <c>Config\PrintOrigin.dat</c> 에 따로 적었다. 그러면 같은 값이 두 군데
    /// 생기고, 티칭 화면에서 PRINT START 를 옮긴 날 둘이 갈라진다 — 원점 창에는 옛 값이 뜨는데
    /// 인쇄는 새 자리에서 시작한다. 값의 주인을 하나로 두면 갈라질 자리가 없다.</para>
    ///
    /// <para>쓰는 축은 <b>X·Y 뿐</b>이다. PRINT START 에서 T 는 움직이지 않고, Z 는 헤드 높이라
    /// 원점이 아니다 — 여기서 Z 까지 적으면 티칭해 둔 헤드 높이가 스테이지 현재값으로 밀린다.</para>
    /// </summary>
    public sealed class RecipePrintOriginStore : IPrintOriginStore
    {
        private readonly MainViewModel _mainVM;

        public RecipePrintOriginStore(MainViewModel mainVM)
            => _mainVM = mainVM ?? throw new ArgumentNullException(nameof(mainVM));

        /// <summary>이 원점이 어느 티칭 포인트인지 — 화면에 그대로 보여 준다.</summary>
        public static string PointName => PointNames.PrintStart;

        public bool TryRead(out AxisPoint origin)
        {
            origin = default;

            var axes = _mainVM.RecipeVM?.GetPointAxes(PointNames.PrintStart);
            if (axes == null) return false;

            double Get(string a) => axes.TryGetValue(a, out double v) ? v : 0.0;
            origin = new AxisPoint(Get("X"), Get("Y"), Get("Z"));
            return true;
        }

        public bool Write(AxisPoint origin, out string message)
        {
            var recipe = _mainVM.RecipeVM;
            if (recipe == null) { message = "레시피 화면이 아직 준비되지 않았습니다."; return false; }

            // X·Y 만 넘긴다 — 여기 없는 축은 SetPointAxes 가 건드리지 않는다.
            var axes = new Dictionary<string, double> { ["X"] = origin.X, ["Y"] = origin.Y };

            bool ok = recipe.SetPointAxes(PointNames.PrintStart, axes, out message);
            _mainVM.AddLog(
                ok ? $"[PRINT] 인쇄 원점 → {message}"
                   : $"[PRINT] 인쇄 원점 저장 실패: {message}",
                ok ? LogLevel.Success : LogLevel.Error);
            return ok;
        }
    }
}
