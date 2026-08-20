using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IJPSystem.Platform.HMI.Common.Controls
{
    /// <summary>
    /// 숫자 입력 칸 + 위/아래 화살표.
    ///
    /// <para><b>칸 높이는 그대로 둔다</b>: 화살표는 자체 높이를 갖지 않고 칸의 높이를 반씩
    /// 나눠 갖는다. 그래서 이 컨트롤을 기존 24px 칸 자리에 그대로 넣어도 줄 높이가 변하지 않는다.</para>
    ///
    /// <para><see cref="Step"/> 은 그 값의 <b>양자화 격자</b>와 같게 준다(시간 0.05µs ·
    /// 전압 0.05V · 기울기 0.01). 격자와 다른 폭으로 올리면 눌러서 만든 값이 저장 직전
    /// 계산에서 다시 반올림되어, 화면 숫자와 실제 값이 어긋난다.</para>
    /// </summary>
    public partial class NumericSpinner : UserControl
    {
        public NumericSpinner()
        {
            InitializeComponent();
            Loaded += (_, _) => ShowText();
        }

        // ── Value ─────────────────────────────────────────────────────────
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(double), typeof(NumericSpinner),
                new FrameworkPropertyMetadata(0.0,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((NumericSpinner)d).ShowText();

        // ── 표시·증감 설정 ────────────────────────────────────────────────

        /// <summary>화살표 한 번에 움직이는 폭. 그 값의 양자화 격자와 같게 준다.</summary>
        public static readonly DependencyProperty StepProperty =
            DependencyProperty.Register(nameof(Step), typeof(double), typeof(NumericSpinner),
                new PropertyMetadata(0.05));

        public double Step
        {
            get => (double)GetValue(StepProperty);
            set => SetValue(StepProperty, value);
        }

        public static readonly DependencyProperty FormatProperty =
            DependencyProperty.Register(nameof(Format), typeof(string), typeof(NumericSpinner),
                new PropertyMetadata("F2", (d, _) => ((NumericSpinner)d).ShowText()));

        public string Format
        {
            get => (string)GetValue(FormatProperty);
            set => SetValue(FormatProperty, value);
        }

        public static readonly DependencyProperty MinimumProperty =
            DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(NumericSpinner),
                new PropertyMetadata(double.NegativeInfinity));

        public double Minimum
        {
            get => (double)GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(NumericSpinner),
                new PropertyMetadata(double.PositiveInfinity));

        public double Maximum
        {
            get => (double)GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        // ── 읽기 전용 ─────────────────────────────────────────────────────
        public static readonly DependencyProperty IsReadOnlyProperty =
            DependencyProperty.Register(nameof(IsReadOnly), typeof(bool), typeof(NumericSpinner),
                new PropertyMetadata(false, OnReadOnlyChanged));

        public bool IsReadOnly
        {
            get => (bool)GetValue(IsReadOnlyProperty);
            set => SetValue(IsReadOnlyProperty, value);
        }

        private static void OnReadOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var s = (NumericSpinner)d;
            bool ro = (bool)e.NewValue;

            s.PART_Text.IsReadOnly = ro;
            // 계산값은 회색 — 입력칸처럼 보이면 넣어도 안 바뀐다고 오해한다.
            s.PART_Text.Foreground = ro ? Palette.ReadOnlyText : Palette.NormalText;
            // 눌러도 안 바뀌는 화살표는 고장난 것으로 읽힌다 — 아예 감춘다.
            s.PART_Spin.Visibility = ro ? Visibility.Collapsed : Visibility.Visible;
        }

        private static class Palette
        {
            public static readonly System.Windows.Media.Brush NormalText =
                new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xE2, 0xE8, 0xF0));
            public static readonly System.Windows.Media.Brush ReadOnlyText =
                new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x64, 0x74, 0x8B));

            static Palette() { NormalText.Freeze(); ReadOnlyText.Freeze(); }
        }

        // ── 동작 ──────────────────────────────────────────────────────────

        private void OnUp(object sender, RoutedEventArgs e)   => Bump(+1);
        private void OnDown(object sender, RoutedEventArgs e) => Bump(-1);

        private void Bump(int direction)
        {
            if (IsReadOnly) return;

            // 화살표를 누르기 전에 손으로 고쳐 둔 값이 있으면 그것을 먼저 받는다.
            Commit();

            double step = Step > 0 ? Step : 0.05;
            SetClamped(Value + direction * step);
        }

        private void OnTextLostFocus(object sender, RoutedEventArgs e) => Commit();

        private void OnTextKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:  Commit(); e.Handled = true; break;
                case Key.Up:     Bump(+1); e.Handled = true; break;
                case Key.Down:   Bump(-1); e.Handled = true; break;
                case Key.Escape: ShowText(); e.Handled = true; break;   // 되돌리기
            }
        }

        /// <summary>글자를 값으로 받는다. 숫자가 아니면 되돌린다.</summary>
        private void Commit()
        {
            if (IsReadOnly) return;

            if (double.TryParse(PART_Text.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                SetClamped(v);
            else
                ShowText();
        }

        private void SetClamped(double v)
        {
            v = Math.Clamp(v, Minimum, Maximum);

            // 부동소수 잔재(2.6500000000000004)를 남기지 않는다 — 화면에도 파일에도 남는다.
            v = Math.Round(v, 6);

            if (Math.Abs(v - Value) < 1e-12) { ShowText(); return; }
            Value = v;      // 바인딩이 값을 받고, OnValueChanged 가 글자를 다시 그린다
        }

        private void ShowText()
        {
            string text = Value.ToString(Format ?? "F2", CultureInfo.InvariantCulture);
            if (PART_Text.Text != text) PART_Text.Text = text;
        }
    }
}
