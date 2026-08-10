using System.Collections.Concurrent;
using DepotDownloader;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Internal;
using SteamWorksWebAPI;
using SteamWorksWebAPI.Interfaces;
using tot_lib.OsSpecific;

namespace TrebuchetLib.Services;

public class Steam : IDebugListener, IDisposable
{
    public Steam(
        ILogger<Steam> logger, 
        IOsPlatformSpecific osSpecific,
        AppSetup appSetup, 
        IProgressCallback<DepotDownloader.Progress> progress)
    {
        _logger = logger;
        _osSpecific = osSpecific;
        _appSetup = appSetup;
        _progress = progress;

        DebugLog.AddListener(this);
        Util.ConsoleWriteRedirect += OnConsoleWriteRedirect;
        ContentDownloader.Config.RememberPassword = false;
        ContentDownloader.Config.DownloadManifestOnly = false;
        ContentDownloader.Config.LoginID = null;
        ContentDownloader.Config.Progress = progress;
        UpdateDownloaderConfig();
        // SteamKit session is created on first use (Connect / workshop / download).
        _sessionLazy = new Lazy<Steam3Session>(CreateSession, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private Steam3Session CreateSession()
    {
        var session = ContentDownloader.InitializeSteam3(null, null, _appSetup.Config.CellId);
        session.Connected += (_, _) => Connected?.Invoke(this, EventArgs.Empty);
        session.Disconnected += (_, _) => Disconnected?.Invoke(this, EventArgs.Empty);
        return session;
    }
        
    private readonly ILogger<Steam> _logger;
    private readonly IOsPlatformSpecific _osSpecific;
    private readonly AppSetup _appSetup;
    private readonly IProgressCallback<DepotDownloader.Progress> _progress;
    private readonly Lazy<Steam3Session> _sessionLazy;
    private Steam3Session _session => _sessionLazy.Value;
    private readonly Dictionary<ulong, SteamWorksWebAPI.PublishedFile> _publishedFiles = [];
    private DateTime _lastCacheClear = DateTime.MinValue;
    private SteamStatus _status = SteamStatus.StandBy;
    private CancellationTokenSource? _cts;

    public event EventHandler? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler<SteamStatus>? StatusChanged;
    public bool IsConnected => _sessionLazy.IsValueCreated && _session.IsLoggedOn;

    public SteamStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            OnStatusChanged(_status);
        }
    }

    public void ClearSteamCache()
    {
        UpdateDownloaderConfig();
        Tools.DeleteIfExists(ContentDownloader.Config.DepotConfigDirectory);
    }

    public async Task Connect()
    {
        await _session.Connect();
    }

    public void Disconnect(bool sendLogOff = true)
    {
        _session.Disconnect(sendLogOff);
    }

    public void SetTemporaryProgress(IProgress<DepotDownloader.Progress> progress)
    {
        ContentDownloader.Config.Progress = progress;
    }

    public void RestoreProgress()
    {
        ContentDownloader.Config.Progress = _progress;
    }
        
    public async Task<List<PublishedMod>> RequestModDetails(List<ulong> list)
    {
        var pending = list.ToList();
        var results = GetCache(pending);
        if (pending.Count > 0)
        {
            using (_logger.BeginScope((@"ModList", pending)))
                _logger.LogInformation(@"Seeking mod details");
            try
            {
                var response = await SteamRemoteStorage.GetPublishedFileDetails(new GetPublishedFileDetailsQuery(pending),
                    CancellationToken.None);
                foreach (var r in response.PublishedFileDetails)
                {
                    results.Add(r);
                    _publishedFiles[r.PublishedFileID] = r;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, @"Could not download mod infos");
            }
        }

        await EnrichEmptyTagsFromSteamKit(results);
        return GetPublishedModFiles(results).ToList();
    }

    public void ClearModDetailsCache()
    {
        _logger.LogInformation(@"Invalidating mod details cache");
        _publishedFiles.Clear();
        _lastCacheClear = DateTime.UtcNow;
    }

    /// <summary>
    /// Throws when any workshop mod is tagged for the wrong game edition (e.g. Legacy pak on Enhanced).
    /// </summary>
    public async Task EnsureModsCompatibleWithEdition(IEnumerable<ulong> modIds)
    {
        var ids = modIds.Distinct().ToList();
        if (ids.Count == 0) return;

        var details = await RequestModDetails(ids);
        foreach (var mod in details.Where(m => m.Tags.Count == 0))
        {
            _logger.LogWarning(
                "Workshop mod {Title} ({PublishedFileId}) has no Steam tags; using title fallback for edition check (impliesEnhanced={Implies})",
                mod.Title, mod.PublishedFileId, Constants.TitleImpliesEnhanced(mod.Title));
        }

        var incompatible = details
            .Where(m => !Constants.IsWorkshopModCompatible(_appSetup.Edition, m.Tags, m.ConsumerAppId, m.Title))
            .Select(m => m.Title)
            .ToList();

        if (incompatible.Count == 0) return;

        throw new InvalidOperationException(
            $"These workshop mods are not compatible with {Constants.GetEditionDisplayName(_appSetup.Edition)}: {string.Join(", ", incompatible)}");
    }

    /// <summary>
    /// Count the amount of instances currently installed. This does not verify if the files are valid, just the main binary.
    /// </summary>
    /// <returns></returns>
    public int GetInstalledInstances()
    {
        int count = 0;

        string folder = Path.Combine(_appSetup.GetBaseInstancePath());
        if (!Directory.Exists(folder))
            return 0;

        string[] instances = Directory.GetDirectories(folder);
        foreach (string instance in instances)
        {
            string bin = Path.Combine(instance, Constants.FolderGameBinaries, Constants.FileServerBin);
            if (File.Exists(bin))
                count++;
        }

        return count;
    }

    /// <summary>
    /// Get the installed local build id of a specified instance
    /// </summary>
    /// <param name="instance"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    public ulong GetInstanceBuildId(int instance)
    {
        string manifest = Path.Combine(
            _appSetup.GetInstancePath(instance),
            Constants.FileBuildID);

        if (!File.Exists(manifest))
            return 0;

        string content = File.ReadAllText(manifest);
        if (ulong.TryParse(content, out ulong buildID))
            return buildID;
        return 0;
    }

    /// <summary>
    /// Force refresh of the steam app info cache and get the current build id of the server app.
    /// </summary>
    /// <returns></returns>
    public async Task<uint> GetSteamBuildId()
    {
        UpdateDownloaderConfig();

        if (!await WaitSteamConnectionAsync())
            throw new Exception("Could not connect to steam");

        return await Task.Run(async () =>
        {
            try
            {
                await _session.RequestAppInfo(_appSetup.ServerAppId, true);
                return ContentDownloader.GetSteam3AppBuildNumber(_appSetup.ServerAppId,
                    ContentDownloader.DEFAULT_BRANCH);
            }
            catch(Exception ex)
            {
                _logger.LogWarning(ex, "Cannot retrieve build ID");
                return 0U;
            }
        });
    }

    public async Task<CPublishedFile_QueryFiles_Response?> QueryWorkshopSearch(uint appId, string searchTerms, uint perPage,
        uint page, string? requiredTags = null, string? excludedTags = null)
    {
        var data = new Dictionary<string, object>
        {
            {@"search", searchTerms},
            {@"perPage", perPage},
            {@"page", page},
            {@"requiredTags", requiredTags ?? string.Empty},
            {@"excludedTags", excludedTags ?? string.Empty}
        };
        using var scope = _logger.BeginScope(data);
        _logger.LogInformation(@"Begin workshop search");
        try
        {
            // Server-side requiredtags/excludedtags + return_tags (DepotDownloader ForkTrebuchet).
            var response = await _session.QueryPublishedFileSearch(
                appId, searchTerms, perPage, page, requiredTags, excludedTags);

            // Client-side safety net when tags are present; server-side filter handles untagged rows.
            FilterWorkshopSearchByTags(response, requiredTags, excludedTags);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed");
            return null;
        }
    }

    private void FilterWorkshopSearchByTags(CPublishedFile_QueryFiles_Response response, string? requiredTag,
        string? excludedTag)
    {
        if (string.IsNullOrEmpty(requiredTag) && string.IsNullOrEmpty(excludedTag))
            return;

        var before = response.publishedfiledetails.Count;
        for (var i = response.publishedfiledetails.Count - 1; i >= 0; i--)
        {
            var file = response.publishedfiledetails[i];
            if (!WorkshopFilePassesTagFilter(file, requiredTag, excludedTag))
                response.publishedfiledetails.RemoveAt(i);
        }

        var removed = before - response.publishedfiledetails.Count;
        if (removed > 0)
            _logger.LogInformation(@"Filtered {Removed} workshop results by tags (required={Required}, excluded={Excluded})",
                removed, requiredTag ?? @"-", excludedTag ?? @"-");
    }

    private static bool WorkshopFilePassesTagFilter(PublishedFileDetails file, string? requiredTag, string? excludedTag)
    {
        var tags = file.tags;
        // Search rows often omit tags; trust server-side requiredtags/excludedtags filtering.
        if (tags is null || tags.Count == 0)
            return true;

        if (!string.IsNullOrEmpty(requiredTag) &&
            !tags.Any(t => string.Equals(t.tag, requiredTag, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (!string.IsNullOrEmpty(excludedTag) &&
            tags.Any(t => string.Equals(t.tag, excludedTag, StringComparison.OrdinalIgnoreCase)))
            return false;

        return true;
    }

    public IEnumerable<PublishedMod> GetPublishedModFiles(List<SteamWorksWebAPI.PublishedFile> files)
    {
        var updated = GetUpdatedUGCFileIDs(files.GetManifestKeyValuePairs()).ToList();

        foreach (var file in files)
        {
            var status = updated
                .FirstOrDefault(x => x.PublishedId == file.PublishedFileID, UGCFileStatus.Default(0));
            if (status.PublishedId != 0)
                yield return new PublishedMod(file, status);
            else
                yield return new PublishedMod(file, new UGCFileStatus(file.PublishedFileID, UGCStatus.Missing));
        }
    }
        
    public List<UGCFileStatus> CheckModsForUpdate(ICollection<(ulong pubId, ulong manifestId)> mods)
    {
        var updated = GetUpdatedUGCFileIDs(mods).ToList();
        var known = updated.Select(x => x.PublishedId).ToHashSet();
        var missing = new ConcurrentBag<ulong>();
        Parallel.ForEach(mods, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2)
        }, item =>
        {
            var (pubId, _) = item;
            if (known.Contains(pubId)) return;
            if (!_appSetup.TryGetModPath(pubId.ToString(), out _))
                missing.Add(pubId);
        });
        foreach (var id in missing)
            updated.Add(new UGCFileStatus(id, UGCStatus.Missing));
        return updated;
    }

    /// <summary>
    /// Compare a list of manifest ID from Published Files with the local steam cache
    /// </summary>
    /// <param name="keyValuePairs"></param>
    /// <returns></returns>
    public IEnumerable<UGCFileStatus> GetUpdatedUGCFileIDs(IEnumerable<(ulong pubId, ulong manisfestId)> keyValuePairs)
    {
        UpdateDownloaderConfig();

        var depotConfigStore = DepotConfigStore.LoadInstanceFromFile(
            Path.Combine(
                _appSetup.GetWorkshopFolder(), 
                ContentDownloader.CONFIG_DIR, 
                ContentDownloader.DEPOT_CONFIG));
        foreach (var (pubID, manisfestID) in keyValuePairs)
        {
            if (!depotConfigStore.InstalledUGCManifestIDs.TryGetValue(pubID, out ulong manisfest))
            {
                yield return new UGCFileStatus(pubID, UGCStatus.Missing);
                continue;
            }

            if (manisfest != manisfestID)
            {
                if (manisfest == ContentDownloader.INVALID_MANIFEST_ID)
                    yield return new UGCFileStatus(pubID, UGCStatus.Corrupted);
                else
                    yield return new UGCFileStatus(pubID, UGCStatus.Updatable);
                continue;
            }

            yield return new UGCFileStatus(pubID, UGCStatus.UpToDate);
        }
    }

    public void ClearUGCFileIdsFromStorage(IEnumerable<ulong> idList)
    {
        UpdateDownloaderConfig();
        DepotConfigStore.LoadFromFile(
            Path.Combine(
                _appSetup.GetWorkshopFolder(), 
                ContentDownloader.CONFIG_DIR, 
                ContentDownloader.DEPOT_CONFIG));
            
        foreach (var id in idList)
            DepotConfigStore.Instance.InstalledUGCManifestIDs.Remove(id);
        DepotConfigStore.Save();
    }

    public ICollection<ulong> GetUGCFileIdsFromStorage()
    {
        UpdateDownloaderConfig();
        DepotConfigStore.LoadFromFile(
            Path.Combine(
                _appSetup.GetWorkshopFolder(), 
                ContentDownloader.CONFIG_DIR, 
                ContentDownloader.DEPOT_CONFIG));
        return DepotConfigStore.Instance.InstalledUGCManifestIDs.Keys.ToList();
    }

    /// <summary>
    /// Remove all junctions present in any server instance folder. Used before update to avoid crash du to copy error on junction folders.
    /// </summary>
    public void RemoveAllSymbolicLinks()
    {
        string folder = _appSetup.GetBaseInstancePath();
        if (!Directory.Exists(folder))
            return;
        string[] instances = Directory.GetDirectories(folder);
        foreach (string instance in instances)
            _osSpecific.RemoveSymbolicLink(Path.Combine(instance, Constants.FolderGameSave));
    }

    /// <summary>
    /// Update a list of mods from the steam workshop.
    /// </summary>
    /// <param name="enumerable"></param>
    /// <param name="cts"></param>
    /// <returns></returns>
    public async Task UpdateMods(IEnumerable<ulong> enumerable)
    {
        using var status = EnterStatus(SteamStatus.UpdatingMods, out var cts);
        if (!await WaitSteamConnectionAsync()) return;

        UpdateDownloaderConfig();
        ContentDownloader.Config.InstallDirectory = Path.Combine(_appSetup.GetWorkshopFolder());
        await Task.Run(async () =>
        {
            try
            {
                await ContentDownloader.DownloadUGCAsync([Constants.AppIDLiveClient, Constants.AppIDTestLiveClient],
                    enumerable, ContentDownloader.DEFAULT_BRANCH, cts);
            }
            catch(Exception ex)
            {
                _logger.LogWarning(ex, "Cannot update the mods");
            }
        });
    }

    /// <summary>
    /// Update instance 0 using steamcmd, then copy the files of instance0 to update other instances.
    /// </summary>
    /// <returns></returns>
    public async Task UpdateServerInstances()
    {
        if (_appSetup.Config.ServerInstanceCount <= 0) return;
        using var status = EnterStatus(SteamStatus.UpdatingServers, out var cts);

        if (!await WaitSteamConnectionAsync()) return;

        _logger.LogInformation("Updating all server instances...");
        int count = _appSetup.Config.ServerInstanceCount;
        for (int i = 0; i < count; i++)
        {
            if (i == 0)
            {
                await UpdateServer(i, cts);
            }
            else
            {
                await UpdateServerFromInstance0(i, cts.Token);
            }
        }
    }

    public void WriteLine(string category, string msg)
    {
        _logger.LogInformation($"[{category}] {msg}");
    }

    public void Dispose()
    {
        if (_sessionLazy.IsValueCreated)
            Disconnect();
    }
    
    public void CancelOperation()
    {
        if (_cts is null || Status == SteamStatus.StandBy) return;
        _cts.Cancel();
    }
        
    private void OnStatusChanged(SteamStatus e)
    {
        StatusChanged?.Invoke(this, e);
    }
    
    public async Task<bool> WaitSteamConnectionAsync()
    {
        if (IsConnected) return true;
        await _session.Connect();
        return IsConnected;
    }

    private IDisposable EnterStatus(SteamStatus status, out CancellationTokenSource cts)
    {
        if (status == SteamStatus.StandBy)
            throw new ArgumentException(nameof(status));

        if (Status != SteamStatus.StandBy)
            throw new OperationCanceledException("Operation is already on going");

        Status = status;
        _cts = new CancellationTokenSource();
        cts = _cts;
        return new DisposableAction(() =>
        {
            Status = SteamStatus.StandBy;
            _cts.Dispose();
            _cts = null;
        });
    }

    private void OnConsoleWriteRedirect(string obj)
    {
        obj = obj.Replace(Environment.NewLine, string.Empty);
        _logger.LogInformation($"[ContentDownloader] {obj}");
    }
        
    /// <summary>
    /// Update a server instance other than 0, from the content of instance 0.
    /// </summary>
    /// <param name="instanceNumber"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    /// <exception cref="DirectoryNotFoundException"></exception>
    private async Task UpdateServerFromInstance0(int instanceNumber, CancellationToken token)
    {
        if (_appSetup.Config.ServerInstanceCount <= 0) return;
        if (instanceNumber == 0)
            throw new Exception("Can't update instance 0 with itself.");

        _logger.LogInformation("Updating server instance {0} from instance 0.", instanceNumber);

        string instance = _appSetup.GetInstancePath(instanceNumber);
        string instance0 = _appSetup.GetInstancePath(0);

        if (!Directory.Exists(instance0))
            throw new DirectoryNotFoundException($"{instance0} was not found.");

        _osSpecific.RemoveSymbolicLink(Path.Combine(instance0, Constants.FolderGameSave));
        _osSpecific.RemoveSymbolicLink(Path.Combine(instance, Constants.FolderGameSave));
        Tools.DeleteIfExists(instance);
        Tools.CreateDir(instance);

        await Tools.DeepCopyAsync(instance0, instance, token);
    }
        
    /// <summary>
    /// Performs a server update on a targeted instance.
    /// </summary>
    /// <param name="instanceNumber"></param>
    /// <param name="cts"></param>
    /// <param name="reinstall"></param>
    /// <returns></returns>
    private async Task UpdateServer(int instanceNumber, CancellationTokenSource cts, bool reinstall = false)
    {
        if (_appSetup.Config.ServerInstanceCount <= 0) return;
            
        if (!await WaitSteamConnectionAsync().ConfigureAwait(false)) return;

        _logger.LogInformation($"Updating server instance {instanceNumber}.");

        string instance = _appSetup.GetInstancePath(instanceNumber);
        if (reinstall)
        {
            _osSpecific.RemoveSymbolicLink(Path.Combine(instance, Constants.FolderGameSave));
            Tools.DeleteIfExists(instance);
        }

        Tools.CreateDir(instance);
        UpdateDownloaderConfig();
        ContentDownloader.Config.InstallDirectory = instance;
        await Task.Run(async () =>
        {
            try
            {
                await ContentDownloader.DownloadAppAsync(_appSetup.ServerAppId, [],
                    ContentDownloader.DEFAULT_BRANCH, null, null, null, false, false, cts);
                var buildId = await GetSteamBuildId();
                SetInstanceBuildId(instanceNumber, buildId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, @"Failed to update server");
            }
        }, cts.Token).ConfigureAwait(false);
    }
        
    private void UpdateDownloaderConfig()
    {
        ContentDownloader.Config.CellID = 0; //TODO: Offer regional download selection
        ContentDownloader.Config.MaxDownloads = _appSetup.Config.MaxDownloads;
        ContentDownloader.Config.DepotConfigDirectory = Path.Combine(_appSetup.GetWorkshopFolder(), ContentDownloader.CONFIG_DIR);
        // Per-edition Steam account store — same pattern as TestLive: Workshop/{VersionFolder}/account.config
        AccountSettingsStore.LoadFromFile(
            Path.Combine(_appSetup.GetWorkshopFolder(), _appSetup.VersionFolder, "account.config"));
    }
    
    /// <summary>
    /// Web API GetPublishedFileDetails often omits tags; SteamKit GetDetails with includetags fills them in.
    /// </summary>
    private async Task EnrichEmptyTagsFromSteamKit(List<SteamWorksWebAPI.PublishedFile> files)
    {
        var idsNeedingTags = files
            .Where(f => f.Tags is null || f.Tags.Length == 0)
            .Select(f => f.PublishedFileID)
            .Distinct()
            .ToList();
        if (idsNeedingTags.Count == 0) return;

        if (!IsConnected && !await WaitSteamConnectionAsync())
        {
            _logger.LogWarning("Cannot enrich workshop tags: not connected to Steam");
            return;
        }

        try
        {
            foreach (var appId in new[] { Constants.AppIDLiveClient, Constants.AppIDTestLiveClient })
            {
                var stillNeeding = idsNeedingTags
                    .Where(id => _publishedFiles.TryGetValue(id, out var f) && (f.Tags is null || f.Tags.Length == 0))
                    .ToList();
                if (stillNeeding.Count == 0) break;

                var details = await _session.GetPublishedFileDetails(appId, stillNeeding);
                foreach (var detail in details)
                {
                    if (detail.tags is null || detail.tags.Count == 0) continue;
                    if (!_publishedFiles.TryGetValue(detail.publishedfileid, out var file)) continue;

                    file.Tags = detail.tags
                        .Select(t => new SteamWorksWebAPI.SteamTag { tag = t.tag })
                        .Where(t => !string.IsNullOrWhiteSpace(t.tag))
                        .ToArray();
                }
            }

            foreach (var id in idsNeedingTags)
            {
                if (_publishedFiles.TryGetValue(id, out var file) && (file.Tags is null || file.Tags.Length == 0))
                    _logger.LogWarning("Workshop mod {PublishedFileId} still has no tags after Steam enrichment", id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enrich workshop tags from Steam session");
        }
    }

    private List<SteamWorksWebAPI.PublishedFile> GetCache(List<ulong> list)
    {
        List<SteamWorksWebAPI.PublishedFile> results = [];
        // Keep workshop details longer to avoid Steam round-trips on large modlists.
        if ((DateTime.UtcNow - _lastCacheClear).TotalMinutes > 15.0)
            ClearModDetailsCache();
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var mod = list[i];
            if (_publishedFiles.TryGetValue(mod, out var file))
            {
                list.RemoveAt(i);
                results.Add(file);
            }
        }

        return results;
    }
    
    private void SetInstanceBuildId(int instance, ulong buildId)
    {
        string manifest = Path.Combine(
            _appSetup.GetInstancePath(instance),
            Constants.FileBuildID);
            
        File.WriteAllText(manifest, buildId.ToString());
    }
}