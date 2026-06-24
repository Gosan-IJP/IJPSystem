using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Motion;

namespace IJPSystem.Machines.Inkjet5G
{
    // 메인: 공통 골격(IO/Motion/Vision, 생성자, 수명 주기)
    // 도메인별 IO 상수와 메서드는 Partials/InkjetMachine.{Chuck|Door|Fluid|Lamp|Safety}.cs 에 위치
    public partial class InkjetMachine : IMachine
    {
        public IIODriver IO { get; }
        public IMotionDriver Motion { get; }
        public IVisionDriver Vision { get; }
        public string MachineName => "INKJET SYSTEM";
        public MotionAxisRoot Config { get; set; } = new MotionAxisRoot();

        // 도메인별 partial이 자기 영역의 IO tag 상수를 채워 넣음
        private static partial class DI { }
        private static partial class DO { }

        public InkjetMachine(IIODriver ioDriver, IMotionDriver motionDriver, IVisionDriver visionDriver)
        {
            IO     = ioDriver;
            Motion = motionDriver;
            Vision = visionDriver;
        }

        public void Initialize()
        {
            IO.Connect();
            Motion.Connect();
            Vision.Connect();
        }

        public void Terminate()
        {
            IO.Disconnect();
            Motion.Disconnect();
            Vision.Disconnect();
        }
    }
}
