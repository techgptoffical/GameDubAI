namespace GameDub.Core;

public sealed class FallbackVoice(IVoiceProvider primary,IVoiceProvider fallback,Action<string>? notify = null) : IChunkedVoiceProvider
{
    private volatile bool primaryFailed;
    public string Identity=>primary.Identity+":cpu-fallback-v1";
    public IReadOnlyList<LocalizedLine> DeliveryChunks(LocalizedLine line)=>primary is IChunkedVoiceProvider chunked?chunked.DeliveryChunks(line):[];
    public async Task<AudioClip> SynthesizeAsync(LocalizedLine line,string voice,CancellationToken token)
    {
        if(!primaryFailed)
        {
            try { return await primary.SynthesizeAsync(line,voice,token); }
            catch(Exception error) when(error is not OperationCanceledException && !token.IsCancellationRequested)
            { primaryFailed=true; notify?.Invoke("GPU synthesis unavailable; continuing with the local CPU voice. "+error.Message); }
        }
        return await fallback.SynthesizeAsync(line,voice,token);
    }
}
