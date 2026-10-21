// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.IsolatedStorage;
using ProtoBuf;

namespace DepotDownloader
{
    [ProtoContract]
    public class AccountSettingsStore
    {
        // Member 1 was a Dictionary<string, byte[]> for SentryData.

        [ProtoMember(2, IsRequired = false)]
        public ConcurrentDictionary<string, int> ContentServerPenalty { get; private set; }

        // Member 3 was a Dictionary<string, string> for LoginKeys.

        [ProtoMember(4, IsRequired = false)]
        public Dictionary<string, string> LoginTokens { get; private set; }

        [ProtoMember(5, IsRequired = false)]
        public Dictionary<string, string> GuardData { get; private set; }

        string FileName;

        AccountSettingsStore()
        {
            ContentServerPenalty = new ConcurrentDictionary<string, int>();
            LoginTokens = new(StringComparer.OrdinalIgnoreCase);
            GuardData = new(StringComparer.OrdinalIgnoreCase);
        }

        static bool Loaded
        {
            get { return Instance != null; }
        }

        public static AccountSettingsStore Instance;
        static readonly IsolatedStorageFile IsolatedStorage = IsolatedStorageFile.GetUserStoreForAssembly();
        static readonly object SaveLock = new();
        const string TempSuffix = ".tmp";

        public static void LoadFromFile(string filename)
        {
            if (Loaded)
                return;

            // Save() deletes the old file only once the new temporary one is complete; if it was interrupted right
            // after that, the temporary file holds the latest settings.
            var source = filename;
            if (!IsolatedStorage.FileExists(source) && IsolatedStorage.FileExists(filename + TempSuffix))
                source = filename + TempSuffix;

            if (IsolatedStorage.FileExists(source))
            {
                try
                {
                    using var fs = IsolatedStorage.OpenFile(source, FileMode.Open, FileAccess.Read);
                    using var ds = new DeflateStream(fs, CompressionMode.Decompress);
                    Instance = Serializer.Deserialize<AccountSettingsStore>(ds);
                }
                catch (Exception ex) when (ex is IOException or IsolatedStorageException || Util.IsCorruptDataException(ex))
                {
                    // Corrupt data used to throw out of here on every start (this runs in Trebuchet's Steam service
                    // constructor); the settings are only a cache, start over instead.
                    Util.WriteLine("Failed to load account settings: {0}", ex.Message);
                    Instance = new AccountSettingsStore();
                }
            }
            else
            {
                Instance = new AccountSettingsStore();
            }

            Instance.FileName = filename;
        }

        public static void Save()
        {
            if (!Loaded)
                throw new Exception("Saved config before loading");

            lock (SaveLock)
            {
                try
                {
                    // Write a temporary file first so a crash or power loss while saving can no longer leave a truncated
                    // file. IsolatedStorageFile cannot replace a file in one step: the old one is deleted only once the
                    // new one is complete (LoadFromFile falls back to the temporary file if needed).
                    var tempFileName = Instance.FileName + TempSuffix;
                    using (var fs = IsolatedStorage.OpenFile(tempFileName, FileMode.Create, FileAccess.Write))
                    {
                        using (var ds = new DeflateStream(fs, CompressionMode.Compress, leaveOpen: true))
                        {
                            Serializer.Serialize(ds, Instance);
                        }

                        fs.Flush(flushToDisk: true);
                    }

                    if (IsolatedStorage.FileExists(Instance.FileName))
                        IsolatedStorage.DeleteFile(Instance.FileName);
                    IsolatedStorage.MoveFile(tempFileName, Instance.FileName);
                }
                catch (Exception ex) when (ex is IOException or IsolatedStorageException)
                {
                    Util.WriteLine("Failed to save account settings: {0}", ex.Message);
                }
            }
        }
    }
}
