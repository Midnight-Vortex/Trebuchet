using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData.Binding;
using ReactiveUI;

namespace Trebuchet.ViewModels.SettingFields
{
    public interface IRefreshableField
    {
        ReactiveCommand<Unit, Unit> Update { get; }
    }
    
    public interface IValueField : IRefreshableField
    {
        ReactiveCommand<Unit, Unit> Reset { get; }
        ReactiveCommand<Unit, Unit> HyperlinkClick { get; }
        bool Experiment { get; }
        bool IsDefault { get; }
        bool IsVisible { get; }
        string Title { get; }
        string Description { get; }
        bool DisplayGenericDescription { get; }
        bool DisplayDescription { get; }
        bool IsEnabled { get; }
        string Hyperlink { get; }
        
    }
    
    public abstract class FieldElement : ReactiveObject
    {
    }

    public abstract class FieldElement<F> : FieldElement where F : FieldElement<F>
    {
        private string _title = string.Empty;
        private bool _experiment = false;

        public string Title
        {
            get => _title;
            protected set => this.RaiseAndSetIfChanged(ref _title, value);
        }

        public bool Experiment
        {
            get => _experiment;
            protected set => this.RaiseAndSetIfChanged(ref _experiment, value);
        }

        public F SetTitle(string title)
        {
            Title = title;
            return (F)this;
        }

        public F SetExperiment()
        {
            _experiment = true;
            return (F)this;
        }
    }

    public abstract class DescriptiveElement<F> : FieldElement<F> where F : DescriptiveElement<F>
    {
        private string _description = string.Empty;
        private bool _displayDescription;
        private bool _displayGenericDescription = true;

        public string Description
        {
            get => _description;
            protected set => this.RaiseAndSetIfChanged(ref _description, value);
        }

        public bool DisplayDescription
        {
            get => _displayDescription;
            protected set => this.RaiseAndSetIfChanged(ref _displayDescription, value);
        }

        public bool DisplayGenericDescription
        {
            get => _displayGenericDescription;
            protected set => this.RaiseAndSetIfChanged(ref _displayGenericDescription, value);
        }

        public F SetDescription(string description)
        {
            Description = description;
            DisplayDescription = !string.IsNullOrEmpty(Description) && DisplayGenericDescription;
            return (F)this;
        }
    
        public F ToggleGenericDescription(bool toggle)
        {
            DisplayGenericDescription = toggle;
            DisplayDescription = !string.IsNullOrEmpty(Description) && DisplayGenericDescription;
            return (F)this;
        }
    }
    
    public abstract class Field<F> : DescriptiveElement<F> where F : Field<F>
    {

        private bool _isVisible = true;
        private string _hyperlink = string.Empty;
        private bool _isEnabled = true;

        public Field()
        {
            HyperlinkClick = ReactiveCommand.Create(() => tot_lib.Utils.OpenWeb(Hyperlink));
        }

        public bool IsVisible
        {
            get => _isVisible;
            set => this.RaiseAndSetIfChanged(ref _isVisible, value);
        }

        public string Hyperlink
        {
            get => _hyperlink;
            protected set => this.RaiseAndSetIfChanged(ref _hyperlink, value);
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            protected set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
        }

        public ReactiveCommand<Unit, Unit> HyperlinkClick { get; }
        public abstract ReactiveCommand<Unit, Unit> Reset { get; }
    }
    
    public abstract class Field<F,T> : Field<F>, IValueField where F : Field<F,T>
    {
        private Func<T>? _getter;
        private Action<T>? _setter;
        private Func<T>? _defaultBuilder;
        private T _value;
        private ObservableAsPropertyHelper<bool> _isDefault;

        protected Field(T initialValue)
        {
            _value = initialValue;
            _defaultBuilder = () => initialValue;

            _isDefault = this.WhenAnyValue(x => x.Value, x => x.DefaultBuilder, (v, d) =>
            {
                if (d is null) return true;
                return AreValuesEqual(v, d.Invoke());
            }).ToProperty(this, x => x.IsDefault);
            
            Reset = ReactiveCommand.Create(
                canExecute: this.WhenAnyValue(x => x.IsDefault, x => !x),
                execute: () =>
                {
                    if (DefaultBuilder is not null)
                        Value = DefaultBuilder.Invoke();
                });

            Update = ReactiveCommand.Create(() =>
            {
                if (Getter is not null)
                    Value = Getter.Invoke();
            });
            
            ValueChanged = ReactiveCommand.Create<T, bool>((v) =>
            {
                if (Getter is null || Setter is null) return false;
                var value = Getter.Invoke();
                if (AreValuesEqual(value, v)) return false;
                Setter?.Invoke(v);
                return true;
            });

            this.WhenAnyValue(x => x.Value)
                .InvokeCommand(ValueChanged);
        }

        public Func<T>? Getter
        {
            get => _getter;
            protected set => this.RaiseAndSetIfChanged(ref _getter, value);
        }

        public Action<T>? Setter
        {
            get => _setter;
            protected set => this.RaiseAndSetIfChanged(ref _setter, value);
        }

        public Func<T>? DefaultBuilder
        {
            get => _defaultBuilder;
            protected set => this.RaiseAndSetIfChanged(ref _defaultBuilder, value);
        }

        public T Value
        {
            get => _value;
            set => this.RaiseAndSetIfChanged(ref _value, value);
        }
        
        public bool IsDefault
        {
            get => _isDefault.Value;
        }

        public override ReactiveCommand<Unit, Unit> Reset { get; }
        public ReactiveCommand<T, bool> ValueChanged { get; }
        public ReactiveCommand<Unit,Unit> Update { get; }

        public F SetDefault(Func<T> defGenerator)
        {
            DefaultBuilder = defGenerator;
            return (F)this;
        }

        public F SetSetter(Action<T> setter)
        {
            Setter = setter;
            return (F)this;
        }

        public F SetEnabled(bool enabled)
        {
            IsEnabled = enabled;
            return (F)this;
        } 

        public F SetGetter(Func<T> getter)
        {
            Getter = getter;
            Value = Getter.Invoke();
            return (F)this;
        }

        public F WhenFieldChanged(ReactiveCommand<Unit,Unit> command)
        {
            ArgumentNullException.ThrowIfNull(command);
            ValueChanged.Where(x => x).Select(_ => Unit.Default).InvokeCommand(command);
            return (F)this;
        }

        public F UpdateWith<OF, OT>(Field<OF, OT> field) where OF : Field<OF,OT>
        {
            field.ValueChanged.Where(x => x).Subscribe((_) => Update.Execute().Subscribe());
            return (F)this;
        }

        protected virtual bool AreValuesEqual(T valueA, T valueB)
        {
            return EqualityComparer<T>.Default.Equals(valueA, valueB);
        }
    }
}