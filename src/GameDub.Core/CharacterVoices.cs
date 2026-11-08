namespace GameDub.Core;

public sealed record HindiVoice(string Id,string Label,string Category,string Timbre);
public sealed record VoiceAssignment(string Speaker,string VoiceId,string Evidence,double? Confidence);
public sealed record CharacterVoiceProfile(string VoiceId,string Category="Unspecified",string Timbre="",string DefaultDelivery="neutral");
public static class CharacterVoices
{
    // Same IDs drive both engines: Kokoro voices, or the IndicF5 profiles made from Parler speakers.
    public static readonly HindiVoice[] Catalog=[new("hm_omega","Deep male (Omega / Rohit)","Male","Deep, calm adult male"),new("hm_psi","Young male (Psi / Aman)","Male","Young, energetic male"),
        new("hf_alpha","Expressive female (Alpha / Divya)","Female","Natural, expressive female"),new("hf_beta","Calm female (Beta / Rani)","Female","Mature, calm female")];
    public static bool IsKnownVoice(string id)=>Catalog.Any(v=>v.Id==id);
    public static VoiceAssignment Assign(Dialogue line,GameProfile profile)
    {
        lock(profile)
        {
        var explicitName=!string.IsNullOrWhiteSpace(line.Speaker)&&!line.Speaker.Equals("Unknown",StringComparison.OrdinalIgnoreCase);
        var manual=!explicitName&&!string.IsNullOrWhiteSpace(profile.ManualSpeaker)&&!profile.ManualSpeaker.Equals("Unknown",StringComparison.OrdinalIgnoreCase);
        var speaker=explicitName?line.Speaker.Trim():manual?profile.ManualSpeaker.Trim():"Unknown";
        var voice=profile.VoiceFor(speaker);if(!IsKnownVoice(voice))voice=IsKnownVoice(profile.DefaultVoice)?profile.DefaultVoice:"hm_omega";
        return new(speaker,voice,explicitName?"Explicit subtitle label":manual?"Manual character selection":"User default; identity unknown",explicitName||manual?1:null);
        }
    }
    public static bool MigrateKnownGame(GameProfile profile)
    {
        if(profile.VoiceSchemaVersion>=2)return false;
        if(profile.GameId.Equals("GTA5_Enhanced",StringComparison.OrdinalIgnoreCase))
        {
            if(profile.Voices.Count==0&&profile.Characters.Count==0&&profile.DefaultVoice=="hf_alpha")profile.DefaultVoice="hm_omega";
            profile.Voices.TryAdd("Michael","hm_omega");profile.Voices.TryAdd("Franklin","hm_psi");
        }
        profile.VoiceSchemaVersion=2;return true;
    }
}
