using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ImageStudio.Core;

/// <summary>App data locations: %LOCALAPPDATA%\VertexAIStudio.</summary>
public sealed class AppPaths
{
    public string Root { get; }
    public string Database => System.IO.Path.Combine(Root, "studio.db");
    public string Settings => System.IO.Path.Combine(Root, "settings.json");
    public string Images => System.IO.Path.Combine(Root, "images");

    public AppPaths(string? root = null)
    {
        Root = root ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VertexAIStudio");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Images);
    }
}

/// <summary>SQLite persistence for conversations, messages and image references.</summary>
public sealed class Store : IDisposable
{
    readonly SqliteConnection _db;
    readonly AppPaths _paths;
    public const int TrashDays = 30;

    public Store(AppPaths paths)
    {
        _paths = paths;
        _db = new SqliteConnection($"Data Source={paths.Database}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
        Exec("""
            CREATE TABLE IF NOT EXISTS conversations(
              id TEXT PRIMARY KEY, title TEXT, model TEXT,
              created_at INTEGER, updated_at INTEGER, deleted_at INTEGER NULL);
            CREATE TABLE IF NOT EXISTS messages(
              id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
              seq INTEGER, role TEXT, text TEXT, enhanced TEXT, model TEXT, status TEXT, error TEXT,
              created_at INTEGER, duration_ms INTEGER, thoughts TEXT, continue_from TEXT, settings_label TEXT);
            CREATE INDEX IF NOT EXISTS ix_messages_conv ON messages(conversation_id, seq);
            CREATE TABLE IF NOT EXISTS images(
              id TEXT PRIMARY KEY, message_id TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
              idx INTEGER, sha1 TEXT, mime TEXT, path TEXT, export_path TEXT,
              width INTEGER, height INTEGER, kind TEXT);
            CREATE INDEX IF NOT EXISTS ix_images_msg ON images(message_id);
            CREATE TABLE IF NOT EXISTS response_cache(key TEXT PRIMARY KEY, message_id TEXT, created_at INTEGER);
            """);
        try { Exec("ALTER TABLE messages ADD COLUMN notes TEXT"); } catch (SqliteException) { /* already there */ }
    }

    public void Dispose() => _db.Dispose();

    void Exec(string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(sql, args);
        cmd.ExecuteNonQuery();
    }

    SqliteCommand Cmd(string sql, params (string, object?)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }

    // ---------------- conversations ----------------

    public void SaveConversation(Conversation c) => Exec("""
        INSERT INTO conversations(id,title,model,created_at,updated_at,deleted_at) VALUES($id,$t,$m,$c,$u,$d)
        ON CONFLICT(id) DO UPDATE SET title=$t, model=$m, updated_at=$u, deleted_at=$d
        """, ("$id", c.Id), ("$t", c.Title), ("$m", c.Model), ("$c", c.CreatedAt), ("$u", c.UpdatedAt), ("$d", c.DeletedAt));

    public List<Conversation> ListConversations(string? search = null, bool trash = false)
    {
        var sql = """
            SELECT c.id,c.title,c.model,c.created_at,c.updated_at,c.deleted_at,
                   (SELECT COUNT(*) FROM messages m WHERE m.conversation_id=c.id AND m.role='user')
            FROM conversations c WHERE
            """ + " " + (trash ? "c.deleted_at IS NOT NULL" : "c.deleted_at IS NULL");
        if (!string.IsNullOrWhiteSpace(search))
            sql += " AND (c.title LIKE $q OR EXISTS(SELECT 1 FROM messages m WHERE m.conversation_id=c.id AND m.text LIKE $q))";
        sql += " ORDER BY c.updated_at DESC";
        using var cmd = Cmd(sql, ("$q", $"%{search}%"));
        using var r = cmd.ExecuteReader();
        var list = new List<Conversation>();
        while (r.Read())
            list.Add(new Conversation
            {
                Id = r.GetString(0), Title = r.GetString(1), Model = r.GetString(2),
                CreatedAt = r.GetInt64(3), UpdatedAt = r.GetInt64(4),
                DeletedAt = r.IsDBNull(5) ? null : r.GetInt64(5), MessageCount = r.GetInt32(6),
            });
        return list;
    }

    public int TrashCount()
    {
        using var cmd = Cmd("SELECT COUNT(*) FROM conversations WHERE deleted_at IS NOT NULL");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void TrashConversation(string id) =>
        Exec("UPDATE conversations SET deleted_at=$d WHERE id=$id", ("$d", Conversation.Now()), ("$id", id));

    public void RestoreConversation(string id) =>
        Exec("UPDATE conversations SET deleted_at=NULL WHERE id=$id", ("$id", id));

    /// <summary>Permanently removes conversations trashed more than 30 days ago (and their image files).</summary>
    public void PurgeTrash(bool all = false)
    {
        var cutoff = Conversation.Now() - (all ? 0 : TrashDays * 24L * 3600 * 1000);
        foreach (var c in ListConversations(trash: true).Where(c => c.DeletedAt <= cutoff))
        {
            Exec("DELETE FROM conversations WHERE id=$id", ("$id", c.Id));
            try { Directory.Delete(System.IO.Path.Combine(_paths.Images, c.Id), true); } catch { }
        }
    }

    // ---------------- messages ----------------

    public void SaveMessage(Message m)
    {
        using var tx = _db.BeginTransaction();
        Exec("""
            INSERT INTO messages(id,conversation_id,seq,role,text,enhanced,model,status,error,created_at,duration_ms,thoughts,continue_from,settings_label,notes)
            VALUES($id,$c,$s,$r,$t,$e,$m,$st,$er,$ca,$du,$th,$cf,$sl,$no)
            ON CONFLICT(id) DO UPDATE SET text=$t, enhanced=$e, model=$m, status=$st, error=$er,
              duration_ms=$du, thoughts=$th, continue_from=$cf, settings_label=$sl, notes=$no
            """, ("$id", m.Id), ("$c", m.ConversationId), ("$s", m.Seq), ("$r", m.Role), ("$t", m.Text),
            ("$e", m.Enhanced), ("$m", m.Model), ("$st", m.Status), ("$er", m.Error), ("$ca", m.CreatedAt),
            ("$du", m.DurationMs), ("$th", m.Thoughts), ("$cf", m.ContinueFromImageId), ("$sl", m.SettingsLabel), ("$no", m.Notes));
        Exec("DELETE FROM images WHERE message_id=$id", ("$id", m.Id));
        foreach (var i in m.Images)
        {
            i.MessageId = m.Id;
            Exec("""
                INSERT INTO images(id,message_id,idx,sha1,mime,path,export_path,width,height,kind)
                VALUES($id,$m,$i,$s,$mi,$p,$ep,$w,$h,$k)
                """, ("$id", i.Id), ("$m", m.Id), ("$i", i.Idx), ("$s", i.Sha1), ("$mi", i.Mime), ("$p", i.Path),
                ("$ep", i.ExportPath), ("$w", i.Width), ("$h", i.Height), ("$k", i.Kind));
        }
        Exec("UPDATE conversations SET updated_at=$u WHERE id=$c", ("$u", Conversation.Now()), ("$c", m.ConversationId));
        tx.Commit();
    }

    public void DeleteMessage(string id) => Exec("DELETE FROM messages WHERE id=$id", ("$id", id));

    public List<Message> LoadMessages(string conversationId)
    {
        var list = new List<Message>();
        var byId = new Dictionary<string, Message>();
        using (var cmd = Cmd("""
            SELECT id,seq,role,text,enhanced,model,status,error,created_at,duration_ms,thoughts,continue_from,settings_label,notes
            FROM messages WHERE conversation_id=$c ORDER BY seq
            """, ("$c", conversationId)))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var m = new Message
                {
                    Id = r.GetString(0), ConversationId = conversationId, Seq = r.GetInt32(1), Role = r.GetString(2),
                    Text = r.IsDBNull(3) ? "" : r.GetString(3), Enhanced = Str(r, 4), Model = Str(r, 5),
                    Status = r.GetString(6), Error = Str(r, 7), CreatedAt = r.GetInt64(8),
                    DurationMs = r.IsDBNull(9) ? null : r.GetInt64(9), Thoughts = Str(r, 10),
                    ContinueFromImageId = Str(r, 11), SettingsLabel = Str(r, 12), Notes = Str(r, 13),
                };
                list.Add(m);
                byId[m.Id] = m;
            }
        using (var cmd = Cmd("""
            SELECT i.id,i.message_id,i.idx,i.sha1,i.mime,i.path,i.export_path,i.width,i.height,i.kind
            FROM images i JOIN messages m ON m.id=i.message_id WHERE m.conversation_id=$c ORDER BY i.idx
            """, ("$c", conversationId)))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                if (byId.TryGetValue(r.GetString(1), out var m))
                    m.Images.Add(new ImageRef
                    {
                        Id = r.GetString(0), MessageId = m.Id, Idx = r.GetInt32(2), Sha1 = r.GetString(3),
                        Mime = r.GetString(4), Path = r.GetString(5), ExportPath = Str(r, 6),
                        Width = r.GetInt32(7), Height = r.GetInt32(8), Kind = r.GetString(9),
                    });
        return list;
    }

    static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    // ---------------- response cache ----------------

    public string? CacheLookup(string key)
    {
        using var cmd = Cmd("SELECT message_id FROM response_cache WHERE key=$k", ("$k", key));
        return cmd.ExecuteScalar() as string;
    }

    public void CachePut(string key, string messageId) => Exec(
        "INSERT OR REPLACE INTO response_cache(key,message_id,created_at) VALUES($k,$m,$c)",
        ("$k", key), ("$m", messageId), ("$c", Conversation.Now()));

    // ---------------- image files ----------------

    /// <summary>Writes the original bytes unchanged to images\&lt;conv&gt;\&lt;sha1&gt;.&lt;ext&gt;.</summary>
    public ImageRef SaveImageBytes(string conversationId, byte[] bytes, string mime, string kind, string? base64 = null)
    {
        base64 ??= Convert.ToBase64String(bytes);
        var sha1 = Sha1Hex(base64);
        var dir = System.IO.Path.Combine(_paths.Images, conversationId);
        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, sha1 + MimeExt(mime));
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        var (w, h) = ImageInfo.Size(bytes);
        return new ImageRef { Sha1 = sha1, Mime = mime, Path = path, Width = w, Height = h, Kind = kind };
    }

    public static string Sha1Hex(string text) =>
        Convert.ToHexString(SHA1.HashData(Encoding.ASCII.GetBytes(text))).ToLowerInvariant();

    public static string MimeExt(string mime) => mime switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".png",
    };
}
