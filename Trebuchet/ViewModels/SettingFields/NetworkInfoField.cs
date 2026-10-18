using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Sockets;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using Trebuchet.Assets;
using TrebuchetLib;
using TrebuchetLib.Services;

namespace Trebuchet.ViewModels.SettingFields;

/// <summary>
/// DSL "Network Configuration": adapters/IPs/MAC, external IP, port test and connection info for a server profile.
/// </summary>
public class NetworkInfoField : DescriptiveElement<NetworkInfoField>
{
    public NetworkInfoField(Func<ServerProfile> profile, Func<MacroContext> macroContext, Action<string> useAsMultiHome)
    {
        _profile = profile;
        _macroContext = macroContext;

        Refresh = ReactiveCommand.CreateFromTask(OnRefresh);
        TestPorts = ReactiveCommand.CreateFromTask(OnTestPorts);
        UseAsMultiHome = ReactiveCommand.Create(() =>
        {
            if (SelectedAdapter is not null) useAsMultiHome(SelectedAdapter.Address);
        });
        CopyConnection = ReactiveCommand.CreateFromTask(OnCopyConnection);
        CopyMac = ReactiveCommand.CreateFromTask(async () =>
        {
            if (SelectedAdapter is { MacAddress.Length: > 0 } adapter)
                await Utils.Utils.SetClipboardText(adapter.MacAddress);
        });

        LoadAdapters();
    }

    private readonly Func<ServerProfile> _profile;
    private readonly Func<MacroContext> _macroContext;
    private List<NetworkAdapterInfo> _adapters = [];
    private NetworkAdapterInfo? _selectedAdapter;
    private string _externalIp = ExternalIpProvider.Cached;
    private string _status = string.Empty;
    private bool _busy;

    public List<NetworkAdapterInfo> Adapters
    {
        get => _adapters;
        private set => this.RaiseAndSetIfChanged(ref _adapters, value);
    }

    public NetworkAdapterInfo? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedAdapter, value);
            this.RaisePropertyChanged(nameof(MacAddress));
        }
    }

    public string MacAddress => SelectedAdapter?.MacAddress ?? string.Empty;

    public string ExternalIp
    {
        get => _externalIp;
        private set => this.RaiseAndSetIfChanged(ref _externalIp, value);
    }

    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public bool Busy
    {
        get => _busy;
        private set => this.RaiseAndSetIfChanged(ref _busy, value);
    }

    public ObservableCollection<PortTestResult> PortResults { get; } = [];

    public ReactiveCommand<Unit, Unit> Refresh { get; }
    public ReactiveCommand<Unit, Unit> TestPorts { get; }
    public ReactiveCommand<Unit, Unit> UseAsMultiHome { get; }
    public ReactiveCommand<Unit, Unit> CopyConnection { get; }
    public ReactiveCommand<Unit, Unit> CopyMac { get; }

    private void LoadAdapters()
    {
        var profile = _profile();
        Adapters = NetworkTools.GetLocalAdapters();
        SelectedAdapter = Adapters.FirstOrDefault(x => x.Address == profile.MultiHomeAddress.Trim())
                          ?? Adapters.FirstOrDefault();
    }

    private async Task OnRefresh()
    {
        Busy = true;
        try
        {
            LoadAdapters();
            ExternalIp = await ExternalIpProvider.GetAsync(force: true);
            Status = string.IsNullOrEmpty(ExternalIp) ? Resources.NetworkExternalIpFailed : string.Empty;
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task OnTestPorts()
    {
        Busy = true;
        try
        {
            var profile = _profile();
            var bind = profile.EnableMultiHome ? profile.MultiHomeAddress : null;
            var tests = new List<(string label, int port, ProtocolType protocol)>
            {
                (Resources.SettingServerGameClientPort, profile.GameClientPort, ProtocolType.Udp),
                (Resources.SettingServerRawUDPPort, profile.GameClientPort + 1, ProtocolType.Udp),
                (Resources.SettingServerSourceQueryPort, profile.SourceQueryPort, ProtocolType.Udp),
            };
            if (profile.EnableRCon)
                tests.Add((Resources.SettingServerRConPort, profile.RConPort, ProtocolType.Tcp));

            var results = await Task.Run(() => tests
                .Select(t => NetworkTools.TestPort(t.label, t.port, t.protocol, bind))
                .ToList());
            PortResults.Clear();
            foreach (var result in results)
                PortResults.Add(result);
            Status = results.All(x => x.Available) ? Resources.NetworkPortsFree : Resources.NetworkPortsInUse;
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task OnCopyConnection()
    {
        if (string.IsNullOrEmpty(ExternalIpProvider.Cached))
            ExternalIp = await ExternalIpProvider.GetAsync();
        var text = Utils.Utils.BuildConnectionInfo(_macroContext());
        Status = await Utils.Utils.SetClipboardText(text) ? Resources.ConnectionInfoCopied : string.Empty;
    }
}
