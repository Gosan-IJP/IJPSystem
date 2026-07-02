using System;
using System.Windows.Input;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// MVVM 용 ICommand 구현 (CommandManager.RequerySuggested 기반).
    /// DXF Rasterizer 처럼 CanExecute 가 UI 상태(레이어 선택/결과 유무)에 따라
    /// 자동 재평가되어야 하는 경우에 사용한다.
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => _execute(parameter);

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}
