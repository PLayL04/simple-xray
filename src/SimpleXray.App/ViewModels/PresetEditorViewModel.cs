using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleXray.App.Localization;
using SimpleXray.Core.Connection;
using SimpleXray.Core.Routing;

namespace SimpleXray.App.ViewModels;

/// <summary>
/// Edits a user preset (or shows a built-in one read-only): name, unmatched-traffic target, rules,
/// and a raw JSON mode for advanced users.
/// </summary>
public sealed partial class PresetEditorViewModel : ViewModelBase
{
    private static readonly string[] Lines = ["\r\n", "\n", "\r"];

    private readonly RoutingPreset _original;
    private readonly PresetRepository _presets;
    private readonly IConnectionManager _connection;

    [ObservableProperty]
    private string _name;

    /// <summary>Index in <see cref="RouteOutbound"/> order: proxy, direct, block.</summary>
    [ObservableProperty]
    private int _defaultOutboundIndex;

    /// <summary>Index in <see cref="PresetValidator.DomainStrategies"/>.</summary>
    [ObservableProperty]
    private int _domainStrategyIndex;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormMode), nameof(JsonToggleText))]
    private bool _isJsonMode;

    [ObservableProperty]
    private string _jsonText = string.Empty;

    public PresetEditorViewModel(RoutingPreset preset, PresetRepository presets, IConnectionManager connection)
    {
        ArgumentNullException.ThrowIfNull(preset);
        _original = preset;
        _presets = presets;
        _connection = connection;
        _name = preset.DisplayName(Loc.Culture);
        Load(preset);
    }

    /// <summary>Raised when the editor should close; the argument tells whether the preset was saved.</summary>
    public event EventHandler<bool>? Closed;

    public ObservableCollection<RuleEditorViewModel> Rules { get; } = [];

    public bool IsReadOnly => _original.BuiltIn;

    public bool IsEditable => !IsReadOnly;

    public bool IsFormMode => !IsJsonMode;

    public string JsonToggleText => IsJsonMode ? Loc.Get("Editor_ShowForm") : Loc.Get("Editor_ShowJson");

    public string Title => IsReadOnly ? Loc.Get("Editor_TitleView") : Loc.Get("Editor_Title");

    [RelayCommand]
    private void AddRule()
    {
        Rules.Add(new RuleEditorViewModel(new RoutingRule(), this));
        Renumber();
    }

    internal void Remove(RuleEditorViewModel rule)
    {
        Rules.Remove(rule);
        Renumber();
    }

    internal void Move(RuleEditorViewModel rule, int delta)
    {
        var index = Rules.IndexOf(rule);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Rules.Count)
        {
            return;
        }

        Rules.Move(index, target);
        Renumber();
    }

    [RelayCommand]
    private void ToggleJson()
    {
        Error = null;
        if (!IsJsonMode)
        {
            JsonText = PresetRepository.Export(BuildFromForm());
            IsJsonMode = true;
            return;
        }

        try
        {
            var parsed = PresetSerializer.Parse(JsonText);
            Name = parsed.DisplayName(Loc.Culture);
            Load(parsed);
            IsJsonMode = false;
        }
        catch (PresetException ex)
        {
            Error = PresetItem.ErrorText(ex);
        }
    }

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private async Task SaveAsync()
    {
        Error = null;
        RoutingPreset saved;
        try
        {
            RoutingPreset edited;
            if (IsJsonMode)
            {
                var parsed = PresetSerializer.Parse(JsonText);
                edited = parsed with { Name = parsed.DisplayName(Loc.Culture) };
            }
            else
            {
                edited = BuildFromForm();
            }

            saved = _presets.Save(edited with { Id = _original.Id, Names = null, SourceUrl = _original.SourceUrl });
        }
        catch (PresetException ex)
        {
            Error = PresetItem.ErrorText(ex);
            return;
        }
        catch (IOException ex)
        {
            Error = $"{Loc.Get("Editor_SaveFailed")}: {ex.Message}";
            return;
        }

        Closed?.Invoke(this, true);
        if (_presets.Selected.Id == saved.Id)
        {
            await _connection.ReconnectIfConnectedAsync();
        }
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, false);

    /// <summary>The preset as currently shown in the form (not validated).</summary>
    internal RoutingPreset BuildFromForm() => _original with
    {
        Name = Name.Trim(),
        Names = null,
        DefaultOutbound = (RouteOutbound)Math.Clamp(DefaultOutboundIndex, 0, 2),
        DomainStrategy = PresetValidator.DomainStrategies[Math.Clamp(DomainStrategyIndex, 0, PresetValidator.DomainStrategies.Count - 1)],
        Rules = [.. Rules.Select(r => r.ToRule())],
        BuiltIn = false,
    };

    internal static IReadOnlyList<string> SplitLines(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : [.. text.Split(Lines, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private void Load(RoutingPreset preset)
    {
        DefaultOutboundIndex = (int)preset.DefaultOutbound;
        DomainStrategyIndex = Math.Max(0, PresetValidator.DomainStrategies.ToList().IndexOf(preset.DomainStrategy));
        Rules.Clear();
        foreach (var rule in preset.Rules)
        {
            Rules.Add(new RuleEditorViewModel(rule, this));
        }

        Renumber();
    }

    private void Renumber()
    {
        for (var i = 0; i < Rules.Count; i++)
        {
            Rules[i].Number = i + 1;
            Rules[i].IsFirst = i == 0;
            Rules[i].IsLast = i == Rules.Count - 1;
        }
    }
}

/// <summary>One rule in the preset editor. Lists are edited as one value per line.</summary>
public sealed partial class RuleEditorViewModel : ViewModelBase
{
    private readonly PresetEditorViewModel _owner;
    private readonly IReadOnlyList<string> _process;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private int _outboundIndex;

    [ObservableProperty]
    private string _domains;

    [ObservableProperty]
    private string _ips;

    [ObservableProperty]
    private string _port;

    /// <summary>0 = any, 1 = TCP, 2 = UDP.</summary>
    [ObservableProperty]
    private int _networkIndex;

    [ObservableProperty]
    private string _protocols;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    private int _number;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    private bool _isFirst;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    private bool _isLast;

    public RuleEditorViewModel(RoutingRule rule, PresetEditorViewModel owner)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _owner = owner;
        _process = rule.Process;
        _enabled = rule.Enabled;
        _name = rule.Name ?? string.Empty;
        _outboundIndex = (int)rule.Outbound;
        _domains = string.Join(Environment.NewLine, rule.Domain);
        _ips = string.Join(Environment.NewLine, rule.Ip);
        _port = rule.Port ?? string.Empty;
        _networkIndex = rule.Network switch { "tcp" => 1, "udp" => 2, _ => 0 };
        _protocols = string.Join(", ", rule.Protocol);
    }

    public string Header => Loc.Format("Editor_RuleNumber", Number);

    public bool IsEditable => _owner.IsEditable;

    private bool CanMoveUp() => IsEditable && !IsFirst;

    private bool CanMoveDown() => IsEditable && !IsLast;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => _owner.Move(this, -1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => _owner.Move(this, 1);

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void Remove() => _owner.Remove(this);

    public RoutingRule ToRule() => new()
    {
        Enabled = Enabled,
        Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
        Outbound = (RouteOutbound)Math.Clamp(OutboundIndex, 0, 2),
        Domain = PresetEditorViewModel.SplitLines(Domains),
        Ip = PresetEditorViewModel.SplitLines(Ips),
        Port = string.IsNullOrWhiteSpace(Port) ? null : Port.Trim(),
        Network = NetworkIndex switch { 1 => "tcp", 2 => "udp", _ => null },
        Protocol = [.. Protocols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
        Process = _process,
    };
}
