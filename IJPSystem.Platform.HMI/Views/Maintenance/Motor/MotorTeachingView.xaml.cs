using IJPSystem.Platform.HMI.ViewModels;
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace IJPSystem.Platform.HMI.Views
{
    public partial class MotorTeachingView : UserControl
    {
        // 미사용 축 표시 — RecipeView.NumericEditingStyle 과 동일한 빨간색 계열.
        // Freeze 로 dispatcher-affinity 제거 + 셀 다수에서 공유 안전.
        private static readonly Brush DisabledFieldBg     = MakeFrozen("#3F1D1D");
        private static readonly Brush DisabledFieldBorder = MakeFrozen("#7F1D1D");
        private static readonly Brush DisabledFieldFg     = MakeFrozen("#FCA5A5");

        private static SolidColorBrush MakeFrozen(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        public MotorTeachingView()
        {
            InitializeComponent();
            this.Loaded += MotorTeachingView_Loaded;
            this.Unloaded += MotorTeachingView_Unloaded;

            // 사용자 액션만 캐치 (프로그래밍 변경/자동 포커스 이동에는 무반응)
            PointGrid.AddHandler(CheckBox.ClickEvent, new RoutedEventHandler(OnTeachingClick));
        }

        private void OnTeachingClick(object sender, RoutedEventArgs e)
        {
            // CheckBox.Click은 사용자 클릭/Space 키에만 발동 (IsChecked 프로그래밍 변경에는 안 발동)
            // Dictionary indexer 바인딩은 자동 통지가 없어 같은 행의 다른 셀이 stale 됨
            if (e.OriginalSource is CheckBox cb && cb.DataContext is TeachingPoint tp)
                tp.RefreshAxisUsed();
        }

        // ── Jog 이벤트 ──────────────────────────────────────────────────────────

        private void JogBackward_MouseDown(object sender, MouseButtonEventArgs e) => ExecuteJog(sender, false);

        private void JogForward_MouseDown(object sender, MouseButtonEventArgs e) => ExecuteJog(sender, true);

        // 단위 라디오(Conti/10µm/100µm)는 SelectedAxis 의 JogUnit 만 갱신하므로
        // Tag(X/Y/Z/T)로 다른 축을 지목한 경우 그 축의 JogUnit 은 기본값(=0, Conti)에 머무름.
        // → 모드 라디오 의도가 반영되도록 SelectedAxis.JogUnit 을 대상 축에 복사한 뒤 실행.
        private void ExecuteJog(object sender, bool isForward)
        {
            if (DataContext is not MotorTeachingViewModel vm) return;
            var axis = ResolveAxis(sender);
            if (axis == null) return;   // 구성에 없는 축(예: 3축 구성의 T) 버튼은 무동작

            if (vm.SelectedAxis != null)
                axis.JogUnit = vm.SelectedAxis.JogUnit;

            _ = axis.JogMoveAsync(isForward);
        }

        private void Jog_MouseUp(object sender, MouseButtonEventArgs e)
        {
            var axis = ResolveAxis(sender);
            if (axis != null) _ = axis.StopAsync();
        }

        // 조그 대상 축 해석:
        //   Tag 없음      → SelectedAxis (SELECT AXIS 패널 조그)
        //   Tag=X/Y/Z/T   → AxisNo 일치 축. 일치하는 축이 없으면 null 반환
        //                   → 구성에 없는 축(예: 3축 구성의 T) 버튼은 아무 동작도 하지 않음.
        //                     (이전엔 SelectedAxis 로 폴백되어 T 버튼이 선택 축을 움직이는 버그가 있었음)
        private AxisViewModel? ResolveAxis(object sender)
        {
            if (DataContext is not MotorTeachingViewModel vm) return null;
            string? tag = (sender as Button)?.Tag?.ToString();
            if (string.IsNullOrEmpty(tag)) return vm.SelectedAxis;
            return vm.AxisList.FirstOrDefault(a => a.Info.AxisNo == tag);
        }

        // ── View 수명주기 ────────────────────────────────────────────────────────

        private void MotorTeachingView_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (DataContext is not MotorTeachingViewModel vm) return;

                vm.LoadFromDatabase();
                BuildColumns(vm);
                vm.PropertyChanged += OnVmPropertyChanged;

            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void MotorTeachingView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MotorTeachingViewModel vm) return;
            vm.PropertyChanged -= OnVmPropertyChanged;
            vm.Cleanup();
        }

        // ── DataGrid 갱신 ────────────────────────────────────────────────────────

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MotorTeachingViewModel.TeachingPoints) &&
                DataContext is MotorTeachingViewModel vm)
            {
                PointGrid.ItemsSource = vm.TeachingPoints;
            }
        }

        // 단일 클릭으로 셀 편집 진입
        // CheckBox 는 ClickMode=Press(BuildAxisCellTemplate 에서 설정)로 자체 처리하므로
        // 클릭 타겟이 CheckBox 트리이면 이 핸들러는 비켜준다(이중 토글 방지).
        private void PointGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var probe = e.OriginalSource as DependencyObject;
            while (probe != null && probe is not DataGridCell)
            {
                if (probe is CheckBox) return;
                probe = VisualTreeHelper.GetParent(probe);
            }

            var dep = e.OriginalSource as DependencyObject;
            while (dep != null && dep is not DataGridCell && dep is not DataGridColumnHeader)
                dep = VisualTreeHelper.GetParent(dep);

            if (dep is DataGridCell { IsEditing: false, IsReadOnly: false } cell)
            {
                if (!cell.IsFocused) cell.Focus();
                (sender as DataGrid)?.BeginEdit(e);
            }
        }

        // ── 컬럼 빌드 ────────────────────────────────────────────────────────────

        private void BuildColumns(MotorTeachingViewModel vm)
        {
            try
            {
                PointGrid.ItemsSource = null;
                PointGrid.Columns.Clear();

                // TEACHING POS. 열 (첫 번째 고정, 읽기 전용)
                var nameColumn = new DataGridTextColumn
                {
                    Header = "TEACHING POS.",
                    Binding = new Binding("PointName"),
                    IsReadOnly = true,
                    Width = 160,
                    ElementStyle = new Style(typeof(TextBlock))
                    {
                        Setters =
                        {
                            new Setter(TextBlock.FontSizeProperty, 14.0),
                            new Setter(TextBlock.ForegroundProperty,
                                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#93C5FD"))),
                            new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center),
                            new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                        }
                    }
                };
                PointGrid.Columns.Add(nameColumn);

                // 축별 위치 열 (동적 생성, 편집 가능)
                var cellStyle = PointGrid.TryFindResource("EditableCellStyle") as Style;
                var editingStyle = new Style(typeof(TextBox))
                {
                    Setters =
                    {
                        new Setter(TextBox.ForegroundProperty,     new SolidColorBrush(Colors.White)),
                        new Setter(TextBox.BackgroundProperty,     new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E3A5F"))),
                        new Setter(TextBox.BorderBrushProperty,    new SolidColorBrush((Color)ColorConverter.ConvertFromString("#60A5FA"))),
                        new Setter(TextBox.BorderThicknessProperty, new Thickness(1)),
                        new Setter(TextBox.FontSizeProperty,       14.0),
                        new Setter(TextBox.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
                        new Setter(TextBox.VerticalContentAlignmentProperty,   VerticalAlignment.Center),
                        new Setter(TextBox.CaretBrushProperty,     new SolidColorBrush(Colors.White)),
                    }
                };

                // 미사용 축은 기본 WPF disabled 회색보다 명확한 빨간색 계열로 표시.
                var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
                disabledTrigger.Setters.Add(new Setter(TextBox.BackgroundProperty,  DisabledFieldBg));
                disabledTrigger.Setters.Add(new Setter(TextBox.BorderBrushProperty, DisabledFieldBorder));
                disabledTrigger.Setters.Add(new Setter(TextBox.ForegroundProperty,  DisabledFieldFg));
                disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty,   1.0));
                editingStyle.Triggers.Add(disabledTrigger);

                var displayStyle = new Style(typeof(TextBlock))
                {
                    Setters =
                    {
                        new Setter(TextBlock.FontSizeProperty,            14.0),
                        new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center),
                        new Setter(TextBlock.VerticalAlignmentProperty,   VerticalAlignment.Center),
                        new Setter(TextBlock.TextAlignmentProperty,       TextAlignment.Center),
                    }
                };

                foreach (var axis in vm.AxisList)
                {
                    string axisName  = axis.Info.Name;
                    string axisShort = axis.Info.AxisNo.Replace(" AXIS", "").ToUpper();

                    // IsReadOnly=true로 셀의 편집 라이프사이클을 우회.
                    // TextBox 자체가 사용자 입력을 받아 LostFocus에 ViewModel 갱신 → 자동 BeginEdit/EditEnding 미발생
                    var templateColumn = new DataGridTemplateColumn
                    {
                        Header       = axisShort,
                        Width        = 130,
                        IsReadOnly   = true,
                        CellStyle    = cellStyle,
                        CellTemplate = BuildAxisCellTemplate(axisName, editingStyle),
                    };
                    PointGrid.Columns.Add(templateColumn);
                }

                PointGrid.ItemsSource = vm.TeachingPoints;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Grid 초기화 오류: {ex.Message}");
            }
        }

        // 축별 셀 템플릿: [CheckBox(사용여부) + TextBox(위치값)]
        // CheckBox는 항상 활성, TextBox만 IsEnabled를 AxisUsed에 바인딩.
        // 셀이 IsReadOnly=true라 TextBox가 직접 입력을 받음 (LostFocus에서 ViewModel 갱신).
        private static DataTemplate BuildAxisCellTemplate(string axisName, Style? innerStyle)
        {
            var stack = new FrameworkElementFactory(typeof(StackPanel));
            stack.SetValue(StackPanel.OrientationProperty,          Orientation.Horizontal);
            stack.SetValue(StackPanel.HorizontalAlignmentProperty,  HorizontalAlignment.Center);
            stack.SetValue(StackPanel.VerticalAlignmentProperty,    VerticalAlignment.Center);

            // CheckBox — ClickMode=Press 로 마우스 Down 시점에 즉시 토글.
            // DataGrid 가 셀 선택 처리로 Click(=MouseUp) 을 가로채는 WPF 의 고질적 버그
            // (DataGridTemplateColumn 안의 CheckBox 가 한 번에 토글 안 되는 현상) 를 우회.
            // Focusable=false 는 셀이 키보드 포커스를 먼저 가져가서 첫 클릭이 묻히는 케이스 방지.
            var cb = new FrameworkElementFactory(typeof(CheckBox));
            cb.SetBinding(CheckBox.IsCheckedProperty,
                new Binding($"AxisUsed[{axisName}]")
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                });
            cb.SetValue(CheckBox.VerticalAlignmentProperty, VerticalAlignment.Center);
            cb.SetValue(CheckBox.MarginProperty, new Thickness(0, 0, 8, 0));
            cb.SetValue(CheckBox.ClickModeProperty, ClickMode.Press);
            cb.SetValue(UIElement.FocusableProperty, false);
            stack.AppendChild(cb);

            // TextBox (값 입력 — LostFocus에 ViewModel 갱신)
            var tb = new FrameworkElementFactory(typeof(TextBox));
            tb.SetBinding(TextBox.TextProperty,
                new Binding($"Positions[{axisName}]")
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
                    StringFormat = "F3",
                });
            tb.SetBinding(UIElement.IsEnabledProperty, new Binding($"AxisUsed[{axisName}]"));
            tb.SetValue(FrameworkElement.MinWidthProperty, 60.0);
            if (innerStyle != null)
                tb.SetValue(FrameworkElement.StyleProperty, innerStyle);
            stack.AppendChild(tb);

            return new DataTemplate { VisualTree = stack };
        }
    }
}
