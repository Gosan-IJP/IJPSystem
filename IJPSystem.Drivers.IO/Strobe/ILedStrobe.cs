using System;

namespace IJPSystem.Drivers.IO.Strobe
{
    /// <summary>
    /// LED 스트로브 제어 추상화. Drop Watcher 촬영 시 정지 영상처럼 잡기 위한 발광 제어.
    /// (LabVIEW "20_DAQ LED Trigger" / iCore Strobe Controller 대응)
    /// </summary>
    public interface ILedStrobe : IDisposable
    {
        void Open();
        /// <summary>스트로브 on/off.</summary>
        void Enable(bool on);
        /// <summary>토출 대비 발광 지연 [us] — 촬영 시점 조정.</summary>
        void SetDelayMicroseconds(double us);
        /// <summary>발광 폭 [us].</summary>
        void SetWidthMicroseconds(double us);
        /// <summary>1회 트리거(수동 발광).</summary>
        void Fire();
        void Close();
    }
}
