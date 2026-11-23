using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DesktopAssistant.Core;

public sealed record Message(string Role, string Text, DateTimeOffset At);
public sealed record Conversation(string Id, string Title, DateTimeOffset UpdatedAt);
public sealed record Appointment(string Id, DateOnly Day, TimeOnly Start, TimeOnly? End,
    string Title, string Note, bool Done = false)
{
    public DateTimeOffset StartsAt()
    {
        DateTime local = Day.ToDateTime(Start, DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(local))
            throw new ArgumentException("此时间在本机时区中不存在，请选择其他时间。");
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 200 || Note.Length > 4000)
            throw new ArgumentException("请输入日程标题（最多 200 字），备注最多 4000 字。");
        if (End is TimeOnly end && end <= Start)
            throw new ArgumentException("结束时间须晚于开始时间。");
        _ = StartsAt();
    }
}

public sealed class LocalStore
{
    private readonly string _connectionString;

    public LocalStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "assistant.db"), ForeignKeys = true, DefaultTimeout = 5, Pooling = false
        }.ToString();
        using SqliteConnection db = Open();
        using SqliteCommand init = db.CreateCommand();
        init.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS conversation (
                id TEXT PRIMARY KEY, title TEXT NOT NULL, updated INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS message (
                sequence INTEGER PRIMARY KEY, conversation_id TEXT NOT NULL REFERENCES conversation(id) ON DELETE CASCADE,
                role TEXT NOT NULL, text TEXT NOT NULL, at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS message_conversation ON message(conversation_id, sequence);
            CREATE TABLE IF NOT EXISTS appointment (
                id TEXT PRIMARY KEY, day TEXT NOT NULL, start TEXT NOT NULL, end TEXT, title TEXT NOT NULL,
                note TEXT NOT NULL, done INTEGER NOT NULL, reminded INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS appointment_day ON appointment(day, start);
            """;
        init.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    public void AddMessage(string conversationId, Message message)
    {
        using SqliteConnection db = Open();
        using SqliteTransaction transaction = db.BeginTransaction();
        using SqliteCommand command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversation (id, title, updated) VALUES ($id, $title, $at)
            ON CONFLICT(id) DO UPDATE SET updated = excluded.updated;
            INSERT INTO message (conversation_id, role, text, at) VALUES ($id, $role, $text, $at);
            """;
        string title = message.Text.Split(['\r', '\n'], 2)[0].Trim();
        var characters = new StringInfo(title);
        if (characters.LengthInTextElements > 42)
            title = characters.SubstringByTextElements(0, 42) + "…";
        command.Parameters.AddWithValue("$id", conversationId);
        command.Parameters.AddWithValue("$title", title.Length > 0 ? title : "会话");
        command.Parameters.AddWithValue("$role", message.Role);
        command.Parameters.AddWithValue("$text", message.Text);
        command.Parameters.AddWithValue("$at", message.At.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public List<Conversation> Conversations(string query)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.title, c.updated FROM conversation c
            WHERE $query = '' OR instr(lower(c.title), lower($query)) > 0 OR EXISTS
                (SELECT 1 FROM message m WHERE m.conversation_id = c.id AND instr(lower(m.text), lower($query)) > 0)
            ORDER BY c.updated DESC, c.id;
            """;
        command.Parameters.AddWithValue("$query", query.Trim());
        using SqliteDataReader reader = command.ExecuteReader();
        List<Conversation> result = [];
        while (reader.Read())
            result.Add(new Conversation(reader.GetString(0), reader.GetString(1), FromUnix(reader.GetInt64(2))));
        return result;
    }

    public List<Message> Messages(string id)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT role, text, at FROM message WHERE conversation_id = $id ORDER BY sequence;";
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        List<Message> result = [];
        while (reader.Read())
            result.Add(new Message(reader.GetString(0), reader.GetString(1), FromUnix(reader.GetInt64(2))));
        return result;
    }

    public void DeleteConversation(string id) => Delete("DELETE FROM conversation WHERE id = $id;", id);
    public void DeleteAppointment(string id) => Delete("DELETE FROM appointment WHERE id = $id;", id);

    private void Delete(string sql, string id)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public string Export(Conversation conversation, bool json)
    {
        List<Message> messages = Messages(conversation.Id);
        if (json)
            return JsonSerializer.Serialize(new { conversation, messages }, JsonDefaults.Options);
        StringBuilder body = new();
        body.Append("# ").AppendLine(conversation.Title);
        foreach (Message message in messages)
        {
            body.AppendLine().Append("## ").Append(message.Role == "user" ? "你" : "助手")
                .Append(" · ").AppendLine(message.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
                .AppendLine().AppendLine(message.Text);
        }
        return body.ToString();
    }

    public List<Appointment> Appointments(DateOnly day)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT id, day, start, end, title, note, done FROM appointment WHERE day = $day ORDER BY start, title;";
        command.Parameters.AddWithValue("$day", DayKey(day));
        return ReadAppointments(command);
    }

    public void SaveAppointment(Appointment appointment)
    {
        appointment.Validate();
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO appointment (id, day, start, end, title, note, done)
            VALUES ($id, $day, $start, $end, $title, $note, $done)
            ON CONFLICT(id) DO UPDATE SET
                reminded = CASE WHEN day <> excluded.day OR start <> excluded.start OR (done = 1 AND excluded.done = 0)
                                THEN 0 ELSE reminded END,
                day = excluded.day, start = excluded.start, end = excluded.end,
                title = excluded.title, note = excluded.note, done = excluded.done;
            """;
        command.Parameters.AddWithValue("$id", appointment.Id);
        command.Parameters.AddWithValue("$day", DayKey(appointment.Day));
        command.Parameters.AddWithValue("$start", TimeKey(appointment.Start));
        command.Parameters.AddWithValue("$end", appointment.End is TimeOnly end ? TimeKey(end) : DBNull.Value);
        command.Parameters.AddWithValue("$title", appointment.Title.Trim());
        command.Parameters.AddWithValue("$note", appointment.Note.Trim());
        command.Parameters.AddWithValue("$done", appointment.Done);
        command.ExecuteNonQuery();
    }

    public List<Appointment> ClaimReminders(DateTimeOffset now)
    {
        using SqliteConnection db = Open();
        using SqliteTransaction tx = db.BeginTransaction();
        using SqliteCommand command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT id, day, start, end, title, note, done FROM appointment
            WHERE day BETWEEN $today AND $tomorrow AND done = 0 AND reminded = 0
            ORDER BY day, start;
            """;
        DateOnly today = DateOnly.FromDateTime(now.LocalDateTime);
        command.Parameters.AddWithValue("$today", DayKey(today));
        command.Parameters.AddWithValue("$tomorrow", DayKey(DateOnly.FromDateTime(now.AddMinutes(5).LocalDateTime)));
        List<Appointment> due = ReadAppointments(command).FindAll(item =>
        {
            DateTimeOffset start = item.StartsAt();
            return start >= now && start <= now.AddMinutes(5);
        });
        command.Parameters.Clear();
        command.CommandText = "UPDATE appointment SET reminded = 1 WHERE id = $id;";
        SqliteParameter id = command.Parameters.Add("$id", SqliteType.Text);
        foreach (Appointment item in due)
        {
            id.Value = item.Id;
            command.ExecuteNonQuery();
        }
        tx.Commit();
        return due;
    }

    private static List<Appointment> ReadAppointments(SqliteCommand command)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        List<Appointment> result = [];
        while (reader.Read())
        {
            result.Add(new Appointment(reader.GetString(0),
                DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                TimeOnly.ParseExact(reader.GetString(2), "HH:mm", CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? null : TimeOnly.ParseExact(reader.GetString(3), "HH:mm", CultureInfo.InvariantCulture),
                reader.GetString(4), reader.GetString(5), reader.GetBoolean(6)));
        }
        return result;
    }

    private static DateTimeOffset FromUnix(long time) => DateTimeOffset.FromUnixTimeMilliseconds(time);
    private static string DayKey(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string TimeKey(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
}
