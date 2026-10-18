using Microsoft.Extensions.Logging;
using ProtoBuf;
using tot_lib;

namespace TrebuchetLib.Sequences;

public class SequenceActionSendRESTQuery : ISequenceAction
{
    public bool CancelOnFailure { get; set; }

    public string Url { get; set; } = string.Empty;
    public int HttpMethod { get; set; } = 0;
    public string Body { get; set; } = string.Empty;
    public string Headers { get; set; } = "Content-Type:application/json";
    public TimeSpan TimeOut { get; set; } = TimeSpan.FromSeconds(10);
    
    public async Task Execute(SequenceArgs args)
    {
        if (string.IsNullOrEmpty(Url))
        {
            if (CancelOnFailure) throw new OperationCanceledException("URL is invalid");
            args.Logger.LogError("URL is invalid");
            return;
        }

        try
        {
            var request = new HttpRequestMessage();
            request.Content = new StringContent(args.Expand(Body));
            request.Method = GetMethod(HttpMethod);
            request.Headers.Add($"user-agent", $"TotTrebuchet/{ProcessUtil.GetAppVersion()}");
            foreach (var keyValuePair in ParseHeaders(Headers))
            {
                if (request.Headers.Contains(keyValuePair.Key))
                    continue;
                request.Headers.Add(keyValuePair.Key, args.Expand(keyValuePair.Value));
            }

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeOut;
            await httpClient.SendAsync(request);
        }
        catch (Exception ex)
        {
            if (CancelOnFailure)
                throw new OperationCanceledException("Failed to send request", ex);
            args.Logger.LogError(ex, "Failed to send request");
        }
    }

    private IEnumerable<KeyValuePair<string, string>> ParseHeaders(string headers)
    {
        var addedHeaders = Headers.Split(Environment.NewLine);
        foreach (var header in addedHeaders)
        {
            var kvp = header.Split(':');
            if(kvp.Length < 2) continue;
            var key = kvp[0].Trim();
            if(string.IsNullOrEmpty(key))
                continue;
            var value = kvp[1].Trim();
            if(string.IsNullOrEmpty(value))
                continue;
            yield return new KeyValuePair<string, string>(key, value);
        }
    }

    private HttpMethod GetMethod(int index)
    {
        switch (index)
        {
            default:
                return System.Net.Http.HttpMethod.Get;
            case 1:
                return System.Net.Http.HttpMethod.Post;
            case 2:
                return System.Net.Http.HttpMethod.Put;
            case 3:
                return System.Net.Http.HttpMethod.Patch;
            case 4:
                return System.Net.Http.HttpMethod.Trace;
            case 5:
                return System.Net.Http.HttpMethod.Delete;
        }
    }
}