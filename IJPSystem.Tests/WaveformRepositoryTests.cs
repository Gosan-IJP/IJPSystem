using System;
using System.IO;
using System.Linq;
using IJPSystem.Platform.Infrastructure.Print.Waveform;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 파형 저장소 — 목록 · 가져오기 · 삭제 · 이름 변경 · 기본값.
    ///
    /// <para>여기서 지키려는 것은 하나다: <b>파형은 파일 묶음(.ComA/.ComB/.Vst)</b>이라
    /// 모든 조작이 묶음 전체에 걸려야 한다. ComA 만 옮기면 목록에는 그대로 보이는데
    /// 로드할 때가 되어서야 짝이 없다는 걸 알게 된다.</para>
    /// </summary>
    public class WaveformRepositoryTests : IDisposable
    {
        private readonly string _dir;

        public WaveformRepositoryTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ijp_wf_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string Make(string name, params string[] exts)
        {
            foreach (string ext in exts)
                File.WriteAllText(Path.Combine(_dir, name + ext), $"{name}{ext}");
            return Path.Combine(_dir, name + exts[0]);
        }

        private WaveformRepository Repo() => new(_dir);

        [Fact]
        public void List_ComA_가_있는_것만_한_항목으로_센다()
        {
            Make("A", ".ComA", ".ComB");
            Make("B", ".ComA");
            Make("혼자ComB", ".ComB");          // 대표 파일이 없으면 목록에 안 나온다

            var list = Repo().List();

            Assert.Equal(new[] { "A", "B" }, list.Select(e => e.Name));
            Assert.True(list[0].HasComB);
            Assert.False(list[1].HasComB);
            Assert.Equal(new[] { 1, 2 }, list.Select(e => e.Index));
        }

        [Fact]
        public void BasePath_는_확장자가_없다_레시피에_적는_형식과_같다()
        {
            Make("PULSE_Small", ".ComA");

            var e = Repo().List().Single();

            Assert.Equal(Path.Combine(_dir, "PULSE_Small"), e.BasePath);
            Assert.True(File.Exists(e.BasePath + ".ComA"));
        }

        [Fact]
        public void Import_는_고른_파일이_ComB_여도_짝을_모두_가져온다()
        {
            string src = Path.Combine(Path.GetTempPath(), "ijp_src_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(src);
            try
            {
                File.WriteAllText(Path.Combine(src, "밖에있는.ComA"), "a");
                File.WriteAllText(Path.Combine(src, "밖에있는.ComB"), "b");

                var e = Repo().Import(Path.Combine(src, "밖에있는.ComB"));

                Assert.Equal("밖에있는", e.Name);
                Assert.True(e.HasComB);
                Assert.True(File.Exists(Path.Combine(_dir, "밖에있는.ComA")));
                Assert.True(File.Exists(Path.Combine(_dir, "밖에있는.ComB")));
            }
            finally { Directory.Delete(src, recursive: true); }
        }

        [Fact]
        public void Import_는_같은_이름이_있으면_덮어쓰지_않고_새_이름을_만든다()
        {
            Make("겹침", ".ComA");

            string src = Path.Combine(Path.GetTempPath(), "ijp_src_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(src);
            try
            {
                File.WriteAllText(Path.Combine(src, "겹침.ComA"), "새 내용");

                var e = Repo().Import(Path.Combine(src, "겹침.ComA"));

                Assert.Equal("겹침_2", e.Name);
                Assert.Equal("겹침.ComA", File.ReadAllText(Path.Combine(_dir, "겹침.ComA")));  // 원본 그대로
            }
            finally { Directory.Delete(src, recursive: true); }
        }

        [Fact]
        public void Rename_은_묶음_전체를_옮긴다()
        {
            Make("옛이름", ".ComA", ".ComB", ".Vst");
            var repo = Repo();

            var e = repo.Rename(repo.List().Single(), "새이름");

            Assert.Equal("새이름", e.Name);
            foreach (string ext in WaveformRepository.Extensions)
            {
                Assert.True(File.Exists(Path.Combine(_dir, "새이름" + ext)), ext + " 가 안 옮겨졌다");
                Assert.False(File.Exists(Path.Combine(_dir, "옛이름" + ext)), ext + " 가 남았다");
            }
        }

        [Fact]
        public void Rename_은_짝_하나라도_겹치면_아무것도_바꾸지_않는다()
        {
            Make("원본", ".ComA", ".ComB");
            Make("이미있음", ".ComB");            // ComA 는 없지만 ComB 가 이미 있다
            var repo = Repo();
            var src = repo.List().Single(e => e.Name == "원본");

            Assert.Throws<IOException>(() => repo.Rename(src, "이미있음"));

            Assert.True(File.Exists(Path.Combine(_dir, "원본.ComA")));
            Assert.True(File.Exists(Path.Combine(_dir, "원본.ComB")));
            Assert.Equal("이미있음.ComB", File.ReadAllText(Path.Combine(_dir, "이미있음.ComB")));
        }

        [Fact]
        public void Remove_는_묶음_전체를_지운다()
        {
            Make("지울것", ".ComA", ".ComB", ".Vst");
            Make("남을것", ".ComA");
            var repo = Repo();

            repo.Remove(repo.List().Single(e => e.Name == "지울것"));

            Assert.Empty(Directory.EnumerateFiles(_dir, "지울것.*"));
            Assert.True(File.Exists(Path.Combine(_dir, "남을것.ComA")));
        }

        [Fact]
        public void MakeDefault_는_표시가_남고_목록에_반영된다()
        {
            Make("가", ".ComA");
            Make("나", ".ComA");
            var repo = Repo();

            repo.MakeDefault(repo.List().Single(e => e.Name == "나"));

            var list = repo.List();
            Assert.False(list.Single(e => e.Name == "가").IsDefault);
            Assert.True(list.Single(e => e.Name == "나").IsDefault);
            Assert.Equal("나", repo.GetDefault()!.Name);
        }

        [Fact]
        public void 기본값을_이름_변경하면_표시도_따라간다()
        {
            Make("기본", ".ComA");
            var repo = Repo();
            repo.MakeDefault(repo.List().Single());

            var e = repo.Rename(repo.List().Single(), "바뀐기본");

            Assert.True(e.IsDefault);
            Assert.Equal("바뀐기본", repo.GetDefault()!.Name);
        }

        [Fact]
        public void 기본값을_지우면_표시도_사라진다()
        {
            Make("기본", ".ComA");
            Make("남는것", ".ComA");
            var repo = Repo();
            repo.MakeDefault(repo.List().Single(e => e.Name == "기본"));

            repo.Remove(repo.List().Single(e => e.Name == "기본"));

            Assert.Null(repo.GetDefault());
            Assert.False(repo.List().Single().IsDefault);
        }

        [Fact]
        public void 기본값_표시_파일은_파형으로_세지_않는다()
        {
            Make("하나", ".ComA");
            var repo = Repo();
            repo.MakeDefault(repo.List().Single());

            Assert.Single(repo.List());
        }

        [Theory]
        [InlineData("이름.ComA", "이름")]
        [InlineData("이름.ComB", "이름")]
        [InlineData("26.06.30_test1.ComA", "26.06.30_test1")]   // 이름에 점이 들어간다
        [InlineData("확장자없음", "확장자없음")]
        public void BaseNameOf_는_파형_확장자만_떼어낸다(string fileName, string expected)
            => Assert.Equal(expected, WaveformRepository.BaseNameOf(fileName));

        [Fact]
        public void SanitizeName_은_파일명에_못_쓰는_문자를_바꾼다()
        {
            Assert.Equal("a_b", WaveformRepository.SanitizeName("a/b"));
            Assert.Equal("Untitled", WaveformRepository.SanitizeName("   "));
        }
    }
}
