using System.Windows.Input;

namespace BotView.ViewModels
{
    public sealed class RelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Predicate<object?>? _canExecute;
        private bool _isExecuting;

        public RelayCommand(Action execute)
            : this(_ =>
            {
                execute();
                return Task.CompletedTask;
            })
        {
        }

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
            : this(parameter =>
            {
                execute(parameter);
                return Task.CompletedTask;
            }, canExecute)
        {
        }

        public RelayCommand(Func<Task> execute)
            : this(_ => execute())
        {
        }

        public RelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) =>
            !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            try
            {
                _isExecuting = true;
                RaiseCanExecuteChanged();
                await _execute(parameter);
            }
            finally
            {
                _isExecuting = false;
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
