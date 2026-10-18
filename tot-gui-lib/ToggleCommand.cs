using System;
using System.ComponentModel;
using System.Windows.Input;

namespace tot_gui_lib
{
    public class ToggleCommand(
        Action<object?, bool> execute,
        bool defaultState,
        string offClass,
        string onClass,
        bool enabled = true)
        : ICommand, INotifyPropertyChanged
    {
        private bool _enabled = enabled;
        private bool _toggled = defaultState;

        public event EventHandler? CanExecuteChanged;
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Classes => _toggled ? onClass : offClass;

        public bool CanExecute(object? parameter)
        {
            return _enabled;
        }

        public void Execute(object? parameter)
        {
            if (!_enabled) return;
            _toggled = !_toggled;
            execute(parameter, _toggled);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Classes)));
        }

        public void SetToggle(bool value)
        {
            if(_toggled == value) return;
            _toggled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Classes)));
        }

        public void IsEnabled(bool enabled)
        {
            _enabled = enabled;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
