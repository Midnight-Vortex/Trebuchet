// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using ProtoBuf;

namespace DepotDownloader
{
    [ProtoContract]
    public class DepotConfigStore
    {
        [ProtoMember(1)]
        public Dictionary<uint, ulong> InstalledManifestIDs { get; private set; }

        [ProtoMember(2)]
        public Dictionary<ulong, ulong> InstalledUGCManifestIDs { get; } = new Dictionary<ulong, ulong>();

        string FileName;

        DepotConfigStore()
        {
            InstalledManifestIDs = [];
        }

        static bool Loaded
        {
            get { return Instance != null; }
        }

        public static DepotConfigStore Instance;

        public static void LoadFromFile(string filename)
        {
            if (Instance != null)
                return;
            Instance = LoadInstanceFromFile(filename);
        }

        public static DepotConfigStore LoadInstanceFromFile(string filename)
        {
            DepotConfigStore store = null;
            if (File.Exists(filename))
            {
                try
                {
                    // Read only, and let Save() replace the file meanwhile (FileShare.Delete), so that the periodic
                    // mod status checks never collide with a download saving its progress.
                    using var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    using var ds = new DeflateStream(fs, CompressionMode.Decompress);
                    store = Serializer.Deserialize<DepotConfigStore>(ds);
                }
                catch (Exception ex) when (Util.IsCorruptDataException(ex))
                {
                    // A file truncated by a crash or power loss (written in place by older versions) used to make every
                    // later load throw, until it was deleted by hand. Start over: missing entries are re-verified.
                    Util.WriteLine("Depot config {0} is corrupt, starting with an empty one: {1}", filename, ex.Message);
                }
            }

            store ??= new DepotConfigStore();
            store.FileName = filename;
            return store;
        }

        static readonly object SaveLock = new();

        public static void Save()
        {
            if (!Loaded)
                throw new Exception("Saved config before loading");

            lock (SaveLock)
            {
                // Write a temporary file and swap it in: a crash or power loss while saving can no longer leave a
                // truncated depot.config, the previous one stays in place until the new one is complete.
                var tempFileName = Instance.FileName + ".tmp";
                using (var fs = File.Open(tempFileName, FileMode.Create))
                {
                    using (var ds = new DeflateStream(fs, CompressionMode.Compress, leaveOpen: true))
                    {
                        Serializer.Serialize(ds, Instance);
                    }

                    fs.Flush(flushToDisk: true);
                }

                File.Move(tempFileName, Instance.FileName, overwrite: true);
            }
        }
    }
}
