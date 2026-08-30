using IJPSystem.Platform.Application.Printing;
using IJPSystem.Platform.Common.Enums;
using IJPSystem.Platform.Application.Sequences;
using IJPSystem.Platform.HMI.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;

namespace IJPSystem.Platform.HMI.Services
{
    /// <summary>
    /// 인쇄 원점 = 레시피 티칭의 <b>PRINT ORIGIN</b> 자리.
    ///
    /// <para>예전에는 <c>Config\PrintOrigin.dat</c> 에 따로 적었다. 그러면 같은 값이 두 군데
    /// 생기고, 티칭 화면에서 자리를 옮긴 날 둘이 갈라진다 — 원점 창에는 옛 값이 뜨는데
    /// 인쇄는 새 자리에서 시작한다. 값의 주인을 하나로 두면 갈라질 자리가 없다.</para>
    ///
    /// <para>쓰는 축은 <b>X·Y 뿐</b>이다. PRINT ORIGIN 에서 T 는 움직이지 않고, Z 는 헤드 높이라
    /// 원점이 아니다 — 여기서 Z 까지 적으면 티칭해 둔 헤드 높이가 스테이지 현재값으로 밀린다.</para>
    /// </summary>
    public sealed class RecipePrintOriginStore : IPrintOriginStore
    {
        private readonly MainViewModel _mainVM;

        public RecipePrintOriginStore(MainViewModel mainVM)
            => _mainVM = mainVM ?? throw new ArgumentNullException(nameof(mainVM));

        /// <summary>이 원점이 어느 티칭 포인트인지 — 화면에 그대로 보여 준다.</summary>
        public static string PointName => PointNames.PrintOrigin;

        /// <summary>
        /// 축 태그(X·Y·Z) → 티칭에 적히는 축 이름.
        ///
        /// <para><b>둘은 다르다.</b> 티칭 표의 열쇠는 <c>Info.Name</c>("X AXIS")인데 코드가
        /// 부르는 이름은 <c>Info.AxisNo</c>("X")다. 여기서 태그를 그대로 열쇠로 쓰면 어느 행에도
        /// 안 맞아서, 읽을 때는 0 이 나오고 쓸 때는 엉뚱한 행이 새로 생긴다 —
        /// 원점 창에 0.000 이 떠 있던 것이 이것이었다.</para>
        ///
        /// <para>축 구성은 장비마다 다르므로(3축/6축) 표에서 찾는다. 못 찾으면 태그를 그대로
        /// 쓴다 — 이름이 곧 태그인 구성도 있다.</para>
        /// </summary>
        private string AxisKey(string tag)
            => _mainVM.SharedAxisList?.FirstOrDefault(
                   a => string.Equals(a.Info.AxisNo, tag, StringComparison.OrdinalIgnoreCase))
                   ?.Info.Name ?? tag;

        public bool TryRead(out AxisPoint origin)
        {
            origin = default;

            var axes = _mainVM.RecipeVM?.GetPointAxes(PointNames.PrintOrigin);
            if (axes == null) return false;

            double Get(string tag) => axes.TryGetValue(AxisKey(tag), out double v) ? v : 0.0;
            origin = new AxisPoint(Get("X"), Get("Y"), Get("Z"));
            return true;
        }

        public bool Write(AxisPoint origin, out string message)
        {
            var recipe = _mainVM.RecipeVM;
            if (recipe == null) { message = "레시피 화면이 아직 준비되지 않았습니다."; return false; }

            // X·Y 만 넘긴다 — 여기 없는 축은 SetPointAxes 가 건드리지 않는다.
            var axes = new Dictionary<string, double>
            {
                [AxisKey("X")] = origin.X,
                [AxisKey("Y")] = origin.Y,
            };

            bool ok = recipe.SetPointAxes(PointNames.PrintOrigin, axes, out message);
            _mainVM.AddLog(
                ok ? $"[PRINT] 인쇄 원점 → {message}"
                   : $"[PRINT] 인쇄 원점 저장 실패: {message}",
                ok ? LogLevel.Success : LogLevel.Error);
            return ok;
        }
    }
}
