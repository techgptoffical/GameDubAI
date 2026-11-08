using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GameDub.Core;

public sealed record DialogueEvent
{
    public long Sequence { get; set; }
    public string Id { get; set; } = "";
    public string Session { get; set; } = "";
    public int Scene { get; set; }
    public string English { get; set; } = "";
    public DateTimeOffset AppearedAt { get; set; }
    public DateTimeOffset? DisappearedAt { get; set; }
    public string Speaker { get; set; } = "Unknown";
    public string SpeakerEvidence { get; set; } = "User default; identity unknown";
    public double? SpeakerConfidence { get; set; }
    public string VoiceId { get; set; } = "hm_omega";
    public string Hindi { get; set; } = "";
    public string TranslationState { get; set; } = "Pending";
    public string SynthesisState { get; set; } = "Pending";
    public string PlaybackState { get; set; } = "Pending";
    public string? AudioCacheKey { get; set; }
    public double? ExpectedSpeechMs { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
    public DateTimeOffset? PlaybackStartedAt { get; set; }
    public DateTimeOffset? PlaybackCompletedAt { get; set; }
    public double TranslationMs { get; set; }
    public double SynthesisMs { get; set; }
    public double? FirstChunkMs { get; set; }
    public string? CancellationReason { get; set; }
    public string? Error { get; set; }
    public string TranslationSource { get; set; } = "";
    public Dialogue ToDialogue()=>new(English,Speaker) { EventId=Id,FirstSeenAt=AppearedAt };
}

// SQLite is a local event ledger, not an unbounded in-memory speech queue.
// Prepared PCM lives only in the scheduler's small channels and the existing cache.
public sealed class DialogueEventStore : IDisposable
{
    private readonly object gate=new();
    private readonly SqliteConnection connection;
    public string DatabasePath { get; }
    public string Session { get; }=Guid.NewGuid().ToString("N");
    private long revision;
    public long Revision=>Interlocked.Read(ref revision);
    public DialogueEventStore(string path)
    {
        DatabasePath=Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        connection=new(new SqliteConnectionStringBuilder { DataSource=DatabasePath,Pooling=false }.ToString()); connection.Open();
        using var command=connection.CreateCommand();command.CommandText="""
            PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS dialogue_events(seq INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT UNIQUE NOT NULL,session TEXT NOT NULL,scene INTEGER NOT NULL,translation TEXT NOT NULL,synthesis TEXT NOT NULL,playback TEXT NOT NULL,payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS dialogue_pending ON dialogue_events(session,scene,translation,synthesis,playback,seq);
            """;command.ExecuteNonQuery();
    }
    public DialogueEvent Add(Dialogue line,int scene,VoiceAssignment voice)
    {
        var item=new DialogueEvent { Id=line.EventId,Session=Session,Scene=scene,English=line.English,AppearedAt=line.FirstSeenAt,
            Speaker=voice.Speaker,VoiceId=voice.VoiceId,SpeakerEvidence=voice.Evidence,SpeakerConfidence=voice.Confidence };
        lock(gate)
        {
            using var command=connection.CreateCommand();command.CommandText="INSERT INTO dialogue_events(id,session,scene,translation,synthesis,playback,payload) VALUES($id,$session,$scene,'Pending','Pending','Pending',$json); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$id",item.Id);command.Parameters.AddWithValue("$session",Session);command.Parameters.AddWithValue("$scene",scene);command.Parameters.AddWithValue("$json",JsonSerializer.Serialize(item));
            item.Sequence=(long)command.ExecuteScalar()!;SaveLocked(item);return item;
        }
    }
    private DialogueEvent? GetLocked(string id)
    {
        using var c=connection.CreateCommand();c.CommandText="SELECT payload FROM dialogue_events WHERE id=$id";c.Parameters.AddWithValue("$id",id);
        return c.ExecuteScalar() is string json?JsonSerializer.Deserialize<DialogueEvent>(json,Json.Options):null;
    }
    public DialogueEvent? Get(string id) { lock(gate)return GetLocked(id); }
    private void SaveLocked(DialogueEvent item)
    {
        using var c=connection.CreateCommand();c.CommandText="UPDATE dialogue_events SET translation=$t,synthesis=$s,playback=$p,payload=$json WHERE id=$id";
        c.Parameters.AddWithValue("$id",item.Id);c.Parameters.AddWithValue("$t",item.TranslationState);c.Parameters.AddWithValue("$s",item.SynthesisState);c.Parameters.AddWithValue("$p",item.PlaybackState);c.Parameters.AddWithValue("$json",JsonSerializer.Serialize(item));c.ExecuteNonQuery();Interlocked.Increment(ref revision);
    }
    public DialogueEvent? Update(string id,Action<DialogueEvent> edit)
    {
        lock(gate) { var item=GetLocked(id);if(item is null)return null;edit(item);SaveLocked(item);return item; }
    }
    // Predicates here are fixed application queries; all event data is parameterized.
    internal DialogueEvent? First(int scene,string predicate)
    {
        lock(gate)
        {
            using var c=connection.CreateCommand();c.CommandText=$"SELECT payload FROM dialogue_events WHERE session=$session AND scene=$scene AND ({predicate}) ORDER BY seq LIMIT 1";
            c.Parameters.AddWithValue("$session",Session);c.Parameters.AddWithValue("$scene",scene);
            return c.ExecuteScalar() is string json?JsonSerializer.Deserialize<DialogueEvent>(json,Json.Options):null;
        }
    }
    public DialogueEvent[] Recent(int limit=150)
    {
        lock(gate)
        {
            using var c=connection.CreateCommand();c.CommandText="SELECT payload FROM dialogue_events ORDER BY seq DESC LIMIT $limit";c.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,500));
            using var reader=c.ExecuteReader();var rows=new List<DialogueEvent>();while(reader.Read())rows.Add(JsonSerializer.Deserialize<DialogueEvent>(reader.GetString(0),Json.Options)!);return rows.ToArray();
        }
    }
    public long Count(int scene,string predicate)
    {
        lock(gate)
        {
            using var c=connection.CreateCommand();c.CommandText=$"SELECT COUNT(*) FROM dialogue_events WHERE session=$session AND scene=$scene AND ({predicate})";
            c.Parameters.AddWithValue("$session",Session);c.Parameters.AddWithValue("$scene",scene);return (long)c.ExecuteScalar()!;
        }
    }
    public void CancelScene(int scene,string reason)
    {
        lock(gate)
        {
            // Page through fixed-size batches; long sessions never materialize all history.
            while(true)
            {
                using var c=connection.CreateCommand();c.CommandText="SELECT payload FROM dialogue_events WHERE session=$session AND scene=$scene AND playback IN ('Pending','Ready','Playing','Deferred') LIMIT 128";
                c.Parameters.AddWithValue("$session",Session);c.Parameters.AddWithValue("$scene",scene);
                var rows=new List<DialogueEvent>();using(var r=c.ExecuteReader())while(r.Read())rows.Add(JsonSerializer.Deserialize<DialogueEvent>(r.GetString(0),Json.Options)!);
                foreach(var row in rows) { row.PlaybackState="Cancelled";row.CancellationReason=reason;SaveLocked(row); }
                if(rows.Count<128)break;
            }
        }
    }
    public void Dispose() { lock(gate)connection.Dispose(); }
}
