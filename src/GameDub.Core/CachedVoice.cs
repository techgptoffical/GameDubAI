using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameDub.Core;

// Speech depends on Hindi, the selected voice and engine controls, not unrelated
// earlier English subtitles. Reuse complete chunks across dialogue contexts.
public sealed class CachedVoice(IVoiceProvider inner,DialogueCache? storage = null) : IChunkedVoiceProvider
{
    private readonly DialogueCache cache=storage??new DialogueCache();
    public string Identity=>inner.Identity+":audio-cache-v1";
    public IReadOnlyList<LocalizedLine> DeliveryChunks(LocalizedLine line)=>inner is IChunkedVoiceProvider chunked?chunked.DeliveryChunks(line):[];
    public async Task<AudioClip> SynthesizeAsync(LocalizedLine line,string voice,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key="audio-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { line.Hindi,line.Emotion,voice,engine=inner.Identity }))));
        var hit=cache.Get(key);
        if(hit is not null)return hit.Value.Clip with { Identity=inner.Identity+":cached-audio" };
        var clip=await inner.SynthesizeAsync(line,voice,token); token.ThrowIfCancellationRequested();
        cache.Put(key,line,clip); return clip;
    }
}
