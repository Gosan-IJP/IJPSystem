using System;
using System.Collections.Generic;
using System.Linq;

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    /// <summary>
    /// 헤드 없이 PCC-E 화면을 확인하기 위한 가상 상태.
    ///
    /// <para><b>실패했을 때 여기로 떨어지지 않는다.</b> 오직
    /// <c>AppConfig.json</c> 의 <c>DriverMode.Head = "Virtual"</c> 일 때만 선택된다.
    /// 네이티브 DLL 이 없어서 이쪽으로 넘어오는 경로를 만들면, 제어PC 에서
    /// Meteor 설치가 깨졌을 때 화면이 초록불에 ST_RUNNING 을 띄운다.</para>
    ///
    /// <para>값은 폴링마다 <b>움직인다</b>. 정지된 숫자면 화면이 갱신되고 있는지
    /// 여전히 알 수 없다 — 카운터가 도는 것이 곧 폴링→화면 경로가 살아 있다는 증거다.</para>
    ///
    /// <para>상황을 고를 수 있게 한 이유: 폴트 표시가 제대로 풀리는지, 주소를 못 받았을 때
    /// 안내가 맞게 뜨는지는 <b>정상 상태만 보면 영영 확인되지 않는다</b>.</para>
    /// </summary>
    public sealed class VirtualMeteorStatusMonitor : IMeteorStatusSource
    {
        public const string Normal        = "정상";
        public const string NotAttached   = "PCC 미부착";
        public const string Fault         = "폴트";
        public const string TransferError = "전송 오류";

        private static readonly string[] All = { Normal, NotAttached, Fault, TransferError };

        private readonly object _gate = new();
        private int _tick;

        public IReadOnlyList<string> Scenarios => All;

        private string _scenario = Normal;
        public string Scenario
        {
            get => _scenario;
            set
            {
                lock (_gate)
                    _scenario = All.Contains(value) ? value : Normal;
            }
        }

        public MeteorHeadStatus Poll()
        {
            string scenario;
            int t;
            lock (_gate) { scenario = _scenario; t = ++_tick; }

            return scenario switch
            {
                NotAttached   => Detached(),
                Fault         => Build(t, faultRegister: 0x00000008, transferError: false, headFault: true),
                TransferError => Build(t, faultRegister: 0,          transferError: true,  headFault: false),
                _             => Build(t, faultRegister: 0,          transferError: false, headFault: false),
            };
        }

        /// <summary>PCC 가 안 붙은 상태 — IP 를 못 받았을 때 화면이 어떻게 보이는지.</summary>
        private static MeteorHeadStatus Detached() => new()
        {
            IsSimulated  = true,
            Connected    = false,
            Reachable    = true,
            PccsAttached = 0,
            PccsRequired = 1,
            PccsPresent  = "",
            PrinterState = "MPS_DISCONNECTED",
            HeadPower    = "HPS_OFF",
            Detail       = "[가상] PCC 미부착 0/1 — DHCP 서버·어댑터 이름 확인",
        };

        private static MeteorHeadStatus Build(int tick, uint faultRegister, bool transferError, bool headFault)
        {
            // 카운터는 폴링마다 는다. 인코더는 이동거리라 크게, PD 는 가끔.
            int pd    = tick / 8;
            int print = tick / 8;

            var pcc = new MeteorPccStatus
            {
                Number              = 1,
                StatusBits          = 0x22F00A00,
                StatusBits2         = transferError ? 1u : 0u,
                FaultRegister       = faultRegister,
                PdCount             = pd,
                PrintCount          = print,
                EncoderCount        = 6_441_230 + tick * 137,
                AbsXCount           = tick * 42,
                DataTransferError   = transferError,
                HeadPowerInProgress = false,
                FwVersion           = 0xED5ACBDC,
                FpgaVersion         = 0x00047033,
                IpAddress           = "192.168.2.10",
                MaxHdcs             = 2,
            };

            var hdcs = new List<MeteorHdcStatus>();
            for (int i = 1; i <= 2; i++)
            {
                hdcs.Add(new MeteorHdcStatus
                {
                    PccNumber              = 1,
                    HeadNumber             = i,
                    State                  = headFault && i == 1 ? "ST_FAULT" : "ST_RUNNING",
                    StatusBits             = 0x00000000,
                    StatusBits2            = 0x00000000,
                    PreloadDataUsedPercent = (tick * 3 + i * 17) % 100,
                    FifoDataUsedPercent    = (tick * 7 + i * 31) % 100,
                    DdramDwordsA           = tick * 64,
                    DdramDwordsB           = tick * 64,
                    HeadDwordsA            = tick * 512,
                    HeadDwordsB            = tick * 512,
                    ImagesQueuedA          = tick % 4,
                    DocsPrintedA           = print,
                });
            }

            bool bad = faultRegister != 0 || headFault;

            return new MeteorHeadStatus
            {
                IsSimulated     = true,
                Connected       = true,
                Reachable       = true,
                PccsAttached    = 1,
                PccsRequired    = 1,
                PccsPresent     = "1",
                PrinterState    = bad ? "MPS_FAULT" : "MPS_IDLE",
                HeadPower       = "HPS_ON",
                PdCount         = pd,
                PrintCount      = print,
                PrintSpeed      = 1000,
                PdCount2        = 0,
                PrintCount2     = 0,
                PreloadDocsSent = print,
                FifoDocsSent    = 0,
                DocsQueuedLane1 = tick % 3,
                DocsQueuedLane2 = 0,
                DocCount        = print,
                JobCount        = 1 + tick / 64,
                Pccs            = new[] { pcc },
                Hdcs            = hdcs,
                Detail          = bad
                    ? "[가상] PCC 1/1 · MPS_FAULT — 폴트 표시 확인용"
                    : "[가상] PCC 1/1 · MPS_IDLE · HeadPower HPS_ON",
            };
        }

        public void Dispose() { }
    }
}
