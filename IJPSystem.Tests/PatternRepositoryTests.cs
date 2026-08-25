using System;
using System.IO;
using System.Linq;
using IJPSystem.Platform.Infrastructure.Vision;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 정렬 패턴 저장소.
    ///
    /// <para>정의(json)와 이미지(png)가 <b>한 벌</b>이라는 것이 핵심이다. 한쪽만 남으면
    /// 화면에는 패턴이 있는 것처럼 보이는데 찾기가 안 된다.</para>
    /// </summary>
    public class PatternRepositoryTests : IDisposable
    {
        private readonly string _dir;
        private readonly PatternRepository _repo;

        public PatternRepositoryTests()
        {
            _dir  = Path.Combine(Path.GetTempPath(), "ijp_pat_" + Guid.NewGuid().ToString("N"));
            _repo = new PatternRepository(_dir);
        }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static GrayImage Template(int w = 40, int h = 30, byte seed = 7)
        {
            var px = new byte[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = (byte)((i * 37 + seed) % 251);
            return new GrayImage(px, w, h);
        }

        private static PatternDefinition Def(string name = "GlassMark") => new()
        {
            Name        = name,
            ReferenceX  = 640.5,
            ReferenceY  = 512.25,
            SceneWidth  = 1280,
            SceneHeight = 1024,
            MinScore    = 0.82,
            SearchRadiusPx = 120,
        };

        [Fact]
        public void 저장한_것을_그대로_읽는다()
        {
            var templ = Template();
            _repo.Save(Def(), templ);

            var e = _repo.Load("GlassMark");

            Assert.NotNull(e);
            Assert.Equal(0.82, e!.Definition.MinScore, 3);
            Assert.Equal(120,  e.Definition.SearchRadiusPx);
            Assert.Equal(templ.Width,  e.Template.Width);
            Assert.Equal(templ.Height, e.Template.Height);
        }

        [Fact]
        public void 이미지가_한_픽셀도_변하지_않는다()
        {
            // 손실 압축으로 저장하면 등록 당시와 다른 그림으로 찾게 된다.
            var templ = Template();
            _repo.Save(Def(), templ);

            var e = _repo.Load("GlassMark");

            Assert.Equal(templ.Pixels, e!.Template.Pixels);
        }

        [Fact]
        public void 기준_좌표는_소수점까지_남는다()
        {
            // 서브픽셀로 구한 기준을 정수로 깎으면 정렬 오차가 그만큼 붙는다.
            _repo.Save(Def(), Template());

            var d = _repo.Load("GlassMark")!.Definition;

            Assert.Equal(640.5,   d.ReferenceX, 4);
            Assert.Equal(512.25,  d.ReferenceY, 4);
        }

        [Fact]
        public void 패턴_크기는_이미지에서_받아_적는다()
        {
            // 정의에 적힌 크기와 실제 png 가 다르면 찾기 좌표가 어긋난다.
            var def = Def();
            def.TemplateWidth = def.TemplateHeight = 999;

            _repo.Save(def, Template(40, 30));

            var d = _repo.Load("GlassMark")!.Definition;
            Assert.Equal(40, d.TemplateWidth);
            Assert.Equal(30, d.TemplateHeight);
        }

        [Fact]
        public void 없는_이름은_null()
            => Assert.Null(_repo.Load("없는패턴"));

        [Fact]
        public void 이미지만_남으면_없는_것으로_본다()
        {
            // 한 벌이 아니면 화면에 있는 척하면 안 된다.
            _repo.Save(Def(), Template());
            File.Delete(_repo.BasePathOf("GlassMark") + ".json");

            Assert.Null(_repo.Load("GlassMark"));
        }

        [Fact]
        public void 목록에_이름이_나온다()
        {
            _repo.Save(Def("A"), Template());
            _repo.Save(Def("B"), Template());

            Assert.Equal(new[] { "A", "B" }, _repo.List().ToArray());
        }

        [Fact]
        public void 덮어써도_한_벌을_유지한다()
        {
            _repo.Save(Def(), Template(40, 30));
            _repo.Save(Def(), Template(24, 24, seed: 99));

            var e = _repo.Load("GlassMark");

            Assert.Equal(24, e!.Template.Width);
            Assert.Equal(24, e.Definition.TemplateWidth);
        }

        [Fact]
        public void 임시파일을_남기지_않는다()
        {
            _repo.Save(Def(), Template());

            Assert.False(File.Exists(_repo.BasePathOf("GlassMark") + ".png.tmp"));
        }

        [Fact]
        public void 지우면_두_파일이_다_없어진다()
        {
            _repo.Save(Def(), Template());
            _repo.Remove("GlassMark");

            Assert.Empty(_repo.List());
            Assert.False(File.Exists(_repo.BasePathOf("GlassMark") + ".png"));
        }

        [Fact]
        public void 파일명에_못_쓰는_문자를_걷어낸다()
        {
            Assert.DoesNotContain('/', PatternRepository.SanitizeName("glass/mark"));
            Assert.Equal("pattern", PatternRepository.SanitizeName("   "));
        }

        [Fact]
        public void 해상도가_다르면_알려_준다()
        {
            // 등록 때와 화면 크기가 다르면 기준 좌표를 믿을 수 없다.
            var d = Def();

            Assert.True(d.MatchesScene(1280, 1024));
            Assert.False(d.MatchesScene(640, 512));
        }

        // ── 해상도 차이 판정 ──────────────────────────────────────────────
        //
        // 매칭 자체는 해상도와 무관하다 — 템플릿을 장면 위로 훑을 뿐이다.
        // 흔들리는 것은 기준 좌표뿐이라, 조금 다르다고 막을 이유가 없다(2026-08-25).

        [Fact]
        public void 같은_해상도면_아무_말도_하지_않는다()
        {
            var c = Def().CheckScene(1280, 1024);

            Assert.Equal(SceneFit.Same, c.Fit);
            Assert.True(c.CanFind);
            Assert.Equal("", c.Message);
        }

        [Fact]
        public void 조금_다르면_막지_않고_오차를_알린다()
        {
            // 1280×1024 → 1282×1028 (0.16% / 0.39%)
            var c = Def().CheckScene(1282, 1028);

            Assert.Equal(SceneFit.Close, c.Fit);
            Assert.True(c.CanFind);                    // 찾기는 진행한다
            Assert.True(c.MaxRefErrorPx > 0);
            Assert.Contains("오차", c.Message);
        }

        [Fact]
        public void 크게_다르면_막는다()
        {
            var c = Def().CheckScene(640, 512);

            Assert.Equal(SceneFit.Different, c.Fit);
            Assert.False(c.CanFind);
            Assert.Contains("다시 등록", c.Message);
        }

        [Theory]
        [InlineData(1280, 1024, SceneFit.Same)]
        [InlineData(1305, 1024, SceneFit.Close)]        // 1.95% — 경계 안
        [InlineData(1306, 1024, SceneFit.Different)]    // 2.03% — 경계 밖
        [InlineData(1280, 1045, SceneFit.Different)]    // 세로만 2.05%
        public void 허용_폭은_2퍼센트다(int w, int h, SceneFit expect)
            => Assert.Equal(expect, Def().CheckScene(w, h).Fit);

        [Fact]
        public void 오차는_나쁜_쪽_해석으로_잡는다()
        {
            // 배율이 달라진 것인지 가장자리를 더 잘라낸 것인지 알 수 없다.
            // 어느 쪽인지 모르니 큰 쪽을 말해야 한다.
            //   ① 배율: 640.5 × (1282/1280 - 1) = 1.0px
            //   ② 자름: |1282 - 1280|            = 2px
            var c = Def().CheckScene(1282, 1024);

            Assert.Equal(2.0, c.MaxRefErrorPx, 6);
        }

        [Fact]
        public void 등록_해상도가_없으면_따지지_않는다()
        {
            // 옛 패턴 파일에는 해상도가 없다 — 그것 때문에 못 쓰게 되면 안 된다.
            var d = Def();
            d.SceneWidth = d.SceneHeight = 0;

            Assert.Equal(SceneFit.Same, d.CheckScene(999, 111).Fit);
        }

        [Fact]
        public void 저장한_패턴을_실제로_찾을_수_있다()
        {
            // 저장 → 읽기 → 매칭까지 한 번에. 어느 단계에서 어긋나도 여기서 걸린다.
            const int W = 200, H = 160;
            var rnd = new Random(3);
            var scene = new byte[W * H];
            for (int i = 0; i < scene.Length; i++) scene[i] = (byte)(80 + rnd.Next(0, 30));
            for (int y = 60; y < 90; y++)
                for (int x = 70; x < 110; x++)
                    scene[y * W + x] = (byte)(x < 90 ? 240 : 15);

            var sceneImg = new GrayImage(scene, W, H);
            var templ    = sceneImg.Crop(65, 55, 50, 40);

            _repo.Save(new PatternDefinition { Name = "M", ReferenceX = 89.5, ReferenceY = 74.5 }, templ);
            var loaded = _repo.Load("M")!;

            var m = PatternMatcher.Find(sceneImg, loaded.Template);

            Assert.True(m.Found);
            Assert.Equal(loaded.Definition.ReferenceX, m.CenterX, 1);
            Assert.Equal(loaded.Definition.ReferenceY, m.CenterY, 1);
        }
    }
}
