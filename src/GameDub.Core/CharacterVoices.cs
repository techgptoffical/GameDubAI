namespace GameDub.Core;

public sealed record HindiVoice(string Id,string Label,string Category,string Timbre);
public sealed record VoiceAssignment(string Speaker,string VoiceId,string Evidence,double? Confidence);
public sealed record CharacterVoiceProfile(string VoiceId,string Category="Unspecified",string Timbre="",string DefaultDelivery="neutral");
public static class CharacterVoices
{
    public static readonly HindiVoice[] Catalog=[new("hm_omega","Omega · Hindi male","Male","Omega model voice"),new("hm_psi","Psi · Hindi male","Male","Psi model voice"),
        new("hf_alpha","Alpha · Hindi female","Female","Alpha model voice"),new("hf_beta","Beta · Hindi female","Female","Beta model voice")];
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
