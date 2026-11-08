using System.Text.RegularExpressions;

namespace GameDub.Core;

// Conservative vocabulary/register edits, not a second translator. Never remove
// clauses, negation, numbers, or replace a user's protected names/glossary terms.
public static class ConversationalHindi
{
    public const string Version="conversational-v1";
    private static readonly Dictionary<string,string> Phrases=new()
    {
        ["कृपया"]="प्लीज़",["धन्यवाद"]="शुक्रिया",["क्षमा करें"]="माफ़ करें",["क्षमा करो"]="माफ़ करो",
        ["प्रतीक्षा"]="इंतज़ार",["सहायता"]="मदद",["सहायताओं"]="मददों",
        ["आवश्यकताओं"]="ज़रूरतों",["आवश्यकताएँ"]="ज़रूरतें",["आवश्यकताएं"]="ज़रूरतें",["आवश्यकता"]="ज़रूरत",["आवश्यक"]="ज़रूरी",
        ["शीघ्र"]="जल्दी",["अवश्य"]="ज़रूर",["अत्यंत"]="बहुत",["अधिकांश"]="ज़्यादातर",
        ["वास्तव में"]="सच में",["प्रयास किया"]="कोशिश की",["प्रयास करो"]="कोशिश करो",["प्रयास करें"]="कोशिश करें",["प्रयास करना"]="कोशिश करना",
        ["चिकित्सक"]="डॉक्टर",["कार्यालय"]="ऑफ़िस",["दूरभाष"]="फ़ोन",["अनुमति"]="इजाज़त",["मानसिक स्वास्थ्य"]="मेंटल हेल्थ",["रेडियो कार्यक्रम"]="रेडियो शो",
        ["प्रस्थान करें"]="निकलें",["प्रस्थान करो"]="निकलो",["प्रस्थान करना"]="निकलना",
        ["प्रस्थान करने"]="निकलने",["प्रस्थान कर गया"]="निकल गया",["प्रस्थान कर गई"]="निकल गई",
        ["पुनः"]="फिर",["कदापि नहीं"]="बिल्कुल नहीं",["निश्चित रूप से"]="ज़रूर",["श्रीमान"]="सर",["महोदय"]="सर",["महोदया"]="मैडम"
    };
    private static readonly Regex Formal=new(string.Join('|',Phrases.Keys.OrderByDescending(s=>s.Length)
        .Select(s=>@"(?<![\p{L}\p{M}])"+Regex.Escape(s)+@"(?![\p{L}\p{M}])")),RegexOptions.Compiled);
    public static string Adapt(string hindi,GameProfile profile)
    {
        if(!profile.ConversationalHindi)return hindi;
        var protectedTerms=profile.Glossary.Values.Concat(profile.Characters.Keys).Concat(profile.Voices.Keys)
            .Where(s=>!string.IsNullOrWhiteSpace(s)).ToArray();
        return Formal.Replace(hindi,m=>protectedTerms.Any(p=>p.Contains(m.Value,StringComparison.Ordinal))?m.Value:Phrases[m.Value]);
    }
}

public static class SpeechDelivery
{
    public static IReadOnlyList<LocalizedLine> Chunks(LocalizedLine line,int maximum=140)
    {
        var output=new List<LocalizedLine>();
        foreach(var sentence in Regex.Split(line.Hindi.Trim(),@"(?<=[।.!?])\s+"))
        {
            var remaining=sentence.Trim();
            while(remaining.Length>maximum)
            {
                var end=remaining.LastIndexOf(' ',Math.Min(maximum,remaining.Length-1));
                if(end<1)break; // A single long word is never cut in half.
                var clause=remaining.LastIndexOfAny([',',';',':','—'],end-1,end);
                if(clause>=maximum/2)end=clause+1;
                output.Add(line with { Hindi=remaining[..end].Trim() });remaining=remaining[end..].TrimStart();
            }
            if(remaining.Length>0)output.Add(line with { Hindi=remaining });
        }
        return output;
    }
}

public static class SubtitleUiFilter
{
    public static bool IsControlText(string value)
    {
        var text=Text.Key(value);
        if(new[]{"paused","loading","loading...","saving...","game paused","press any key"}.Contains(text))return true;
        if(text.Contains("rockstar games")&&(text.Contains("hide menu")||text.Contains("browse")&&text.Contains("caps")))return true;
        var controls=new[]{"zoom","page down","page up","point of interest","blip","select","back"}.Count(text.Contains);
        return controls>=3&&(text.Contains("esc")||text.Contains("tab")||text.Contains("caps"));
    }
}
