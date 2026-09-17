using System;
using TrebuchetLib;

namespace Trebuchet
{
    public sealed class UIConfig : ConfigFile<UIConfig>
    {
        public bool FoldedMenu { get; set; }
        public string CurrentClientProfile { get; set; } = string.Empty;
        public string CurrentModlistProfile { get; set; } = string.Empty;
        public string CurrentSyncProfile { get; set; } = string.Empty;
        public string CurrentServerProfile { get; set; } = string.Empty;
        public bool DisplayProcessPerformance { get; set; } = DisplayProcessPerformanceDefault;
        public bool DisplayWarningOnKill { get; set; } = DisplayWarningOnKillDefault;
        public string UICulture { get; set; } = UICultureDefault;
        public int PlateformTheme { get; set; } = PlatformThemeDefault;
        public bool Experiments { get; set; } = ExperimentsDefault;
        public bool DebugMode { get; set; } = DebugModeDefault;

        public int[] ConsoleFilters
        {
            get => _consoleFilters;
            set => _consoleFilters = value;
        }

        public static readonly bool DisplayProcessPerformanceDefault = true;
        public static readonly bool DisplayWarningOnKillDefault = true;
        public static readonly string UICultureDefault = string.Empty;
        public static readonly int PlatformThemeDefault = 0;
        public static readonly bool ExperimentsDefault = false;
        public static readonly bool DebugModeDefault = false;
        private int[] _consoleFilters = [];

        public void SetInstanceFilter(int instance, ConsoleLogSource source, bool active)
        {
            EnsureConsoleSettings(instance);
            ConsoleFilters[instance] = active 
                ? ConsoleFilters[instance] | (1 << (int)source) 
                : ConsoleFilters[instance] & ~(1 << (int)source);
        }

        public bool GetInstanceFilter(int instance, ConsoleLogSource source)
        {
            if (_consoleFilters.Length <= instance) return source is ConsoleLogSource.ServerLog or ConsoleLogSource.Trebuchet;

            return (ConsoleFilters[instance] & (1 << (int)source)) != 0;
        }

        public void SetInstancePopup(int instance, bool popupedOut)
        {
            EnsureConsoleSettings(instance);
            ConsoleFilters[instance] = popupedOut 
                ? ConsoleFilters[instance] | (1 << 31) 
                : ConsoleFilters[instance] & ~(1 << 31);
        }
        
        public bool GetInstancePopup(int instance)
        {
            if (_consoleFilters.Length <= instance) return false;

            return (ConsoleFilters[instance] & (1 << 31)) != 0;
        }

        private void EnsureConsoleSettings(int instance)
        {
            if (_consoleFilters.Length > instance) return;
            var first = _consoleFilters.Length;
            Array.Resize(ref _consoleFilters, instance + 1);
            Array.Fill(_consoleFilters, (1 << (int)ConsoleLogSource.ServerLog) | (1 << (int)ConsoleLogSource.Trebuchet),
                first, _consoleFilters.Length - first);
        }
    }
}
