namespace IJPSystem.Platform.HMI.Vision
{
    /// <summary>
    /// 노즐 하나의 측정 결과 마커. <see cref="ImageScaleRuler"/> 가 초록으로 그린다.
    ///
    /// 좌표는 <b>이미지 픽셀</b>이다 — 표시 배율(Uniform 축소)은 그리는 쪽이 알고 있으므로
    /// 여기서 화면 좌표로 바꾸지 않는다. 그래야 창 크기가 바뀌어도 마커가 액적에서 떨어지지 않는다.
    /// </summary>
    public sealed class NozzleMeasureMark
    {
        /// <summary>검출된 액적 중심 X[px].</summary>
        public double XPixel { get; init; }

        /// <summary>검출된 액적 중심 Y[px].</summary>
        public double YPixel { get; init; }

        /// <summary>토출 속도[m/s]. NaN 이면 값 없음(마커만 그리고 숫자는 생략).</summary>
        public double Velocity { get; init; } = double.NaN;

        /// <summary>
        /// 측정창 경계에 걸려 면적이 잘린 액적. 숫자를 주황으로 적어 어느 노즐인지 바로 보이게 한다 —
        /// 요약 한 줄에 "3개 걸림"만 뜨면 어느 것을 고쳐야 할지 알 수 없다.
        /// </summary>
        public bool Clipped { get; init; }
    }
}
