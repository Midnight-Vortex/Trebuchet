using System.Diagnostics.CodeAnalysis;

namespace TrebuchetLib.Services;

public static class AppFilesEx
{
    public static TRef Resolve<T, TRef>(this IAppFileHandler<T, TRef> handler, TRef reference) 
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        if (handler.Exists(reference)) return reference;
        return handler.GetDefault();
    }
    
    public static bool TryParseModListRef(this AppFiles files, string name, [NotNullWhen(true)] out IPRefWithModList? reference)
    {
        try
        {
            var uri = new Uri(name);
            if (uri.Scheme == Constants.UriScheme)
                return files.TryParseModListRef(uri, out reference);
            reference = null;
            return false;
        }
        catch
        {
            reference = null;
            return false;
        }
    }

    public static IPRefWithModList ResolveModList(this AppFiles files, string name)
    {
        if (files.TryParseModListRef(name, out var reference))
            return reference;
        return files.Mods.Resolve(name);
    }

    public static bool TryParseModListRef(this AppFiles files, Uri uri, [NotNullWhen(true)] out IPRefWithModList? reference)
    {
        reference = null;
        if (uri.Segments.Length < 2)
            return false;
        switch (uri.Host)
        {
            case Constants.UriSyncHost:
                reference = files.Sync.Ref(Uri.UnescapeDataString(uri.Segments[1]));
                return true;
            case Constants.UriModListHost:
                reference = files.Mods.Ref(Uri.UnescapeDataString(uri.Segments[1]));
                return true;
            default:
                return false;
        }
    }
    
    public static bool TryParseConnectionRef(this AppFiles files, string name, [NotNullWhen(true)]out IPRefWithClientConnection? reference)
    {
        try
        {
            var uri = new Uri(name);
            if (uri.Scheme == Constants.UriScheme)
                return files.TryParseConnectionRef(uri, out reference);
            reference = null;
            return false;
        }
        catch
        {
            reference = null;
            return false;
        }
    }

    public static IPRefWithClientConnection ResolveClientConnectionSource(this AppFiles files, string name)
    {
        if (files.TryParseConnectionRef(name, out var reference))
            return reference;
        return files.Client.Resolve(name);
    }

    public static ClientConnectionRef? ResolveClientConnectionRef(this AppFiles files, string name)
    {
        try
        {
            var uri = new Uri(name);
            if (uri.Segments.Length < 3) return null;
            if (files.TryParseConnectionRef(uri, out var reference))
                return new ClientConnectionRef(reference, Uri.UnescapeDataString(uri.Segments[2]));
            return null;
        }
        catch
        {
            return null;
        }
    }
    
    public static bool TryParseConnectionRef(this AppFiles files, Uri uri, [NotNullWhen(true)]out IPRefWithClientConnection? reference)
    {
        reference = null;
        if (uri.Segments.Length < 2) return false;
        switch (uri.Host)
        {
            case Constants.UriSyncHost:
                reference = files.Sync.Ref(Uri.UnescapeDataString(uri.Segments[1]));
                return true;
            case Constants.UriClientHost:
                reference = files.Client.Ref(Uri.UnescapeDataString(uri.Segments[1]));
                return true;
            default:
                return false;
        }
    }

    public static bool TryParse<T, TRef>(this AppFiles files, string name, [NotNullWhen(true)] out TRef? reference)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        try
        {
            var uri = new Uri(name);
            return files.TryParse<T, TRef>(uri, out reference);
        }
        catch
        {
            reference = null;
            return false;
        }
    }
    
    public static bool TryParse<T, TRef>(this AppFiles files, Uri uri, [NotNullWhen(true)] out TRef? reference)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        reference = null;
        if (uri.Segments.Length < 2) return false;
        reference = files.GetFileHandler<T, TRef>(uri)?.Ref(Uri.UnescapeDataString(uri.Segments[1]));
        return reference != null;
    }

    public static IAppFileHandler<T, TRef>? GetFileHandler<T, TRef>(this AppFiles files, Uri uri)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        try
        {
            if (uri.Host != GetFileHost<T, TRef>()) return null;
            switch (uri.Host)
            {
                case Constants.UriSyncHost:
                    return (IAppFileHandler<T, TRef>)files.Sync;
                case Constants.UriClientHost:
                    return (IAppFileHandler<T, TRef>)files.Client;
                case Constants.UriServerHost:
                    return (IAppFileHandler<T, TRef>)files.Server;
                case Constants.UriModListHost:
                    return (IAppFileHandler<T, TRef>)files.Mods;
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    public static string? GetFileHost<T, TRef>()
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        if (typeof(TRef) == typeof(ClientProfileRef))
            return Constants.UriClientHost;
        if (typeof(TRef) == typeof(ServerProfileRef))
            return Constants.UriServerHost;
        if (typeof(TRef) == typeof(ModListProfileRef))
            return Constants.UriModListHost;
        if (typeof(TRef) == typeof(SyncProfileRef))
            return Constants.UriSyncHost;
        return null;
    }

    public static TRef Resolve<T, TRef>(this IAppFileHandler<T, TRef> handler, string data)
        where T : ProfileFile<T>
        where TRef : class, IPRef<T, TRef>
    {
        try
        {
            var uri = new Uri(data);
            if (uri.Segments.Length >= 2 && uri.Host == GetFileHost<T, TRef>())
                return handler.Resolve(handler.Ref(Uri.UnescapeDataString(uri.Segments[1])));
            return handler.Resolve(handler.Ref(data));
        }
        catch
        {
            return handler.Resolve(handler.Ref(data));
        }
    }
    
    public static bool TryResolve<T, TRef>(this IAppFileHandler<T, TRef> handler, string data,[NotNullWhen(true)] out TRef? reference)
        where T : ProfileFile<T>
        where TRef : class, IPRef<T, TRef>
    {
        try
        {
            var uri = new Uri(data);
            if (uri.Segments.Length >= 2 && uri.Host == GetFileHost<T, TRef>())
            {
                reference = handler.Ref(Uri.UnescapeDataString(uri.Segments[1]));
                if (handler.Exists(reference))
                    return true;
                return false;
            }
            reference = null;
            return false;
        }
        catch
        {
            reference = null;
            return false;
        }
    }

    public static string GetDirectory<T, TRef>(this IAppFileHandler<T, TRef> handler, TRef reference) 
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        return Path.GetDirectoryName(handler.GetPath(reference)) ?? throw new DirectoryNotFoundException();
    }

    public static bool TryGet<T, TRef>(this IAppFileHandler<T, TRef> handler, TRef reference, [NotNullWhen(true)] out T? file)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        file = null; 
        if (!handler.Exists(reference)) return false;
        try
        {
            file = handler.Get(reference);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static T Get<T, TRef>(this IPRef<T, TRef> reference)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        return reference.Handler.Get((TRef)reference);
    }
    
    public static T Create<T, TRef>(this IAppFileHandler<T, TRef> handler, TRef reference)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        if (handler.Cache.TryGetValue(reference, out var profile))
        {
            profile.SaveFile();
            return profile;
        }
        var file = ProfileFile<T>.CreateProfile(handler.GetPath(reference));
        file.SaveFile();
        handler.Cache[reference] = file;
        return file;
    }
    
    public static T Get<T, TRef>(this IAppFileHandler<T, TRef> handler, TRef name)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        if (handler.Cache.TryGetValue(name, out var profile))
            return profile;
        
        if(handler.UseSubFolders)
            ProfileFile<T>.RepairMissingProfileFile(handler.GetPath(name));
        var file = ProfileFile<T>.LoadProfile(handler.GetPath(name));
        handler.Cache[name] = file;
        return file;
    }
    
    public static bool Exists<T, TRef>(this IAppFileHandler<T, TRef> handler, TRef name)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        if(handler.UseSubFolders)
            ProfileFile<T>.RepairMissingProfileFile(handler.GetPath(name));
        return File.Exists(handler.GetPath(name));
    }
    
    public static bool Exists<T, TRef>(this IAppFileHandler<T, TRef> handler, string name)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        if(handler.UseSubFolders)
            ProfileFile<T>.RepairMissingProfileFile(handler.GetPath(name));
        return File.Exists(handler.GetPath(name));
    }
    
    public static string GetPath<T, TRef>(this IAppFileHandler<T, TRef> handler, TRef reference)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        return handler.GetPath(reference.Name);
    }
    
    internal static string GetPath<T, TRef>(this IAppFileHandler<T, TRef> handler, string name)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        if(handler.UseSubFolders)
            return Path.Combine(
                handler.GetBaseFolder(), 
                name, 
                Constants.FileProfileConfig);
        return Path.Combine(handler.GetBaseFolder(), name + ".json");
    }
    
    public static void Delete<T, TRef>(this IAppFileHandler<T, TRef> handler, TRef name)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        var profile = Get(name);
        handler.Cache.Remove(name);
        if(handler.UseSubFolders)
            profile.DeleteFolder();
        else profile.DeleteFile();
    }

    public static async Task<T> Duplicate<T, TRef>(this IAppFileHandler<T, TRef> handler, TRef name, TRef destination)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        if (handler.Exists(destination)) throw new Exception("Destination profile exists");
        if (!handler.Exists(name)) throw new Exception("Source profile does not exists");
        var profile = Get(name);
        if (handler.UseSubFolders)
            await profile.CopyFolderTo(handler.GetPath(destination));
        else profile.CopyFileTo(handler.GetPath(destination));
        var copy = Get(destination);
        return copy;
    }

    public static Task<T> Rename<T, TRef>(this IAppFileHandler<T, TRef> handler, TRef name, TRef destination)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        if (handler.Exists(destination)) throw new Exception("Destination profile exists");
        if (!handler.Exists(name)) throw new Exception("Source profile does not exists");
        var profile = Get(name);
        if(handler.UseSubFolders)
            profile.MoveFolderTo(handler.GetPath(destination));
        else profile.MoveFileTo(handler.GetPath(destination));
        handler.Cache.Remove(name);
        return Task.FromResult(Get(destination));
    }
    
    public static IEnumerable<TRef> GetList<T, TRef>(this IAppFileHandler<T, TRef> handler)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        if (!Directory.Exists(handler.GetBaseFolder()))
            yield break;

        string[] profiles = handler.UseSubFolders 
            ? Directory.GetDirectories(handler.GetBaseFolder(), "*")
            : Directory.GetFiles(handler.GetBaseFolder(), "*.json");
        foreach (string p in profiles)
            yield return handler.Ref(Path.GetFileNameWithoutExtension(p));
    }

    public static Task<long> GetSize<T, TRef>(this IAppFileHandler<T, TRef> handler, TRef name)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        if (!handler.UseSubFolders) return Task.FromResult(0L);
        var dir = Path.GetDirectoryName(handler.GetPath(name));
        if (dir is null) return Task.FromResult(0L);
        return Task.Run(() => Tools.DirectorySize(dir));
    }
    
    public static TRef GetDefault<T, TRef>(this IAppFileHandler<T, TRef> handler)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        var profile = handler.UseSubFolders 
            ? handler.Ref(Tools.GetFirstDirectoryName(handler.GetBaseFolder(), "*"))
            : handler.Ref(Tools.GetFirstFileName(handler.GetBaseFolder(), "*.json"));
        if (!string.IsNullOrEmpty(profile.Name)) 
            return profile;

        profile = handler.Ref("Default");
        if (!File.Exists(handler.GetPath(profile)))
            ProfileFile<T>.CreateProfile(handler.GetPath(profile)).SaveFile();
        return profile;
    }
    
    public static string GetGameLogs<T, TRef>(this IAppFileHandler<T, TRef> handler, TRef reference)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        if (!handler.UseSubFolders)
            return string.Empty;
        return Path.Combine(
            handler.GetBaseFolder(),
            reference.Name,
            Constants.FolderGameSaveLog,
            Constants.FileGameLogFile
        );
    }
    
    public static Task Export<T, TRef>(this IAppFileHandler<T, TRef> handler, TRef name, FileInfo file)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        if (handler.UseSubFolders) throw new NotImplementedException();
        var path = handler.GetPath(name);
        File.Copy(path, file.FullName, true);
        return Task.CompletedTask;
    }

    public static async Task<T> Import<T, TRef>(this IAppFileHandler<T, TRef> handler, FileInfo import, TRef name)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        if (handler.UseSubFolders) throw new NotImplementedException();
        var json = await File.ReadAllTextAsync(import.FullName);
        return await handler.Import(json, name);
    }

    public static Task<T> Import<T, TRef>(this IAppFileHandler<T, TRef> handler, string json, TRef name)
        where T : ProfileFile<T>
        where TRef : class,IPRef<T, TRef>
    {
        if (handler.UseSubFolders) throw new NotImplementedException();
        var path = handler.GetPath(name);
        var profile = ProfileFile<T>.ImportFile(json, path);
        handler.Cache[name] = profile;
        return Task.FromResult(profile);
    }

    public static IEnumerable<ClientConnectionRef> GetConnectionRefs(this IPRefWithClientConnection reference)
    {
        return reference.GetConnections().Select(x => new ClientConnectionRef(reference, x.Name));
    }

    public static IEnumerable<ulong> GetModsFromList(this IPRefWithModList reference)
    {
        if (reference.TryGetModList(out var list))
            foreach (var m in list)
                if (ModListUtil.TryParseModId(m, out ulong id))
                    yield return id;
    }
    
    public static IEnumerable<ulong> GetModsFromList(this IEnumerable<IPRefWithModList> references)
    {
        return references.SelectMany(x => x.GetModsFromList());
    }
    
    private static bool ResolveMod(this AppSetup setup, uint appId, ref string mod)
    {
        string file = mod;
        if (long.TryParse(mod, out _))
            file = Path.Combine(setup.GetWorkshopFolder(), appId.ToString(), mod, "none");

        string? folder = Path.GetDirectoryName(file);
        if (folder == null)
            return false;

        if (!long.TryParse(Path.GetFileName(folder), out _))
            return File.Exists(file);

        if (!Directory.Exists(folder))
            return false;

        // Legacy: *.pak. Enhanced/IoStore: accept .pak or IoStore pair (.ucas/.utoc).
        string[] files = Directory.GetFiles(folder, "*.pak", SearchOption.TopDirectoryOnly);
        if (files.Length > 0)
        {
            mod = files[0];
            return true;
        }

        var hasIoStore = Directory.EnumerateFiles(folder, "*.ucas", SearchOption.TopDirectoryOnly).Any()
                         && Directory.EnumerateFiles(folder, "*.utoc", SearchOption.TopDirectoryOnly).Any();
        if (!hasIoStore)
            return false;

        // Prefer any remaining content file for modlist path; IoStore mounts from the workshop folder.
        var any = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (any is null)
            return false;
        mod = any;
        return true;
    }
    
    public static bool TryGetModPath(this AppSetup setup, string mod, [NotNullWhen(true)] out string? path)
    {
        try
        {
            if (setup.ResolveMod(Constants.AppIDLiveClient, ref mod))
            {
                path = mod;
                return true;
            }
            if (setup.ResolveMod(Constants.AppIDTestLiveClient, ref mod))
            {
                path = mod;
                return true;
            }

            path = null;
            return false;
        }
        catch
        {
            path = null;
            return false;
        }
    }
    
    public static IEnumerable<string> GetModsPath(this AppSetup setup, IEnumerable<string> modlist, bool throwIfFailed = true)
    {
        foreach (string mod in modlist)
        {
            if (!setup.TryGetModPath(mod, out var path))
                if (throwIfFailed)
                    throw new Exception($"Could not resolve mod {path}.");
                else yield return mod;
            else
                yield return path;
        }
    }
}