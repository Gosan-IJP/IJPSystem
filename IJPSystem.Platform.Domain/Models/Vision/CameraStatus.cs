using IJPSystem.Platform.Domain.Common;
using System;

namespace IJPSystem.Platform.Domain.Models.Vision
{
    public class CameraStatus : ViewModelBase
    {
        public string CameraId { get; set; } = string.Empty;

        /// <summary>하드웨어 식별자(IMAQdx 카메라 이름). 표시에는 <see cref="DisplayName"/> 사용.</summary>
        public string Name     { get; set; } = string.Empty;

        /// <summary>화면 표시명. 비어 있으면 <see cref="Name"/> → <see cref="CameraId"/> 순으로 대체.</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Visual Monitor 소스 목록 노출 여부.</summary>
        public bool ShowInMonitor { get; set; } = true;

        /// <summary>표시에 쓸 최종 이름(DisplayName → Name → CameraId).</summary>
        public string DisplayLabel =>
            !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName
            : !string.IsNullOrWhiteSpace(Name) ? Name : CameraId;

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            set => SetProperty(ref _isConnected, value);
        }

        private bool _isCapturing;
        public bool IsCapturing
        {
            get => _isCapturing;
            set => SetProperty(ref _isCapturing, value);
        }

        private bool _isLightOn;
        public bool IsLightOn
        {
            get => _isLightOn;
            set => SetProperty(ref _isLightOn, value);
        }

        private int _lightIntensity;
        public int LightIntensity
        {
            get => _lightIntensity;
            set => SetProperty(ref _lightIntensity, value);
        }

        private double _exposureMs;
        public double ExposureMs
        {
            get => _exposureMs;
            set => SetProperty(ref _exposureMs, value);
        }

        private double _gain;
        public double Gain
        {
            get => _gain;
            set => SetProperty(ref _gain, value);
        }

        private long _totalCaptureCount;
        public long TotalCaptureCount
        {
            get => _totalCaptureCount;
            set => SetProperty(ref _totalCaptureCount, value);
        }

        private DateTime? _lastCaptureTime;
        public DateTime? LastCaptureTime
        {
            get => _lastCaptureTime;
            set => SetProperty(ref _lastCaptureTime, value);
        }

        private InspectionResult? _lastResult;
        public InspectionResult? LastResult
        {
            get => _lastResult;
            set => SetProperty(ref _lastResult, value);
        }
    }
}
