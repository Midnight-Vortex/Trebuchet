// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;
using SteamKit2.Internal;

namespace DepotDownloader
{
    public class Steam3Session : IDisposable
    {
        // Bounds the whole connect + logon wait of one Connect() call, so it can never hang forever.
        static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);
        // A non-anonymous logon may wait for a Steam Guard code typed by the user.
        static readonly TimeSpan AuthenticatedConnectTimeout = TimeSpan.FromMinutes(5);
        // How long Disconnect() waits for the pump to handle the resulting DisconnectedCallback.
        static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(5);
        // CDN auth tokens are dropped this long before they expire (Expiration is UTC), so they get renewed in time.
        static readonly TimeSpan CDNAuthTokenExpiryMargin = TimeSpan.FromMinutes(1);

        // The callback handlers run on the callback pump thread while callers read these flags from other
        // threads (Steam.IsConnected, Connect(), the download code), hence the volatile backing fields.
        volatile bool isLoggedOn;
        volatile bool isConnecting;

        public bool IsLoggedOn { get => isLoggedOn; private set => isLoggedOn = value; }
        public bool IsConnecting { get => isConnecting; private set => isConnecting = value; }

        public ReadOnlyCollection<SteamApps.LicenseListCallback.License> Licenses
        {
            get;
            private set;
        }

        // Concurrent: the periodic build check (every few minutes) and a running download fill them at the same
        // time, and a plain Dictionary written from two threads can be corrupted for good.
        public ConcurrentDictionary<uint, ulong> AppTokens { get; } = [];
        public Dictionary<uint, ulong> PackageTokens { get; } = [];
        public ConcurrentDictionary<uint, byte[]> DepotKeys { get; } = [];
        public ConcurrentDictionary<(uint, string), TaskCompletionSource<SteamContent.CDNAuthToken>> CDNAuthTokens { get; } = [];
        public ConcurrentDictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> AppInfo { get; } = [];
        public ConcurrentDictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> PackageInfo { get; } = [];
        public ConcurrentDictionary<string, byte[]> AppBetaPasswords { get; } = [];

        public SteamClient steamClient;
        public SteamUser steamUser;
        public SteamContent steamContent;
        readonly SteamApps steamApps;
        readonly SteamCloud steamCloud;
        readonly PublishedFile steamPublishedFile;

        readonly CallbackManager callbacks;

        readonly bool authenticatedUser;
        volatile bool bConnecting;
        volatile bool bAborted;
        volatile bool bExpectingDisconnectRemote;
        volatile bool bIsConnectionRecovery;
        int connectionBackoff;
        int disconnectCount; // number of DisconnectedCallbacks handled, Disconnect() waits for it to change
        int authEpoch;
        volatile AuthSession authSession;

        // Guards the connection state transitions (Connect, Disconnect, reconnect) between callers and the pump thread.
        readonly Lock stateLock = new();

        // SteamKit only runs callback handlers (connected, disconnected, logged on/off) while something pumps the
        // CallbackManager. A single background thread does that for the whole life of the session: without it a
        // connection lost after the first logon is never noticed nor recovered, and SteamKit's callback queue grows forever.
        readonly Lock pumpLock = new();
        volatile Thread callbackPump;
        volatile bool pumpStopping;

        // Posted to wake the pump up when the session is disposed; no handler is subscribed to it.
        sealed class PumpWakeUpCallback : CallbackMsg
        {
        }

        public event EventHandler Disconnected;
        public event EventHandler Connected;

        // input
        readonly SteamUser.LogOnDetails logonDetails;

        public Steam3Session(SteamUser.LogOnDetails details, uint cellId = 0)
        {
            this.logonDetails = details;
            this.authenticatedUser = details.Username != null;

            var clientConfiguration = SteamConfiguration.Create(config =>
                config
                    .WithHttpClientFactory(static purpose => HttpClientFactory.CreateHttpClient())
                    .WithCellID(cellId)
            );

            this.steamClient = new SteamClient(clientConfiguration);

            this.steamUser = this.steamClient.GetHandler<SteamUser>();
            this.steamApps = this.steamClient.GetHandler<SteamApps>();
            this.steamCloud = this.steamClient.GetHandler<SteamCloud>();
            var steamUnifiedMessages = this.steamClient.GetHandler<SteamUnifiedMessages>();
            this.steamPublishedFile = steamUnifiedMessages.CreateService<PublishedFile>();
            this.steamContent = this.steamClient.GetHandler<SteamContent>();

            this.callbacks = new CallbackManager(this.steamClient);

            this.callbacks.Subscribe<SteamClient.ConnectedCallback>(ConnectedCallback);
            this.callbacks.Subscribe<SteamClient.DisconnectedCallback>(DisconnectedCallback);
            this.callbacks.Subscribe<SteamUser.LoggedOnCallback>(LogOnCallback);
            this.callbacks.Subscribe<SteamUser.LoggedOffCallback>(LoggedOffCallback);
            this.callbacks.Subscribe<SteamApps.LicenseListCallback>(LicenseListCallback);
        }

        public delegate bool WaitCondition();

        TimeSpan LogOnTimeout => authenticatedUser ? AuthenticatedConnectTimeout : ConnectTimeout;

        /// <summary>
        /// Runs <paramref name="submitter"/> and waits until <paramref name="waiter"/> is true, the session is aborted
        /// or the logon timeout elapsed. Callbacks are handled by the pump thread, this only waits for the condition.
        /// </summary>
        public bool WaitUntilCallback(Action submitter, WaitCondition waiter)
        {
            EnsureCallbackPump();
            submitter();

            var deadline = DateTime.UtcNow + LogOnTimeout;
            while (!bAborted && !waiter() && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }

            return bAborted;
        }

        public bool WaitForCredentials()
        {
            if (IsLoggedOn || bAborted)
                return IsLoggedOn;

            WaitUntilCallback(() => { }, () => IsLoggedOn);

            return IsLoggedOn;
        }

        async Task<bool> WaitForLogOnAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!IsLoggedOn && !bAborted && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }

            return IsLoggedOn;
        }

        void EnsureCallbackPump()
        {
            lock (pumpLock)
            {
                if (callbackPump != null || pumpStopping)
                    return;

                callbackPump = new Thread(RunCallbackPump)
                {
                    IsBackground = true,
                    Name = "SteamKit callback pump",
                };
                callbackPump.Start();
            }
        }

        void RunCallbackPump()
        {
            while (!pumpStopping)
            {
                try
                {
                    // Blocks until a callback is posted; Dispose() posts a PumpWakeUpCallback to end the loop.
                    callbacks.RunWaitCallbacks();
                }
                catch (Exception ex)
                {
                    // A failing handler must never end the pump, otherwise reconnects silently stop working again.
                    Util.WriteLine("Error while handling a Steam callback: {0}", ex.Message);
                }
            }
        }

        /// <summary>
        /// Disconnects from Steam and stops the callback pump. The session cannot be used afterwards.
        /// </summary>
        public void Dispose()
        {
            Disconnect();

            Thread pump;
            lock (pumpLock)
            {
                pumpStopping = true;
                pump = callbackPump;
                callbackPump = null;
            }

            if (pump != null)
            {
                steamClient.PostCallback(new PumpWakeUpCallback());
                if (pump != Thread.CurrentThread)
                    pump.Join(DisconnectTimeout);
            }

            GC.SuppressFinalize(this);
        }

        public async Task RequestAppInfo(uint appId, bool bForce = false)
        {
            if ((AppInfo.ContainsKey(appId) && !bForce) || bAborted)
                return;

            var appTokens = await steamApps.PICSGetAccessTokens([appId], []);

            if (appTokens.AppTokensDenied.Contains(appId))
            {
                Util.WriteLine("Insufficient privileges to get access token for app {0}", appId);
            }

            foreach (var token_dict in appTokens.AppTokens)
            {
                this.AppTokens[token_dict.Key] = token_dict.Value;
            }

            var request = new SteamApps.PICSRequest(appId);

            if (AppTokens.TryGetValue(appId, out var token))
            {
                request.AccessToken = token;
            }

            var appInfoMultiple = await steamApps.PICSGetProductInfo([request], []);

            foreach (var appInfo in appInfoMultiple.Results)
            {
                foreach (var app_value in appInfo.Apps)
                {
                    var app = app_value.Value;

                    Util.WriteLine("Got AppInfo for {0}", app.ID);
                    AppInfo[app.ID] = app;
                }

                foreach (var app in appInfo.UnknownApps)
                {
                    AppInfo[app] = null;
                }
            }
        }

        public async Task RequestPackageInfo(IEnumerable<uint> packageIds)
        {
            var packages = packageIds.ToList();
            packages.RemoveAll(PackageInfo.ContainsKey);

            if (packages.Count == 0 || bAborted)
                return;

            var packageRequests = new List<SteamApps.PICSRequest>();

            foreach (var package in packages)
            {
                var request = new SteamApps.PICSRequest(package);

                // LicenseListCallback fills PackageTokens on the callback pump thread
                lock (PackageTokens)
                {
                    if (PackageTokens.TryGetValue(package, out var token))
                    {
                        request.AccessToken = token;
                    }
                }

                packageRequests.Add(request);
            }

            var packageInfoMultiple = await steamApps.PICSGetProductInfo([], packageRequests);

            foreach (var packageInfo in packageInfoMultiple.Results)
            {
                foreach (var package_value in packageInfo.Packages)
                {
                    var package = package_value.Value;
                    PackageInfo[package.ID] = package;
                }

                foreach (var package in packageInfo.UnknownPackages)
                {
                    PackageInfo[package] = null;
                }
            }
        }

        public async Task<bool> RequestFreeAppLicense(uint appId)
        {
            try
            {
                var resultInfo = await steamApps.RequestFreeLicense(appId);

                return resultInfo.GrantedApps.Contains(appId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to request FreeOnDemand license for app {appId}: {ex.Message}");
                return false;
            }
        }

        public async Task RequestDepotKey(uint depotId, uint appid = 0)
        {
            if (DepotKeys.ContainsKey(depotId) || bAborted)
                return;

            var depotKey = await steamApps.GetDepotDecryptionKey(depotId, appid);

            Util.WriteLine("Got depot key for {0} result: {1}", depotKey.DepotID, depotKey.Result);

            if (depotKey.Result != EResult.OK)
            {
                return;
            }

            DepotKeys[depotKey.DepotID] = depotKey.DepotKey;
        }


        public async Task<ulong> GetDepotManifestRequestCodeAsync(uint depotId, uint appId, ulong manifestId, string branch)
        {
            if (bAborted)
                return 0;

            var requestCode = await steamContent.GetManifestRequestCode(depotId, appId, manifestId, branch);

            if (requestCode == 0)
            {
                Util.WriteLine($"No manifest request code was returned for depot {depotId} from app {appId}, manifest {manifestId}");

                if (!authenticatedUser)
                {
                    Console.WriteLine("Suggestion: Try logging in with -username as old manifests may not be available for anonymous accounts.");
                }
            }
            else
            {
                Console.WriteLine($"Got manifest request code for depot {depotId} from app {appId}, manifest {manifestId}, result: {requestCode}");
            }

            return requestCode;
        }

        public async Task RequestCDNAuthToken(uint appid, uint depotid, Server server)
        {
            var cdnKey = (depotid, server.Host);
            var completion = new TaskCompletionSource<SteamContent.CDNAuthToken>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (bAborted || !CDNAuthTokens.TryAdd(cdnKey, completion))
            {
                return;
            }

            SteamContent.CDNAuthToken token = null;
            try
            {
                DebugLog.WriteLine(nameof(Steam3Session), $"Requesting CDN auth token for {server.Host}");

                var cdnAuth = await steamContent.GetCDNAuthToken(appid, depotid, server.Host);

                Util.WriteLine($"Got CDN auth token for {server.Host} result: {cdnAuth.Result} (expires {cdnAuth.Expiration})");

                if (cdnAuth.Result == EResult.OK)
                {
                    token = cdnAuth;
                }
            }
            catch (Exception ex)
            {
                // e.g. the connection dropped (the job is cancelled) or the request timed out
                Util.WriteLine($"Failed to get CDN auth token for {server.Host}: {ex.Message}");
            }
            finally
            {
                // Download loops await this task, so it must always complete: a failed request used to leave it
                // pending and hung the whole download forever. Failed entries are removed so a later 403 asks again.
                if (token == null)
                {
                    CDNAuthTokens.TryRemove(KeyValuePair.Create(cdnKey, completion));
                }

                completion.TrySetResult(token);
            }
        }

        /// <summary>
        /// Returns the CDN auth token for this depot and host, or null when there is none, its request failed or did
        /// not finish within <paramref name="timeout"/>, or it (nearly) expired. Unusable entries are removed so that
        /// the next 403 requests a new token instead of reusing an expired one forever.
        /// </summary>
        public async Task<string> GetCDNAuthTokenAsync(uint depotid, string host, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var cdnKey = (depotid, host);
            if (!CDNAuthTokens.TryGetValue(cdnKey, out var completion))
            {
                return null;
            }

            SteamContent.CDNAuthToken token;
            try
            {
                token = await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                token = null;
            }

            if (token != null && token.Expiration > DateTime.UtcNow + CDNAuthTokenExpiryMargin)
            {
                return token.Token;
            }

            CDNAuthTokens.TryRemove(KeyValuePair.Create(cdnKey, completion));
            return null;
        }

        public async Task CheckAppBetaPassword(uint appid, string password)
        {
            var appPassword = await steamApps.CheckAppBetaPassword(appid, password);

            Util.WriteLine("Retrieved {0} beta keys with result: {1}", appPassword.BetaPasswords.Count, appPassword.Result);

            foreach (var entry in appPassword.BetaPasswords)
            {
                AppBetaPasswords[entry.Key] = entry.Value;
            }
        }

        public async Task<KeyValue> GetPrivateBetaDepotSection(uint appid, string branch)
        {
            if (!AppBetaPasswords.TryGetValue(branch, out var branchPassword)) // Should be filled by CheckAppBetaPassword
            {
                return new KeyValue();
            }

            AppTokens.TryGetValue(appid, out var accessToken); // Should be filled by RequestAppInfo

            var privateBeta = await steamApps.PICSGetPrivateBeta(appid, accessToken, branch, branchPassword);

            Console.WriteLine($"Retrieved private beta depot section for {appid} with result: {privateBeta.Result}");

            return privateBeta.DepotSection;
        }

        public async Task<PublishedFileDetails> GetPublishedFileDetails(uint appId, PublishedFileID pubFile)
        {
            var pubFileRequest = new CPublishedFile_GetDetails_Request
            {
                appid = appId,
                includetags = true,
                short_description = true
            };
            pubFileRequest.publishedfileids.Add(pubFile);

            var details = await steamPublishedFile.GetDetails(pubFileRequest);

            if (details.Result == EResult.OK)
            {
                return details.Body.publishedfiledetails.FirstOrDefault();
            }

            throw new Exception($"EResult {(int)details.Result} ({details.Result}) while retrieving file details for pubfile {pubFile}.");
        }

        /// <summary>The latest change notes of a workshop file (what its author wrote with each update), newest first.</summary>
        public async Task<List<CPublishedFile_GetChangeHistory_Response.ChangeLog>> GetChangeHistory(ulong publishedFileId, uint count)
        {
            var request = new CPublishedFile_GetChangeHistory_Request
            {
                publishedfileid = publishedFileId,
                total_only = false,
                startindex = 0,
                count = count,
                language = 0
            };
            var response = await steamPublishedFile.GetChangeHistory(request);
            if (response.Result == EResult.OK)
                return response.Body.changes;
            throw new Exception($"EResult {(int)response.Result} ({response.Result}) while retrieving the change history of pubfile {publishedFileId}.");
        }

        public async Task<SteamCloud.UGCDetailsCallback> GetUGCDetails(UGCHandle ugcHandle)
        {
            var callback = await steamCloud.RequestUGCDetails(ugcHandle);

            if (callback.Result == EResult.OK)
            {
                return callback;
            }
            else if (callback.Result == EResult.FileNotFound)
            {
                return null;
            }

            throw new Exception($"EResult {(int)callback.Result} ({callback.Result}) while retrieving UGC details for {ugcHandle}.");
        }

        private void ResetConnectionFlags()
        {
            bExpectingDisconnectRemote = false;
            bIsConnectionRecovery = false;
        }

        public async Task<bool> Connect()
        {
            EnsureCallbackPump();

            lock (stateLock)
            {
                // An aborted attempt (10 failed tries, logon refused, Disconnect(), timeout) must not block new ones:
                // IsConnecting used to stay true after an abort, so Steam stayed dead until the process restarted.
                // While a (re)connect is still running we only wait for it below.
                if (!IsLoggedOn && (!IsConnecting || bAborted))
                {
                    bAborted = false;
                    bConnecting = true;
                    IsConnecting = true;
                    connectionBackoff = 0;
                    Interlocked.Increment(ref authEpoch);
                    authSession = null;

                    ResetConnectionFlags();
                    this.steamClient.Connect();
                }
            }

            if (await WaitForLogOnAsync(LogOnTimeout).ConfigureAwait(false))
                return true;

            if (!bAborted)
            {
                // The attempt is stuck (no answer from the CM, endless retries...). Abort it so the next Connect()
                // starts from scratch instead of waiting on it forever.
                Util.WriteLine("Timed out connecting to Steam3.");
                Abort(false);
            }

            return IsLoggedOn;
        }

        private void Abort(bool sendLogOff = true)
        {
            Disconnect(sendLogOff);
        }

        public void Disconnect(bool sendLogOff = true)
        {
            bool wasActive;
            int handledDisconnects;

            lock (stateLock)
            {
                wasActive = steamClient.IsConnected || IsConnecting || IsLoggedOn;
                handledDisconnects = Volatile.Read(ref disconnectCount);

                bAborted = true;
                bConnecting = false;
                // Lets the next Connect() start a new attempt.
                IsConnecting = false;
                IsLoggedOn = false;
                bIsConnectionRecovery = false;
                Interlocked.Increment(ref authEpoch);
                authSession = null;

                if (sendLogOff && steamClient.IsConnected)
                {
                    steamUser.LogOff();
                }

                steamClient.Disconnect();
            }

            //TODO: Hide progress

            // Give the pump a moment to handle our DisconnectedCallback, so Disconnected has been raised and no
            // callback of this connection is handled after a later Connect(). Without a connection none will come,
            // and on the pump thread itself (Abort() from a handler) waiting would only block it: skip both cases.
            // Never wait without a deadline, this runs on the UI thread at shutdown.
            if (!wasActive || Thread.CurrentThread == callbackPump)
                return;

            var deadline = DateTime.UtcNow + DisconnectTimeout;
            while (Volatile.Read(ref disconnectCount) == handledDisconnects && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }

        private void Reconnect()
        {
            lock (stateLock)
            {
                if (bAborted)
                    return;

                bIsConnectionRecovery = true;
                // Callers wait for this recovery instead of starting a second connection.
                IsConnecting = true;
                Interlocked.Increment(ref authEpoch);
                authSession = null;
                steamClient.Disconnect();
            }
        }

        // CallbackManager handler, hence async void: an exception escaping it is rethrown on the thread pool and ends
        // the process (the pump's catch never sees it). E.g. LogOnAnonymous rethrows a failed machine id lookup.
        private async void ConnectedCallback(SteamClient.ConnectedCallback connected)
        {
            var epoch = Volatile.Read(ref authEpoch);
            try
            {
                await LogOnAfterConnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try
                {
                    Util.WriteLine("Failed to log on to Steam: " + ex.Message);
                    // Ends this attempt so Connect() returns at once; a newer attempt is left alone.
                    if (epoch == Volatile.Read(ref authEpoch))
                        Abort(false);
                }
                catch
                {
                    // Must never throw either; the next Connect() starts over.
                }
            }
        }

        private async Task LogOnAfterConnectAsync()
        {
            // Connected right before Disconnect()/Abort(): don't log on a connection that is being torn down.
            if (bAborted)
                return;

            var epoch = Volatile.Read(ref authEpoch);

            Util.WriteLine(" Done!");
            bConnecting = false;

            // Update our tracking so that we don't time out, even if we need to reconnect multiple times,
            // e.g. if the authentication phase takes a while and therefore multiple connections.
            connectionBackoff = 0;

            if (!authenticatedUser)
            {
                Util.Write("Logging anonymously into Steam3...");
                steamUser.LogOnAnonymous();
            }
            else
            {
                if (logonDetails.Username != null)
                {
                    Util.WriteLine("Logging '{0}' into Steam3...", logonDetails.Username);
                }

                if (authSession is null)
                {
                    if (logonDetails.Username != null && logonDetails.Password != null && logonDetails.AccessToken is null)
                    {
                        try
                        {
                            _ = AccountSettingsStore.Instance.GuardData.TryGetValue(logonDetails.Username, out var guarddata);
                            authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                            {
                                DeviceFriendlyName = nameof(DepotDownloader),
                                Username = logonDetails.Username,
                                Password = logonDetails.Password,
                                IsPersistentSession = ContentDownloader.Config.RememberPassword,
                                GuardData = guarddata,
                                Authenticator = new ConsoleAuthenticator(),
                            });
                        }
                        catch (TaskCanceledException)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            Util.WriteLine("Failed to authenticate with Steam: " + ex.Message);
                            Abort(false);
                            return;
                        }

                        if (epoch != Volatile.Read(ref authEpoch) || bAborted)
                            return;
                    }
                }

                if (authSession != null)
                {
                    try
                    {
                        var result = await authSession.PollingWaitForResultAsync();

                        if (epoch != Volatile.Read(ref authEpoch) || bAborted)
                            return;

                        logonDetails.Username = result.AccountName;
                        logonDetails.Password = null;
                        logonDetails.AccessToken = result.RefreshToken;

                        if (result.NewGuardData != null)
                        {
                            AccountSettingsStore.Instance.GuardData[result.AccountName] = result.NewGuardData;
                        }
                        else
                        {
                            AccountSettingsStore.Instance.GuardData.Remove(result.AccountName);
                        }
                        AccountSettingsStore.Instance.LoginTokens[result.AccountName] = result.RefreshToken;
                        AccountSettingsStore.Save();
                    }
                    catch (TaskCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Util.WriteLine("Failed to authenticate with Steam: " + ex.Message);
                        Abort(false);
                        return;
                    }

                    authSession = null;
                }

                if (epoch != Volatile.Read(ref authEpoch) || bAborted)
                    return;

                steamUser.LogOn(logonDetails);
            }
        }

        private void DisconnectedCallback(SteamClient.DisconnectedCallback disconnected)
        {
            try
            {
                HandleDisconnected(disconnected);
            }
            finally
            {
                Interlocked.Increment(ref disconnectCount);
            }
        }

        private void HandleDisconnected(SteamClient.DisconnectedCallback disconnected)
        {
            // Make a lost connection visible at once: Steam.IsConnected and WaitSteamConnectionAsync rely on
            // IsLoggedOn, and every request on a dead connection fails anyway.
            var wasLoggedOn = IsLoggedOn;
            IsLoggedOn = false;

            DebugLog.WriteLine(nameof(Steam3Session), $"Disconnected: bIsConnectionRecovery = {bIsConnectionRecovery}, UserInitiated = {disconnected.UserInitiated}, bExpectingDisconnectRemote = {bExpectingDisconnectRemote}");

            // When recovering the connection, we want to reconnect even if the remote disconnects us
            if (!bIsConnectionRecovery && (disconnected.UserInitiated || bExpectingDisconnectRemote))
            {
                Util.WriteLine("Disconnected from Steam");

                // Outstanding operations were aborted by Disconnect()/Abort(), which set bAborted themselves.
                // Don't set it here: SteamClient.Connect() also reports a user initiated disconnect of the previous
                // connection, and aborting then would kill the connection attempt that is just starting.
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (bAborted)
                return;

            if (connectionBackoff >= 10)
            {
                Util.WriteLine("Could not connect to Steam after 10 tries");
                Disconnected?.Invoke(this, EventArgs.Empty);
                Abort(false);
                return;
            }

            int backoff;
            lock (stateLock)
            {
                if (bAborted)
                    return;

                backoff = ++connectionBackoff;
                // Connect() callers wait for this recovery instead of starting a second connection.
                IsConnecting = true;
            }

            if (bConnecting)
            {
                Util.WriteLine("Connection to Steam failed. Trying again");
            }
            else
            {
                Util.WriteLine("Lost connection to Steam. Reconnecting");
            }

            if (wasLoggedOn)
                Disconnected?.Invoke(this, EventArgs.Empty);

            Thread.Sleep(1000 * backoff);

            lock (stateLock)
            {
                // Disconnect() may have been called while we were waiting.
                if (bAborted)
                    return;

                // Any connection related flags need to be reset here to match the state after Connect
                ResetConnectionFlags();
                // Invalidate in-flight ConnectedCallback auth (same as Connect/Reconnect).
                Interlocked.Increment(ref authEpoch);
                authSession = null;
                steamClient.Connect();
            }
        }

        private void LoggedOffCallback(SteamUser.LoggedOffCallback loggedOff)
        {
            var wasLoggedOn = IsLoggedOn;
            IsLoggedOn = false;

            // Our own LogOff from Disconnect() ends up here as well, nothing to recover then.
            if (bAborted)
                return;

            Util.WriteLine("Logged off of Steam3: {0}", loggedOff.Result);

            if (wasLoggedOn)
                Disconnected?.Invoke(this, EventArgs.Empty);

            if (loggedOff.Result == EResult.LoggedInElsewhere)
            {
                // Reconnecting would just kick the other session of this account in turn.
                Abort(false);
                return;
            }

            // Steam ended the session (e.g. maintenance) but may keep the socket open, so no DisconnectedCallback
            // would follow and every request would fail. Recover like after a lost connection.
            Reconnect();
        }

        private void LogOnCallback(SteamUser.LoggedOnCallback loggedOn)
        {
            // Stale logon result of a connection that Disconnect()/Abort() already tore down.
            if (bAborted)
                return;

            if (loggedOn.Result == EResult.NoConnection)
            {
                // The logon was sent after the connection dropped. The DisconnectedCallback of that connection drives
                // the reconnect; aborting here would cancel that recovery.
                Util.WriteLine("Unable to login to Steam3: {0}", loggedOn.Result);
                return;
            }

            if (loggedOn.Result == EResult.TryAnotherCM)
            {
                Util.Write("Retrying Steam3 connection (TryAnotherCM)...");

                Reconnect();

                return;
            }

            if (loggedOn.Result == EResult.ServiceUnavailable)
            {
                Util.WriteLine("Unable to login to Steam3: {0}", loggedOn.Result);
                Abort(false);

                return;
            }

            if (loggedOn.Result != EResult.OK)
            {
                Util.WriteLine("Unable to login to Steam3: {0}", loggedOn.Result);
                Abort();

                return;
            }

            lock (stateLock)
            {
                if (bAborted)
                    return;

                IsLoggedOn = true;
                IsConnecting = false;
            }

            Util.WriteLine(" Done!");
            Connected?.Invoke(this, EventArgs.Empty);

            if (ContentDownloader.Config.CellID == 0)
            {
                Util.WriteLine("Using Steam3 suggested CellID: " + loggedOn.CellID);
                ContentDownloader.Config.CellID = (int)loggedOn.CellID;
            }
            else
                Util.WriteLine("Using Steam3 CellID: " + loggedOn.CellID);
        }

        private void LicenseListCallback(SteamApps.LicenseListCallback licenseList)
        {
            if (licenseList.Result != EResult.OK)
            {
                Util.WriteLine("Unable to get license list: {0} ", licenseList.Result);

                // Anonymous sessions never use the license list (see ContentDownloader.AccountHasAccess). Now that
                // callbacks are pumped after logon, aborting would only drop a working session.
                if (authenticatedUser)
                    Abort();

                return;
            }

            Util.WriteLine("Got {0} licenses for account!", licenseList.LicenseList.Count);
            Licenses = licenseList.LicenseList;

            // Runs on the callback pump thread while RequestPackageInfo may read PackageTokens.
            lock (PackageTokens)
            {
                foreach (var license in licenseList.LicenseList)
                {
                    if (license.AccessToken > 0)
                    {
                        PackageTokens.TryAdd(license.PackageID, license.AccessToken);
                    }
                }
            }
        }

        #region Trebuchet Additions

        public async Task<IEnumerable<PublishedFileDetails>> GetPublishedFileDetailsComplete(IEnumerable<uint> apps,
            IEnumerable<ulong> pubFiles)
        {
            var list = new List<SteamKit2.Internal.PublishedFileDetails>();
            foreach (var appId in apps)
            {
                await RequestAppInfo(appId);
                if (!await ContentDownloader.AccountHasAccess(appId, appId))
                {
                    if (await RequestFreeAppLicense(appId))
                    {
                        Util.Write("Obtained FreeOnDemand license for app {0}", appId);

                        // Fetch app info again in case we didn't get it fully without a license.
                        await RequestAppInfo(appId, true);
                    }
                    else
                    {
                        var contentName = ContentDownloader.GetAppName(appId);
                        throw new ContentDownloaderException($"App {appId} ({contentName}) is not available from this account.");
                    }
                }

                list.AddRange(await GetPublishedFileDetails(appId, pubFiles));
            }

            return list.Where(x => x.hcontent_file > 0).GroupBy(x => x.publishedfileid).Select(y => y.First());
        }
        public async Task<List<PublishedFileDetails>> GetPublishedFileDetails(uint appId, IEnumerable<ulong> pubFiles)
        {
            var pubFileRequest = new CPublishedFile_GetDetails_Request
            {
                appid = appId,
                includetags = true,
                short_description = true
            };
            pubFileRequest.publishedfileids.AddRange(pubFiles);
            if (pubFileRequest.publishedfileids.Count == 0)
                return new List<PublishedFileDetails>();

            var details = await steamPublishedFile.GetDetails(pubFileRequest);
            if (details.Result == EResult.OK)
            {
                return details.Body.publishedfiledetails;
            }
            throw new Exception($"EResult {(int)details.Result} ({details.Result}) while retrieving file details for pubfile ({string.Join(",", pubFiles)}).");
        }

        public Task<CPublishedFile_QueryFiles_Response> QueryPublishedFileSearch(uint appId, string searchTerms, uint perPage, uint page)
            => QueryPublishedFileSearch(appId, searchTerms, perPage, page, null, null);

        /// <summary>
        /// Workshop search with optional Steam tags. Always requests tag payloads (return_tags)
        /// so callers can distinguish Enhanced vs Legacy mods.
        /// </summary>
        public Task<CPublishedFile_QueryFiles_Response> QueryPublishedFileSearch(uint appId, string searchTerms, uint perPage, uint page, string requiredTags)
            => QueryPublishedFileSearch(appId, searchTerms, perPage, page, requiredTags, null);

        public async Task<CPublishedFile_QueryFiles_Response> QueryPublishedFileSearch(
            uint appId, string searchTerms, uint perPage, uint page, string requiredTags, string excludedTags)
        {
            var queryRequest = new CPublishedFile_QueryFiles_Request
            {
                appid = appId,
                page = page,
                filetype = 18,
                numperpage = perPage,
                search_text = searchTerms ?? string.Empty,
                strip_description_bbcode = true,
                return_details = true,
                return_vote_data = true,
                return_short_description = true,
                return_tags = true,
                match_all_tags = true,
                query_type = (uint)SteamKit2.EPublishedFileQueryType.RankedByTextSearch
            };

            if (!string.IsNullOrWhiteSpace(requiredTags))
            {
                foreach (var tag in SplitWorkshopTags(requiredTags))
                    queryRequest.requiredtags.Add(tag);
            }

            if (!string.IsNullOrWhiteSpace(excludedTags))
            {
                foreach (var tag in SplitWorkshopTags(excludedTags))
                    queryRequest.excludedtags.Add(tag);
            }

            var details = await steamPublishedFile.QueryFiles(queryRequest);
            if (details.Result == EResult.OK)
            {
                return details.Body;
            }
            throw new Exception($"EResult {(int)details.Result} ({details.Result}) while retrieving query for pubfile ({appId}: {searchTerms}).");
        }

        private static IEnumerable<string> SplitWorkshopTags(string tags)
        {
            return tags.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        #endregion
    }
}
