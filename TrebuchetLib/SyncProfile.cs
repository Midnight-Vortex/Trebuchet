using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json.Serialization;
using SteamKit2.CDN;

namespace TrebuchetLib
{
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(SyncProfile))]
    public partial class SyncProfileJsonContext : JsonSerializerContext
    {
    }
    
    public sealed class SyncProfile : ProfileFile<SyncProfile>
    {
        private List<string> _modlist = [];
        private List<ClientConnection> _clientConnections = [];

        public string SyncURL { get; set; } = string.Empty;
        // A profile imported from a remote JSON may say null: that means empty, it must not crash later.
        public List<string> Modlist
        {
            get => _modlist;
            set => _modlist = value ?? [];
        }
        public List<ClientConnection> ClientConnections
        {
            get => _clientConnections;
            set => _clientConnections = value ?? [];
        }
    }
}