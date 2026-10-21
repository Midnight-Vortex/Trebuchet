using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TrebuchetLib;

namespace Trebuchet.Controls
{
    /// <summary>
    /// Interaction logic for CPUSelector.xaml
    /// </summary>
    public partial class CpuSelector : UserControl
    {
        public static readonly StyledProperty<long> CpuAffinityProperty = AvaloniaProperty.Register<CpuSelector, long>(nameof(CpuAffinity));
        // public static readonly DependencyProperty CPUAffinityProperty = DependencyProperty.Register("CPUAffinity",
        //     typeof(long),
        //     typeof(CPUSelector),
        //     new FrameworkPropertyMetadata(0L,
        //         FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
        //         OnCPUAffinityChanged, null, true, UpdateSourceTrigger.PropertyChanged)
        //     );

        // Registered once: in the instance constructor every new selector added one more handler for all of them.
        static CpuSelector()
        {
            CpuAffinityProperty.Changed.AddClassHandler<CpuSelector>(OnCPUAffinityChanged);
        }

        public CpuSelector()
        {
            int maxCpu = Environment.ProcessorCount;
            CpuList = new List<int>(maxCpu);
            for (int i = 0; i < maxCpu; i++)
                CpuList.Add(i);
            InitializeComponent();
        }

        /// <summary>
        /// The CPUAffinity property is a bitflag that represents the cpu thread affinity
        /// </summary>
        public long CpuAffinity
        {
            get => Tools.Clamp2CPUThreads(GetValue(CpuAffinityProperty));
            set => SetValue(CpuAffinityProperty, Tools.Clamp2CPUThreads(value));
        }
        
        public List<int> CpuList { get; }
        
        private static void OnCPUAffinityChanged(CpuSelector sender, AvaloniaPropertyChangedEventArgs e)
        {
            IEnumerable<CheckBox> children = tot_gui_lib.GuiExtensions.FindVisualChildren<CheckBox>(sender.CheckboxPanel);
            foreach (CheckBox child in children)
            {
                if(child.Tag is null) throw new Exception(@"CpuAffinityTags Are not setup properly.");
                child.IsChecked = (sender.CpuAffinity & (1L << (int)child.Tag)) != 0;
            }
        }
        
        private void CheckBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox)
            {
                if(checkBox.Tag is null) throw new Exception(@"CPUSelector.Tags Are not setup properly.");
                int index = (int)checkBox.Tag;
                checkBox.IsVisible = index < Environment.ProcessorCount;
                checkBox.IsChecked = (CpuAffinity & (1L << index)) != 0;
            }
        }

        private void ToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox checkBox) return;
            if(checkBox.Tag is not null && checkBox.IsChecked == true)
                CpuAffinity |= (1L << (int)checkBox.Tag);
            else if(checkBox.Tag is not null && checkBox.IsChecked == false)
                CpuAffinity &= ~(1L << (int)checkBox.Tag);
        }
    }
}