using System;
using System.Runtime.InteropServices;
using Ttp.Meteor;   // PrinterInterfaceCLS(정적), eRET, ImageBufferAllocParams

namespace IJPSystem.Platform.Infrastructure.Print.Meteor
{
    /// <summary>패킹 결과 — 엔진 버퍼에 그대로 들어갈 DWORD 배열과 행 간격.</summary>
    /// <param name="Data">버퍼 내용.</param>
    /// <param name="RowDwords">한 행(= 한 스텝)이 차지하는 DWORD 수. 행은 DWORD 경계에서 시작한다.</param>
    public readonly record struct MeteorImageData(uint[] Data, int RowDwords);

    /// <summary>
    /// 발사 지도 ↔ Meteor 이미지 버퍼 형식 변환.
    ///
    /// <para>전송에서 <b>떼어 놓은 이유</b>: 이 변환이 틀리면 통신 오류가 아니라 <b>그림이 틀리게</b>
    /// 나온다. 장비 없이 확인할 수 있어야 하는 부분이라 순수 함수로 갈라 둔다.</para>
    /// </summary>
    public static class MeteorImageBuffer
    {
        /// <summary>
        /// 비트뎁스로 쓸 수 있는 값인가.
        ///
        /// <para>SDK 매뉴얼(10.6 PCMD_IMAGE)이 <b>1·2·4 뿐</b>이라고 못박는다 — 이진 헤드가 1bpp,
        /// 그레이스케일 헤드가 2 또는 4bpp(4단계/16단계)다. <b>8bpp 는 없다.</b>
        /// S800 cfg 도 <c>BitsPerPixel = 2 ; BPP (1,2)</c> 로 적혀 있다.</para>
        /// </summary>
        public static bool IsSupportedBpp(int bpp) => bpp is 1 or 2 or 4;

        /// <summary>한 행이 차지하는 DWORD 수 — 행은 DWORD 경계에서 시작한다.</summary>
        public static int RowDwordsFor(int width, int bpp)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (!IsSupportedBpp(bpp)) throw new ArgumentOutOfRangeException(nameof(bpp));
            int perDword = 32 / bpp;
            return (width + perDword - 1) / perDword;
        }

        /// <summary>
        /// 발사 지도를 엔진 버퍼 형식(DWORD 배열)으로 packing.
        ///
        /// <para><b>규약</b> (SDK 매뉴얼 Rev C2.19 §10.6 PCMD_IMAGE — 이미지 버퍼도 같은
        /// 번역기 경로라 데이터 형식이 같다):
        ///   · 각 DWORD 의 <b>최상위 비트가 가장 왼쪽 화소</b>다(MSB first).
        ///   · 값은 행 순서로, 이미지 좌상단부터.
        ///   · 각 행은 오른쪽을 0 으로 채워 정수 개의 DWORD 로 맞춘다. 남는 비트는 무시되고,
        ///     실제 인쇄 폭은 명령의 Width 가 정한다.
        ///   · 행 길이[DWORD] = (폭 / DWORD당 화소 수) 올림.
        /// </para>
        ///
        /// <para><b>주의 — LSB first 가 아니다.</b> 헤드로 직접 보내는 <c>PCMD_HDIMAGE</c> 는
        /// "첫 노즐이 LSB" 라 규약이 <b>반대</b>인데, 우리가 쓰는 이미지 버퍼는 번역기를 거치는
        /// <c>PCMD_IMAGE</c> 계열이라 MSB first 다. 둘을 섞으면 에러 없이 그림이 좌우로 뒤집힌다.
        /// (매뉴얼 예: 1bpp 1×2 화소를 둘 다 켜면 데이터가 <c>0x80000000, 0x80000000</c>)</para>
        /// </summary>
        /// <param name="levels">[스텝, 노즐] 방울 단계.</param>
        /// <param name="bpp">화소 비트뎁스(1·2·4·8).</param>
        public static MeteorImageData Pack(byte[,] levels, int bpp)
        {
            if (levels == null) throw new ArgumentNullException(nameof(levels));
            int height = levels.GetLength(0), width = levels.GetLength(1);
            if (width <= 0 || height <= 0)
                throw new ArgumentException($"빈 패턴입니다({height}스텝 × {width}노즐).", nameof(levels));

            int rowDwords = RowDwordsFor(width, bpp);
            int perDword  = 32 / bpp;
            uint mask     = (uint)((1 << bpp) - 1);

            var data = new uint[(long)rowDwords * height];
            for (int s = 0; s < height; s++)
            {
                int rowBase = s * rowDwords;
                for (int c = 0; c < width; c++)
                {
                    uint v = levels[s, c];
                    if (v == 0) continue;
                    if (v > mask)
                        throw new ArgumentException(
                            $"방울 단계 {v}({s},{c}) 가 {bpp}bpp 상한 {mask} 을 넘습니다.", nameof(levels));
                    // MSB first — 첫 화소가 최상위 비트. 1bpp 첫 화소면 shift 31 → 0x80000000.
                    int shift = 32 - bpp - (c % perDword * bpp);
                    data[rowBase + c / perDword] |= v << shift;
                }
            }
            return new MeteorImageData(data, rowDwords);
        }
    }

    /// <summary>
    /// 발사 지도를 Meteor PrintEngine 의 <b>이미지 버퍼</b>로 올린다.
    /// <see cref="NullPrintDataDownloader"/> 자리에 들어가는 실제 어댑터.
    ///
    /// <para><b>전송 절차</b> (PrinterInterfaceCLS.xml 근거):
    ///   ① <c>PiAllocateImageBufferEx</c> — 크기·비트뎁스를 주고 버퍼를 얻는다(ID 가 구조체로 돌아온다).
    ///   ② <c>PiFillImageBuffer</c> — 패킹한 DWORD 배열을 밀어 넣는다.
    ///   ③ 인쇄는 <c>PCMD_IMAGE_BUFFER</c> 명령으로 — <b>이 클래스가 하지 않는다</b>(아래 참조).
    ///   ④ <c>PiSynchronousImageBufferFree</c> — 반납. 버퍼 수명은 앱 책임이다.
    /// </para>
    ///
    /// <para><b>엔진을 열지 않는다.</b> Meteor 는 한 프로세스만 프린터를 소유할 수 있어
    /// <c>PiStartPrintEngine</c>/<c>PiOpenPrinter</c> 는 <see cref="Devices.DropWatcher.MeteorStatusMonitor"/>
    /// 가 이미 하고 있다. 여기서 또 열면 서로 뺏는다. 버퍼 API 는 "엔진을 호스팅 중인 프로세스"
    /// 이기만 하면 되므로, 이 클래스는 연결 여부만 확인하고 버퍼만 다룬다.</para>
    ///
    /// <para><b>인쇄 명령은 여기 없다.</b> 업로드(이 클래스)와 발사(PCMD_IMAGE_BUFFER)는 분리돼 있다.
    /// <see cref="PrintJobController.BeginPrint"/> 가 그 자리이며, 인쇄는 버퍼 하나만으로 되지 않는다 —
    /// PCMD_STARTJOB → STARTPDOC/STARTFDOC → IMAGE_BUFFER → ENDDOC → ENDJOB 의 job/document
    /// 시퀀스가 필요하다. 그건 이 클래스의 일이 아니다.</para>
    ///
    /// <para><b>명령 배치</b>(PrinterInterfaceCLS.chm, CtrlCmdIds 문서 — 2026-09-08 확보):
    /// <code>
    ///   Cmd[0] = PCMD_IMAGE_BUFFER
    ///   Cmd[1] = 뒤따르는 DWORD 수 (5, y 인터레이스면 7)
    ///   Cmd[2] = Plane (1~MAX_PLANES). 최상위 비트를 세우면 인쇄 후 버퍼 해제.
    ///            Plane = 0 이면 인쇄 없이 해제만(이때 XLeft·YTop·Width 는 무시).
    ///   Cmd[3] = XLeft [화소] (스캔/연속 인쇄에서는 XStart)
    ///   Cmd[4] = YTop  [화소]
    ///   Cmd[5] = Width [화소]. 파일에서 적재한 버퍼면 0(파일 폭을 쓴다).
    ///   Cmd[6] = 이미지 버퍼 ID
    ///   Cmd[7] = (선택) Y 인터레이스 N — N 줄마다 1줄만 인쇄
    ///   Cmd[8] = (선택) 인터레이스 오프셋 0~N-1
    /// </code>
    /// ※ Plane 최상위 비트(<c>PCMD_BLOCK_FLAG</c>)로 해제하는 길이 있으므로, 한 번만 쓸
    ///   버퍼라면 <see cref="Release"/> 를 따로 부르지 않아도 된다.
    /// </para>
    /// </summary>
    public sealed class MeteorImageBufferDownloader : IPrintDataDownloader
    {
        /// <summary>버퍼 없음. (MeteorConsts.IMG_BUF_UNSET 과 같은 값)</summary>
        private const uint NoBuffer = 0xFFFFFFFF;

        private readonly Action<string>? _log;
        private readonly object _io = new();

        private uint _bufferId = NoBuffer;

        public MeteorImageBufferDownloader(Action<string>? log = null) => _log = log;

        public string Name => "Meteor 이미지 버퍼";

        /// <summary>
        /// 엔진 프로세스에 붙어 있는가. 버퍼 API 는 엔진을 호스팅해야만 쓸 수 있어
        /// (미호스팅이면 <c>RVAL_NOT_HOSTING</c>), 여기서 미리 거른다.
        /// </summary>
        public bool IsReady
        {
            get { try { return PrinterInterfaceCLS.PiIsProcessConnected(); } catch { return false; } }
        }

        /// <summary>
        /// 못 보내는 이유. <b>헤드 장착·전원과는 무관하다</b> — 버퍼는 PC 메모리에 잡히므로
        /// PCC 가 붙지 않아도, 헤드가 없어도 올라간다. 막히는 곳은 엔진 프로세스 연결 하나다.
        /// </summary>
        public string? NotReadyReason => IsReady
            ? null
            : "Meteor PrintEngine 프로세스에 연결되지 않았습니다. " +
              "PCC-E 화면에서 [엔진 시작] 을 누르세요 — 이미 떠 있는데도 안 되면 " +
              "다른 앱(LabVIEW·Meteor 도구)이 프린터를 점유한 것입니다. " +
              "(헤드 장착·전원과는 무관합니다 — 버퍼는 PC 메모리에 올라갑니다)";

        /// <summary>지금 올라가 있는 버퍼 ID. 없으면 null — 화면·검사에서 확인용.</summary>
        public uint? BufferId => _bufferId == NoBuffer ? null : _bufferId;

        private int _lastDwords;

        /// <summary>
        /// 엔진 로그와 <b>글자 그대로 대조</b>할 수 있는 값.
        ///
        /// <para>엔진은 자기 로그에 <c>Allocated image buffer DWORDs=17725 ID=2</c> 처럼 남긴다.
        /// 화면에 같은 두 숫자를 띄워 두면, 방금 누른 것이 그 줄이 맞는지 눈으로 확인된다.
        /// 스텝·노즐 수는 <b>읽은 파일</b>을 말할 뿐이라 이 확인을 대신하지 못한다.</para>
        /// </summary>
        public string? LastTransferDetail => _bufferId == NoBuffer
            ? null
            : $"버퍼 #{_bufferId} · {_lastDwords:N0} DWORD";

        public void Download(PrintJob job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));

            int width  = job.Nozzles;   // 크로스스캔 = 노즐
            int height = job.Steps;     // 스캔 진행 = 스텝
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException($"빈 패턴입니다({height}스텝 × {width}노즐).");

            int bpp = ResolveBitsPerPixel(job);
            var packed = MeteorImageBuffer.Pack(job.Pattern.Levels, bpp);
            uint[] data = packed.Data;

            lock (_io)
            {
                Release();   // 앞의 것을 반납하지 않으면 엔진 메모리가 계속 쌓인다

                var p = new ImageBufferAllocParams
                {
                    StructureSizeBytes = (uint)Marshal.SizeOf<ImageBufferAllocParams>(),
                    ImageBufferID      = NoBuffer,
                    SizeDwords         = (uint)data.Length,
                    BitsPerPixel       = (uint)bpp,
                    WidthPixels        = (uint)width,
                    HeightPixels       = (uint)height,
                };

                Check(PrinterInterfaceCLS.PiAllocateImageBufferEx(ref p), "PiAllocateImageBufferEx");
                if (p.ImageBufferID == NoBuffer)
                    throw new InvalidOperationException("버퍼는 할당됐다는데 ID 가 비어 있습니다.");

                _bufferId = p.ImageBufferID;

                try
                {
                    Check(PrinterInterfaceCLS.PiFillImageBuffer(_bufferId, 0, (uint)data.Length, data),
                          "PiFillImageBuffer");
                }
                catch
                {
                    // 채우다 실패하면 버퍼가 엔진에 남는다 — 여기서 되돌리지 않으면 샌다.
                    Release();
                    throw;
                }

                // ★"PCC 전송" 이 아니다. 버퍼는 PC 의 엔진 메모리에 있고, PCC 하드웨어로는
                //   인쇄 명령(PCMD_IMAGE_BUFFER)을 낼 때 간다. 여기서 "PCC 로 갔다" 고 적으면
                //   PCC 화면에서 그 데이터를 찾게 된다 — 거기엔 아직 아무것도 없다.
                _lastDwords = data.Length;
                // DWORD 수와 버퍼 ID 를 그대로 적는다 — 엔진이 자기 로그에 남기는 값과 같아서
                // 두 줄을 나란히 놓고 대조할 수 있다("Allocated image buffer DWORDs=… ID=…").
                _log?.Invoke($"엔진 버퍼 적재 완료 — 버퍼 #{_bufferId}, {data.Length:N0} DWORD, " +
                             $"{height}스텝 × {width}노즐, {bpp}bpp, {data.Length * 4L / 1024}KB " +
                             "(PC 엔진 메모리. PCC 로는 인쇄할 때 나간다)");
            }
        }

        /// <summary>
        /// 버퍼 반납. 발사 중에는 부르면 안 된다 — 동기 해제라 엔진이 아직 읽고 있으면 위험하다.
        /// (<see cref="PrintJobController.Unload"/> 만 부르고, 그쪽은 인쇄가 끝난 뒤다)
        /// </summary>
        public void Release()
        {
            lock (_io)
            {
                if (_bufferId == NoBuffer) return;
                try { PrinterInterfaceCLS.PiSynchronousImageBufferFree(_bufferId); }
                catch { /* 반납 실패로 화면을 막지 않는다 — 엔진 재시작이면 어차피 사라진다 */ }
                _bufferId   = NoBuffer;
                _lastDwords = 0;
            }
        }

        // ── 헬퍼 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 화소 비트뎁스. 저장된 <see cref="PrintDataSet.PrintPara.BitsPerPixel"/> 을 쓰되,
        /// 실제 방울 단계가 그 안에 안 들어가면 <b>거부한다</b> — 조용히 올려 잡으면 헤드가
        /// 기대하는 것과 달라지고, 조용히 잘라내면 큰 방울이 작은 방울로 찍힌다.
        /// </summary>
        private static int ResolveBitsPerPixel(PrintJob job)
        {
            int bpp = job.Para.BitsPerPixel;
            if (!MeteorImageBuffer.IsSupportedBpp(bpp))
                throw new InvalidOperationException(
                    $"지원하지 않는 비트뎁스 {bpp} — Print_Para.dat 의 BitsPerPixel 은 1·2·4·8 중 하나여야 합니다.");

            int max = 0;
            var lv = job.Pattern.Levels;
            for (int s = 0; s < lv.GetLength(0); s++)
                for (int c = 0; c < lv.GetLength(1); c++)
                    if (lv[s, c] > max) max = lv[s, c];

            int limit = (1 << bpp) - 1;
            if (max > limit)
                throw new InvalidOperationException(
                    $"방울 단계 {max} 가 {bpp}bpp 상한 {limit} 을 넘습니다 — 패턴과 Print_Para.dat 가 어긋났습니다.");

            return bpp;
        }

        private static void Check(eRET r, string call)
        {
            if (r == eRET.RVAL_OK) return;
            string hint = r switch
            {
                eRET.RVAL_NOMEM                => " — 엔진 메모리 부족. 앞서 올린 버퍼를 반납했는지 확인하세요.",
                eRET.RVAL_MEM_LIMIT            => " — 엔진 메모리 한도 초과. cfg 의 버퍼 한도를 보거나 패턴을 줄이세요.",
                eRET.RVAL_NOT_HOSTING          => " — 이 프로세스가 PrintEngine 을 띄우지 않았습니다(PiStartPrintEngine 먼저).",
                eRET.RVAL_BADBITSPERPIXEL      => " — 헤드가 받지 않는 비트뎁스입니다. Print_Para.dat 의 BitsPerPixel 을 확인하세요.",
                eRET.RVAL_STRUCT_SIZE_MISMATCH => " — SDK 버전이 다릅니다. lib\\Meteor 의 DLL 과 엔진 버전을 맞추세요.",
                _ => "",
            };
            throw new InvalidOperationException($"Meteor {call} 실패: {r}{hint}");
        }
    }
}
