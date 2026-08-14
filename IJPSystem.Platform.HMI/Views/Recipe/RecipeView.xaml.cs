using IJPSystem.Platform.HMI.ViewModels;
using System;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace IJPSystem.Platform.HMI.Views
{
    public partial class RecipeView : UserControl
    {
        private string _cellEditStartValue = string.Empty;
        private RecipeViewModel? _vm;
        private bool _teachingColumnsBuilt = false;

        public RecipeView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;

            // 사용자 액션만 캐치 (프로그래밍 변경/자동 포커스 이동에는 무반응)
            TeachingPointGrid.AddHandler(CheckBox.ClickEvent,      new RoutedEventHandler(OnTeachingClick));
            TeachingPointGrid.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(OnTeachingTextChanged));
        }

        private void OnTeachingClick(object sender, RoutedEventArgs e)
        {
            // CheckBox.Click은 사용자 클릭/Space 키에만 발동 (IsChecked 프로그래밍 변경에는 안 발동)
            if (e.OriginalSource is CheckBox cb && _vm != null)
            {
                _vm.IsDirty = true;
                // 같은 행의 TextBox IsEnabled 바인딩을 즉시 갱신 → 미사용 색상 토글
                if (cb.DataContext is TeachingPoint tp)
                    tp.RefreshAxisUsed();
            }
        }

        private void OnTeachingTextChanged(object sender, TextChangedEventArgs e)
        {
            // 키보드 포커스가 해당 TextBox 안에 있을 때만 = 사용자 직접 입력
            if (e.OriginalSource is TextBox tb && tb.IsKeyboardFocusWithin && _vm != null)
                _vm.IsDirty = true;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_vm != null)
                _vm.PropertyChanged -= OnVmPropertyChanged;

            _vm = e.NewValue as RecipeViewModel;

            if (_vm != null)
            {
                _vm.PropertyChanged += OnVmPropertyChanged;
                TryBuildTeachingColumns();
            }
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RecipeViewModel.CurrentDataType))
                TryBuildTeachingColumns();
            else if (e.PropertyName == nameof(RecipeViewModel.TeachingPoints))
                TeachingPointGrid.ItemsSource = _vm?.TeachingPoints;
        }

        private void TryBuildTeachingColumns()
        {
            if (_vm == null || _vm.CurrentDataType != RecipeDataType.Teach) return;

            if (_teachingColumnsBuilt)
            {
                TeachingPointGrid.ItemsSource = _vm.TeachingPoints;
                return;
            }
            BuildTeachingColumns();
        }

        private void BuildTeachingColumns()
        {
            if (_vm == null) return;

            TeachingPointGrid.ItemsSource = null;
            TeachingPointGrid.Columns.Clear();

            var cellStyle     = TeachingPointGrid.TryFindResource("RecipeCompareCellStyle") as Style;
            var editingStyle  = TeachingPointGrid.TryFindResource("NumericEditingStyle")    as Style;
            var displayStyle  = TeachingPointGrid.TryFindResource("NumericDisplayStyle")    as Style;

            // 포인트 이름 열 (읽기 전용)
            TeachingPointGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "TEACHING POS.",
                Binding = new Binding("PointName"),
                IsReadOnly = true,
                Width = 180,
                ElementStyle = new Style(typeof(TextBlock))
                {
                    Setters =
                    {
                        new Setter(TextBlock.FontSizeProperty, 15.0),
                        new Setter(TextBlock.ForegroundProperty,
                            new SolidColorBrush((Color)ColorConverter.ConvertFromString("#93C5FD"))),
                        new Setter(TextBlock.FontWeightProperty,          FontWeights.Bold),
                        new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center),
                        new Setter(TextBlock.VerticalAlignmentProperty,   VerticalAlignment.Center),
                    }
                }
            });

            // 축별 [CheckBox + 위치값] 통합 컬럼
            // IsReadOnly=true로 셀의 편집 라이프사이클을 우회 (자동 BeginEdit/EditEnding으로 인한 IsDirty 오발생 방지)
            foreach (var axis in _vm.AxisList)
            {
                string axisName  = axis.Info.Name;
                string axisShort = axisName.Replace(" AXIS", "").ToUpper();

                TeachingPointGrid.Columns.Add(new DataGridTemplateColumn
                {
                    Header       = axisShort,
                    Width        = 130,
                    IsReadOnly   = true,
                    CellStyle    = cellStyle,
                    CellTemplate = BuildAxisCellTemplate(axisName, editingStyle),
                });
            }

            TeachingPointGrid.ItemsSource = _vm.TeachingPoints;
            _teachingColumnsBuilt = true;
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

            // CheckBox
            var cb = new FrameworkElementFactory(typeof(CheckBox));
            cb.SetBinding(CheckBox.IsCheckedProperty,
                new Binding($"AxisUsed[{axisName}]")
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                });
            cb.SetValue(CheckBox.VerticalAlignmentProperty, VerticalAlignment.Center);
            cb.SetValue(CheckBox.MarginProperty, new Thickness(0, 0, 8, 0));
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

        private void TeachingGrid_GotFocus(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not DataGridCell cell) return;
            if (cell.Column.IsReadOnly) return;

            var displayBlock = GetVisualChild<TextBlock>(cell);
            _cellEditStartValue = displayBlock?.Text ?? string.Empty;

            ((DataGrid)sender).BeginEdit(e);

            var textBox = GetVisualChild<TextBox>(cell);
            if (textBox != null) { textBox.Focus(); textBox.SelectAll(); }
        }

        private void TeachingGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            if (e.EditingElement is not TextBox textBox) return;

            if (double.TryParse(_cellEditStartValue, out double oldVal) &&
                double.TryParse(textBox.Text,        out double newVal) &&
                Math.Abs(oldVal - newVal) < 0.0001)
                return;

            if (_vm != null) _vm.IsDirty = true;
        }

        private void DataGrid_GotFocus(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not DataGridCell cell) return;
            if (cell.Column.IsReadOnly) return;

            // BeginEdit 전에 TextBlock에 표시된 현재값을 캡처
            var displayBlock = GetVisualChild<TextBlock>(cell);
            _cellEditStartValue = displayBlock?.Text ?? string.Empty;

            DataGrid dataGrid = (DataGrid)sender;
            dataGrid.BeginEdit(e);

            var textBox = GetVisualChild<TextBox>(cell);
            if (textBox != null)
            {
                textBox.Focus();
                textBox.SelectAll();
            }
        }

        private void DataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            if (e.EditingElement is not TextBox textBox) return;

            // 편집 시작값(TextBlock 표시값)과 입력값을 double로 비교
            if (double.TryParse(_cellEditStartValue, out double oldVal) &&
                double.TryParse(textBox.Text, out double newVal) &&
                Math.Abs(oldVal - newVal) < 0.0001)
                return; // 실제 변경 없음

            if (this.DataContext is IJPSystem.Platform.HMI.ViewModels.RecipeViewModel vm)
                vm.IsDirty = true;
        }

        // 헬퍼 메서드: 셀 내부의 TextBox를 찾는 용도
        private T? GetVisualChild<T>(DependencyObject? parent) where T : DependencyObject
        {
            if (parent == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var childOfChild = GetVisualChild<T>(child);
                if (childOfChild != null) return childOfChild;
            }
            return null;
        }

        ////private void TextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        ////{
        ////    // 정규식: 숫자와 소수점만 허용
        ////    Regex regex = new Regex("[^0-9.]+");
        ////    e.Handled = regex.IsMatch(e.Text);
        ////}
        //
        private void TextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // 1. sender가 TextBox인지 확인
            if (!(sender is TextBox textBox)) return;

            // 2. 현재 이미 입력된 텍스트와 새로 입력될 텍스트를 합쳐봄
            // SelectionLength를 고려하여 사용자가 범위를 지정하고 덮어쓰는 경우까지 계산
            string currentText = textBox.Text;
            int selectionStart = textBox.SelectionStart;
            int selectionLength = textBox.SelectionLength;

            string newText = currentText.Remove(selectionStart, selectionLength)
                                        .Insert(selectionStart, e.Text);

            // 3. 정규식 검사
            // ^[0-9]* : 숫자로 시작하고 (0개 이상)
            // (\.[0-9]*)? : 소수점이 오직 하나만 올 수 있으며, 그 뒤에 숫자가 붙을 수 있음 (그룹 전체가 0 또는 1번)
            // $ : 문자열 끝
            Regex regex = new Regex(@"^[0-9]*(\.[0-9]*)?$");

            // 패턴에 맞지 않으면 입력을 무시함
            e.Handled = !regex.IsMatch(newText);
        }

        // 🌟 스페이스바 입력 차단 (선택 사항)
        private void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space) e.Handled = true;
        }
        private void TextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                tb.SelectAll();
            }
        }

        /// <summary>
        /// 헤드명 입력 — <b>영문 대문자·숫자·하이픈·밑줄·점</b>만 받는다(예: <c>EPSON-S3200</c>).
        ///
        /// <para>대문자로 바꾸는 것은 XAML 의 <c>CharacterCasing="Upper"</c> 가 하고, 여기서는
        /// 허용 문자만 거른다. 소문자를 막지 않고 통과시키는 이유가 그것이다 — 막아 버리면
        /// 소문자 키가 아무 반응이 없어 고장으로 보인다. 실제로는 눌리는 즉시 대문자로 들어간다.</para>
        ///
        /// <para>한글이 안 들어가는 것은 의도다. 헤드명은 벤더 표기를 그대로 적는 자리이고,
        /// 나중에 파일명·로그에 섞여 나가므로 아스키로 묶어 둔다.</para>
        /// </summary>
        private void HeadNameBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !Regex.IsMatch(e.Text, @"^[A-Za-z0-9\-_. ]+$");
        }
    }
}
