using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace IJPSystem.Drivers.Motion
{
    /// <summary>
    /// Comizoa EtherCAT 모션 구현 (ComiEcatSdk.dll 래퍼).
    /// ※ [DllImport] 함수명/인자/호출규약은 자리표시(placeholder)다.
    ///   실제 ComiEcatSdk 헤더(.h)/매뉴얼의 함수로 반드시 교체할 것.
    /// </summary>
    public sealed class ComiEcatMotion : IComiMotion
    {
        private const string Dll = "ComiEcatSdk.dll";

        // ===== P/Invoke (자리표시 → 실제 SDK 함수로 교체) =====
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ecat_Init(int embedded, int sim);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ecat_Close();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ecat_ServoOn(int axis, int on);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ecat_Home(int axis);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ecat_MoveAbs(int axis, double pos);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ecat_MoveRel(int axis, double dist);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ecat_Jog(int axis, int dir);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ecat_Stop(int axis);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ecat_SetVel(int axis, double vel, double acc, double dec);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ecat_SetEncRes(int axis, double ppu);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ecat_SetHomeParam(int axis, int dir, double hi, double lo, double acc, int mode, double offset);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ecat_SetSwLimit(int axis, int enable, double posLim, double negLim);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ecat_SetAlState(int axis, int reset);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ecat_GetPos(int axis, out double pos);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ecat_GetStatus(int axis, out int flags);
        // =========================================================

        private bool _init;

        private static void Check(int rc, string op)
        {
            if (rc != 0) throw new InvalidOperationException($"{op} 실패 (rc={rc})");
        }
        private void Need() { if (!_init) throw new InvalidOperationException("Init 먼저 호출."); }

        public void Init(ComiEcatConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            // 실제 SDK가 ini 경로/네트워크 설정을 받으면 그 API로 교체.
            Check(ecat_Init(cfg.EmbeddedMode ? 1 : 0, cfg.SimulationMode ? 1 : 0), "Init");
            _init = true;
        }

        public void Unload() { if (_init) { try { ecat_Close(); } catch { } _init = false; } }

        public void ServoOn(AxisId a, bool on) { Need(); Check(ecat_ServoOn((int)a, on ? 1 : 0), "ServoOn"); }
        public void Home(AxisId a) { Need(); Check(ecat_Home((int)a), "Home"); }
        public void MoveAbsolute(AxisId a, double pos) { Need(); Check(ecat_MoveAbs((int)a, pos), "MoveAbs"); }
        public void MoveRelative(AxisId a, double dist) { Need(); Check(ecat_MoveRel((int)a, dist), "MoveRel"); }
        public void Jog(AxisId a, int dir) { Need(); Check(ecat_Jog((int)a, dir), "Jog"); }
        public void Stop(AxisId a) { Need(); Check(ecat_Stop((int)a), "Stop"); }

        public void StopAll()
        {
            foreach (AxisId a in Enum.GetValues(typeof(AxisId)))
                try { ecat_Stop((int)a); } catch { }
        }

        public void SetVelocity(AxisId a, VelocityProfile p)
        { Need(); Check(ecat_SetVel((int)a, p.Velocity, p.Acceleration, p.Deceleration), "SetVelocity"); }

        public void SetEncoderResolution(AxisId a, double ppu)
        { Need(); Check(ecat_SetEncRes((int)a, ppu), "SetEncRes"); }

        public void SetHomeParameters(AxisId a, HomeParameters h)
        { Need(); Check(ecat_SetHomeParam((int)a, h.Direction, h.HighVelocity, h.LowVelocity, h.Acceleration, h.HomeMode, h.Offset), "SetHomeParam"); }

        public void SetSoftLimit(AxisId a, SoftLimit l)
        { Need(); Check(ecat_SetSwLimit((int)a, l.Enabled ? 1 : 0, l.PositiveLimit, l.NegativeLimit), "SetSwLimit"); }

        public void SetAlarmState(AxisId a, bool reset)
        { Need(); Check(ecat_SetAlState((int)a, reset ? 1 : 0), "SetAlState"); }

        public AxisState GetState(AxisId a)
        {
            Need();
            ecat_GetPos((int)a, out double pos);
            ecat_GetStatus((int)a, out int f);
            // 상태 비트 해석은 실제 SDK 정의에 맞게 교체.
            return new AxisState
            {
                Position = pos,
                IsMoving = (f & 0x01) != 0,
                ServoOn = (f & 0x02) != 0,
                IsHomed = (f & 0x04) != 0,
                Alarm = (f & 0x08) != 0,
                PositiveLimit = (f & 0x10) != 0,
                NegativeLimit = (f & 0x20) != 0
            };
        }

        public bool WaitForDone(AxisId a, int timeoutMs)
        {
            Need();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!GetState(a).IsMoving) return true;
                Thread.Sleep(5);
            }
            return false;
        }

        public void Dispose() => Unload();
    }
}
