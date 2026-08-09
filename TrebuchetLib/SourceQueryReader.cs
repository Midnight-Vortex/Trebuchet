using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TrebuchetLib
{
    public class SourceQueryReader : IDisposable
    {
        // \xFF\xFF\xFF\xFFTSource Engine Query\x00 because UTF-8 doesn't like to encode 0xFF
        public static readonly byte[] REQUEST = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x54, 0x53, 0x6F, 0x75, 0x72, 0x63, 0x65, 0x20, 0x45, 0x6E, 0x67, 0x69, 0x6E, 0x65, 0x20, 0x51, 0x75, 0x65, 0x72, 0x79, 0x00 };

        private IPEndPoint _endpoint;
        private DateTime _lastUpdate = DateTime.MinValue;
        private int _refreshRate;
        private int _timeout;
        private byte[] _buffer = [];
        private CancellationTokenSource? _cts;

        public SourceQueryReader(IPEndPoint ep, int timeout, int refreshRate = 30 * 1000)
        {
            _endpoint = ep;
            _timeout = timeout;
            _refreshRate = Math.Max(timeout + 500, refreshRate);
        }

        public enum EnvironmentFlags : byte
        {
            Linux = 0x6C,   //l
            Windows = 0x77, //w
            Mac = 0x6D,     //m
            MacOsX = 0x6F   //o
        }

        [Flags]
        public enum ExtraDataFlags : byte
        {
            GameID = 0x01,
            SteamID = 0x10,
            Keywords = 0x20,
            Spectator = 0x40,
            Port = 0x80
        }

        public enum ServerTypeFlags : byte
        {
            Dedicated = 0x64,     //d
            Nondedicated = 0x6C,   //l
            SourceTV = 0x70   //p
        }

        public enum VACFlags : byte
        {
            Unsecured = 0,
            Secured = 1
        }

        public enum VisibilityFlags : byte
        {
            Public = 0,
            Private = 1
        }

        public byte Bots { get; private set; }

        public EnvironmentFlags Environment { get; private set; }

        public ExtraDataFlags ExtraDataFlag { get; private set; }

        public string Folder { get; private set; } = string.Empty;

        public string Game { get; private set; } = string.Empty;

        // Extra data
        public ulong GameID { get; private set; }

        public byte Header { get; private set; }

        public short ID { get; private set; }

        // I
        public string Keywords { get; private set; } = string.Empty;

        public string Map { get; private set; } = string.Empty;

        public int MaxPlayers { get; private set; }

        public string Name { get; private set; } = string.Empty;

        public bool Online { get; private set; }

        public int Players { get; private set; }

        public short Port { get; private set; }

        public byte Protocol { get; private set; }

        public ServerTypeFlags ServerType { get; private set; }

        //0x20
        public string Spectator { get; private set; } = string.Empty;

        //0x40
        public short SpectatorPort { get; private set; }

        //0x01
        public ulong SteamID { get; private set; }

        public VACFlags VAC { get; private set; }

        public string Version { get; private set; } = string.Empty;

        public VisibilityFlags Visibility { get; private set; }
        
        public void Refresh()
        {
            if (Online && (DateTime.Now - _lastUpdate).TotalMilliseconds < _refreshRate)
                return;
            _lastUpdate = DateTime.Now;

            try
            { 
                ProcessQuery();
            }
            catch
            {
                Online = false;
            }
        }

        public void StartQueryThread()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            Task.Run(() => ThreadLoop(_endpoint, _timeout, _refreshRate, _cts.Token), _cts.Token);
        }

        public void StopQueryThread()
        {
            if (_cts is null) return;
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        public void Dispose()
        {
            StopQueryThread();
        }

        private void ProcessQuery()
        {
            byte[] result;
            lock (this)
                result = _buffer;
            using MemoryStream ms = new MemoryStream(result);
            using BinaryReader br = new BinaryReader(ms, Encoding.UTF8);
            ReadResponse(ms, br);
            br.Close();
            ms.Close();
        }

        private void ReadResponse(MemoryStream ms, BinaryReader br)
        {
            ms.Seek(4, SeekOrigin.Begin);   // skip the 4 0xFFs
            Header = br.ReadByte();
            Protocol = br.ReadByte();
            Name = br.ReadNullTerminatedString() ?? string.Empty;
            Map = br.ReadNullTerminatedString() ?? string.Empty;
            Folder = br.ReadNullTerminatedString() ?? string.Empty;
            Game = br.ReadNullTerminatedString() ?? string.Empty;
            ID = br.ReadInt16();
            Players = br.ReadByte();
            MaxPlayers = br.ReadByte();
            Bots = br.ReadByte();
            ServerType = (ServerTypeFlags)br.ReadByte();
            Environment = (EnvironmentFlags)br.ReadByte();
            Visibility = (VisibilityFlags)br.ReadByte();
            VAC = (VACFlags)br.ReadByte();
            Version = br.ReadNullTerminatedString() ?? string.Empty;
            Online = true;
        }
        
        private void ResetResponse()
        {
            Header = 0;
            Protocol = 0;
            Name = string.Empty;
            Map = string.Empty;
            Folder = string.Empty;
            Game = string.Empty;
            ID = 0;
            Players = 0;
            MaxPlayers = 0;
            Bots = 0;
            ServerType = ServerTypeFlags.Dedicated;
            Environment = EnvironmentFlags.Windows;
            Visibility = VisibilityFlags.Public;
            VAC = VACFlags.Unsecured;
            Version = string.Empty;
            Online = false;
        }

        private void ThreadLoop(IPEndPoint endPoint, int timeout, int rate, CancellationToken token)
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = timeout;

            while (!token.IsCancellationRequested)
            {
                var sleepMs = rate;
                try
                {
                    udp.Send(REQUEST, REQUEST.Length, _endpoint);
                    var result = udp.Receive(ref _endpoint);
                    lock(this)
                        _buffer = result;
                }
                catch
                {
                    lock (this)
                        _buffer = [];
                    sleepMs = Math.Min(rate, 1000);
                }
                Thread.Sleep(sleepMs);
            }
        }

        //ExtraDataFlag = (ExtraDataFlags)br.ReadByte();

        //#region These EDF readers have to be in this order because that's the way they are reported

        //if (ExtraDataFlag.HasFlag(ExtraDataFlags.Port))
        //    Port = br.ReadInt16();
        //if (ExtraDataFlag.HasFlag(ExtraDataFlags.SteamID))
        //    SteamID = br.ReadUInt64();
        //if (ExtraDataFlag.HasFlag(ExtraDataFlags.Spectator))
        //{
        //    SpectatorPort = br.ReadInt16();
        //    Spectator = ReadNullTerminatedString(ref br);
        //}
        //if (ExtraDataFlag.HasFlag(ExtraDataFlags.Keywords))
        //    Keywords = ReadNullTerminatedString(ref br);
        //if (ExtraDataFlag.HasFlag(ExtraDataFlags.GameID))
        //    GameID = br.ReadUInt64();

        //#endregion These EDF readers have to be in this order because that's the way they are reported
    }
}