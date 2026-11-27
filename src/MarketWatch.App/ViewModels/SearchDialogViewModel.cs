using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarketWatch.App.Services;
using MarketWatch.Core.Models;
using MarketWatch.Core.Services;

namespace MarketWatch.App.ViewModels;

public sealed partial class SearchResultRowViewModel : ObservableObject
{
    public Instrument Instrument { get; }
    public string Name => Instrument.Name;
    public string DisplayCode => Instrument.DisplayCode;
    public string MarketLabel { get; }
    public string TypeLabel { get; }
    public string Source { get; }

    [ObservableProperty] private bool _isAlreadyAdded;

    public SearchResultRowViewModel(SearchResultItem item, bool alreadyAdded)
    {
        Instrument = item.Instrument;
        MarketLabel = item.MarketLabel;
        TypeLabel = item.SecurityTypeLabel;
        Source = item.Source;
        _isAlreadyAdded = alreadyAdded;
    }
}

public sealed partial class SearchDialogViewModel : ObservableObject
{
    private readonly AppStateCoordinator _coordinator;
    private CancellationTokenSource? _searchCts;

    [ObservableProperty] private string _queryText = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _statusText = "支持按代码、中文名称或拼音搜索港股 / A股 / 美股，或直接从下方常用标的添加。";

    public ObservableCollection<SearchResultRowViewModel> Results { get; } = new();

    public SearchDialogViewModel(AppStateCoordinator coordinator)
    {
        _coordinator = coordinator;
        LoadPresets(null);
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var q = QueryText?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(q))
        {
            LoadPresets(null);
            return;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        IsSearching = true;
        StatusText = $"正在搜索 \"{q}\" ...";
        Results.Clear();

        try
        {
            var existing = _coordinator.Settings.Watchlist.Select(w => w.Id).ToHashSet();

            // First match local curated catalog for instant accurate hits on indices
            var localHits = DefaultPresets.CreateFullCatalog()
                .Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                            i.DisplayCode.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                            i.Id.Code.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var seen = new HashSet<InstrumentId>();
            foreach (var inst in localHits)
            {
                if (seen.Add(inst.Id))
                {
                    Results.Add(new SearchResultRowViewModel(ToSearchItem(inst, "内置预设"), existing.Contains(inst.Id)));
                }
            }

            var remote = await _coordinator.SearchInstrumentsAsync(q, ct);
            if (ct.IsCancellationRequested)
                return;

            foreach (var item in remote)
            {
                if (seen.Add(item.Instrument.Id))
                {
                    Results.Add(new SearchResultRowViewModel(item, existing.Contains(item.Instrument.Id)));
                }
            }

            StatusText = Results.Count > 0
                ? $"共找到 {Results.Count} 个相关标的"
                : $"未找到与 \"{q}\" 匹配的标的";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"在线搜索失败（{ex.Message}），已展示本地匹配预设。";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private void FilterPresets(string? marketFilter)
    {
        Market? filter = marketFilter?.ToUpperInvariant() switch
        {
            "HK" => Market.HK,
            "CN" => Market.CN,
            "US" => Market.US,
            _ => null
        };
        LoadPresets(filter);
    }

    private void LoadPresets(Market? filter)
    {
        Results.Clear();
        var existing = _coordinator.Settings.Watchlist.Select(w => w.Id).ToHashSet();
        var items = DefaultPresets.CreateFullCatalog()
            .Where(i => !filter.HasValue || i.Id.Market == filter.Value);

        foreach (var inst in items)
        {
            Results.Add(new SearchResultRowViewModel(ToSearchItem(inst, "常用预设"), existing.Contains(inst.Id)));
        }

        StatusText = filter.HasValue
            ? $"常用{filter.Value}市场标的（共 {Results.Count} 项）"
            : $"常用多市场标的预设（共 {Results.Count} 项）";
    }

    [RelayCommand]
    private async Task AddInstrumentAsync(SearchResultRowViewModel? row)
    {
        if (row is null || row.IsAlreadyAdded)
            return;

        await _coordinator.UpdateSettingsAsync(s =>
        {
            if (s.Watchlist.All(w => w.Id != row.Instrument.Id))
            {
                s.Watchlist.Add(row.Instrument.Clone());
            }
        }, triggerImmediateRefresh: true);

        row.IsAlreadyAdded = true;
        StatusText = $"已添加 {row.Name} ({row.DisplayCode}) 到自选列表";
    }

    private static SearchResultItem ToSearchItem(Instrument inst, string source) => new()
    {
        Instrument = inst,
        MarketLabel = inst.Id.Market switch { Market.HK => "港股", Market.CN => "A股", Market.US => "美股", _ => "" },
        SecurityTypeLabel = inst.Id.AssetType switch { AssetType.Index => "指数", AssetType.Etf => "ETF", _ => "股票" },
        Source = source
    };
}
