using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TrebuchetLib
{
    public class Rcon : IRcon, IDisposable
    {
        public Rcon(IPEndPoint endpoint, string password, ILogger<Rcon> logger)
        {
            EndPoint = endpoint;
            _password = password;
            _logger = logger;
        }

        private readonly ILogger<Rcon> _logger;
        private readonly Queue<RconPacket> _rconQueue = [];
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly string _password;
        private TcpClient? _client;
        private CancellationTokenSource? _timeoutCts;
        private int _id;

        private readonly Dictionary<string, object> _context = new()
        {
            { "TrebSource", ConsoleLogSource.RCon }
        };
        
        public IPEndPoint EndPoint { get; }
        
        /// <summary>
        /// Timeout duration for each RCon requests, in seconds
        /// </summary>
        public int Timeout { get; init; } = 15;

        /// <summary>
        /// Keep alive duration for the active RCon duration, in seconds
        /// </summary>
        public int KeepAlive { get; init; } = 300;

        public void Dispose()
        {
            _client?.Close();
            _client?.Dispose();
            _client = null;
        }

        public Rcon SetContext(string key, object value)
        {
            _context[key] = value;
            return this;
        }

        public async Task<RConResponse> Send(string data, CancellationToken token)
        {
            if (data.Length > 4086)
                throw new ArgumentOutOfRangeException(nameof(data), "Data must be less than 4086 characters.");

            var packet = new RconPacket
            {
                Id = _id++,
                Type = RconPacketType.SERVERDATA_EXECCOMMAND,
                Body = data
            };

            await _semaphore.WaitAsync(token);
            try
            {
                await Connect(token);
                var response = await RConSend(_client, packet, token);
                if (response.Exception is not null) CloseConnection();
                else AutoDisconnect();
                return response;
            }
            catch { CloseConnection(); throw; }
            finally { _semaphore.Release(); }
        }

        public int QueueData(string data)
        {
            if (data.Length > 4086)
                throw new ArgumentOutOfRangeException(nameof(data), "Data must be less than 4086 characters.");

            var packet = new RconPacket
            {
                Id = _id++,
                Type = RconPacketType.SERVERDATA_EXECCOMMAND,
                Body = data
            };
            _rconQueue.Enqueue(packet);
            return packet.Id;
        }

        public async Task<List<RConResponse>> FlushQueue(CancellationToken token)
        {
            if (_rconQueue.Count == 0) return [];
            
            await _semaphore.WaitAsync(token);
            try
            {
                await Connect(token);
                List<RConResponse> responses = [];
                while (_rconQueue.Count > 0)
                {
                    var packet = _rconQueue.Dequeue();
                    var response = await RConSend(_client, packet, token);
                    responses.Add(response);
                    if (response.Exception is not null) { CloseConnection(); break; }
                }
                AutoDisconnect();
                return responses;
            }
            catch { CloseConnection(); throw; }
            finally { _semaphore.Release(); }
        }

        public async Task<List<RConResponse>> Send(IEnumerable<string> data, CancellationToken ct)
        {
            List<int> ids = [];
            foreach (var d in data)
                ids.Add(QueueData(d));
            var responses = await FlushQueue(ct);
            return ids.Select(x => responses.FirstOrDefault(r => r.Id == x, RConResponse.Empty)).ToList();
        }

        
        [MemberNotNull("_client")]
        public async Task Connect(CancellationToken token)
        {
            if (_client is not null && _client.Connected) return;
            CloseConnection();
            _client = new();
            _client.ReceiveTimeout = Timeout * 1000;
            _client.SendTimeout = Timeout * 1000;
            await _client.ConnectAsync(EndPoint, token);
            await RConLogin(_password, _client.GetStream(), token);
        }

        public async Task Disconnect()
        {
            await _semaphore.WaitAsync();
            try { CloseConnection(); }
            finally { _semaphore.Release(); }
        }

        private void CloseConnection()
        {
            _client?.Dispose();
            _client = null;
        }

        private void AutoDisconnect()
        {
            Task.Run(AutoDisconnectTask);
        }
 
        private async Task AutoDisconnectTask()
        {
            try
            {
                await _semaphore.WaitAsync();
                if (_timeoutCts is not null)
                    await _timeoutCts.CancelAsync();
                _timeoutCts = new();
                var ct = _timeoutCts.Token;
                _semaphore.Release();

                await Task.Delay(KeepAlive * 1000, ct);
                await Disconnect();
            }
            catch (OperationCanceledException)
            {
            }
        }

        private RconPacket BuildAuthPacket(string password)
        {
            var packet = new RconPacket
            {
                Id = ++_id,
                Type = RconPacketType.SERVERDATA_AUTH,
                Body = password
            };

            return packet;
        }

        private async Task<RConResponse> EvaluateRconResponse(BinaryReader br)
        {
            return await Task.Run(() =>
            {
                try
                {
                    //todo: have the response have a max size and have it match the announced size
                    int size = br.ReadInt32();
                    if (size < 10 ||
                        size > 4096) // rcon message (called packet) can never be larger than 4096 bytes and never smaller than 10, this is by design in the protocol definition
                        throw new Exception($"Invalid packet received (size=10<{size}<4096)");

                    var id = br.ReadInt32();
                    _ = (RconPacketType)br.ReadInt32();
                    var response = br.ReadNullTerminatedString();
                    var code = br.ReadByte();
                    if (code != 0)
                        throw new Exception($"Invalid packet received ({code})");

                    if (id == -1)
                        throw new Exception("Authentication failed.");

                    return new RConResponse(id, response ?? string.Empty);
                }
                catch(Exception ex)
                {
                    return new RConResponse(ex);
                }
            });
        }

        private async Task RConLogin(string password, NetworkStream stream, CancellationToken ct)
        {
            var auth = BuildAuthPacket(password);
            await using var bw = new BinaryWriter(stream, Encoding.ASCII, true);
            var result = await auth.WriteBinary(bw);
            LogResponse(result);
            ct.ThrowIfCancellationRequested();
            var response = await RConReceive(stream, ct);
            if (response.Exception is not null) throw response.Exception;
        }

        private async Task<RConResponse> RConReceive(NetworkStream stream, CancellationToken ct)
        {
            using var br = new BinaryReader(stream, Encoding.ASCII, true);
            ct.ThrowIfCancellationRequested();
            var response = await EvaluateRconResponse(br);
            LogResponse(response);
            return response;
        }

        private async Task<RConResponse> RConSend(TcpClient client, RconPacket packet, CancellationToken ct)
        {
            await using var bw = new BinaryWriter(client.GetStream(), Encoding.ASCII, true);
            var result = await packet.WriteBinary(bw);
            LogResponse(result);
            ct.ThrowIfCancellationRequested();
            return await RConReceive(client.GetStream(), ct);
        }

        private void LogResponse(RConResponse response)
        {
            if (response.IsEmpty) return;
            using var scope = _logger.BeginScope(_context);

            if (response.Exception is not null)
                _logger.LogError(response.Exception, response.Response);
            else
                _logger.LogInformation(response.Response);   
        }

        // Message contains int32 length, int32 request id, int32 type, string body null terminated, string null terminator
        // It is important to note that the length is not including itself, so the minimum length is 10 bytes
        // The maximum length is 4096 bytes, this is by design in the protocol definition
        // https://developer.valvesoftware.com/wiki/Source_RCON_Protocol
        private class RconPacket
        {
            public string Body { get; init; } = string.Empty;
            public int Id { get; init; }
            public RconPacketType Type { get; init; }

            private int Length => Body.Length + 10;

            public async Task<RConResponse> WriteBinary(BinaryWriter bw)
            {
                return await Task.Run(() =>
                {
                    try
                    {
                        bw.Write(Length);
                        bw.Write(Id);
                        bw.Write((int)Type);
                        bw.WriteNullTerminatedString(Body);
                        bw.Write((byte)0);
                        return RConResponse.Empty;
                    }
                    catch(Exception ex)
                    {
                        return new RConResponse(Id, ex);
                    }
                });
            }
        }
    }
}
