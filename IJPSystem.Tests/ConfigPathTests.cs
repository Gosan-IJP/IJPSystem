using System;
using System.IO;
using IJPSystem.Platform.Common.Utilities;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 설정에 적힌 경로 풀기.
    ///
    /// <para>Meteor 설정 파일은 <b>우리가 만드는 파일이 아니다</b> — 이름도 헤드마다 다르고
    /// 제어 PC 에서는 Meteor 설치 폴더에 있다. 그래서 절대 경로를 그대로 가리킬 수 있어야 하고,
    /// 상대 경로는 Config 폴더 기준이어야 한다.</para>
    /// </summary>
    public class ConfigPathTests
    {
        [Fact]
        public void 비어_있으면_예전_파일명을_찾는다()
        {
            string p = PathUtils.ResolveConfigPath("", "PrintEngine.cfg");

            Assert.EndsWith(Path.Combine("Config", "PrintEngine.cfg"), p);
        }

        [Fact]
        public void 공백만_있어도_비어_있는_것으로_본다()
            => Assert.Equal(PathUtils.ResolveConfigPath(null, "PrintEngine.cfg"),
                            PathUtils.ResolveConfigPath("   ", "PrintEngine.cfg"));

        [Fact]
        public void 절대_경로는_그대로_쓴다()
        {
            // 제어 PC 는 Meteor 설치 폴더를 그대로 가리킨다.
            const string abs = @"C:\Users\Public\Documents\Meteor\Config\PccE\DefaultEpsonS3200_PccE.cfg";

            Assert.Equal(abs, PathUtils.ResolveConfigPath(abs, "PrintEngine.cfg"));
        }

        [Fact]
        public void 상대_경로는_Config_폴더_기준이다()
        {
            string p = PathUtils.ResolveConfigPath("PccE/DefaultEpsonS3200_PccE.cfg", "PrintEngine.cfg");

            Assert.EndsWith(Path.Combine("Config", "PccE", "DefaultEpsonS3200_PccE.cfg"), p);
        }

        [Theory]
        [InlineData(@"Config\PccE\x.cfg")]
        [InlineData("Config/PccE/x.cfg")]
        public void Config_를_적어도_두_번_붙지_않는다(string configured)
        {
            // "Config\Config\PccE\x.cfg" 가 되면 파일을 못 찾고, 화면에는 그럴듯한 경로가 뜬다.
            string p = PathUtils.ResolveConfigPath(configured, "PrintEngine.cfg");

            Assert.EndsWith(Path.Combine("Config", "PccE", "x.cfg"), p);
            Assert.DoesNotContain(Path.Combine("Config", "Config"), p);
        }
    }
}
