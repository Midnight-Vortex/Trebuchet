using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TrebuchetLib.Services;

public class Progress : IProgressCallback<DepotDownloader.Progress>
{
    private DepotDownloader.Progress _value;
    private SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
    
    public event EventHandler<DepotDownloader.Progress>? ProgressChanged;

    public async void Report(DepotDownloader.Progress value)
    {
        try
        {
            await _semaphore.WaitAsync();
        }
        catch
        {
            return;
        }
        try
        {
            _value = value;
            ProgressChanged?.Invoke(this, value);
        }
        catch
        {
            // progress display must never break a download
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<DepotDownloader.Progress> GetProgressAsync()
    {
        await _semaphore.WaitAsync();
        var value = _value;
        _semaphore.Release();
        return value;
    }
}