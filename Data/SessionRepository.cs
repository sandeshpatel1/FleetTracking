using Microsoft.Data.SqlClient;

namespace TrackingMVC.Data
{
    /// <summary>
    /// Enforces one active login session per user. "Active" means the
    /// stored session id has had a heartbeat within SessionTimeoutMinutes —
    /// if a browser was closed without logging out, the old session goes
    /// stale on its own and a fresh login is allowed again, instead of
    /// permanently locking the account.
    /// </summary>
    public class SessionRepository
    {
        private readonly DbHelper _db;
        public const int SessionTimeoutMinutes = 15;

        public SessionRepository(DbHelper db) => _db = db;

        /// <summary>Returns true if this user already has a live session elsewhere.</summary>
        public async Task<bool> HasActiveSessionAsync(int userId)
        {
            const string sql = @"
                SELECT 1
                FROM   [atmparking].[dbo].[login_users]
                WHERE  [id] = @id
                  AND  [active_session_id] IS NOT NULL
                  AND  [session_last_seen] >= DATEADD(MINUTE, -@timeout, GETDATE())";
            await using var con = _db.GetConnection();
            await con.OpenAsync();
            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@id", userId);
            cmd.Parameters.AddWithValue("@timeout", SessionTimeoutMinutes);
            var result = await cmd.ExecuteScalarAsync();
            return result != null;
        }

        /// <summary>Starts (or takes over) the one allowed session for this user.</summary>
        public async Task<Guid> StartSessionAsync(int userId)
        {
            var sessionId = Guid.NewGuid();
            const string sql = @"
                UPDATE [atmparking].[dbo].[login_users]
                SET    [active_session_id] = @sid, [session_last_seen] = GETDATE()
                WHERE  [id] = @id";
            await using var con = _db.GetConnection();
            await con.OpenAsync();
            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@id", userId);
            await cmd.ExecuteNonQueryAsync();
            return sessionId;
        }

        /// <summary>
        /// Called on every authenticated request. Refreshes the heartbeat and
        /// confirms this browser still holds the current session — returns
        /// false if a newer login has since taken over the account.
        /// </summary>
        public async Task<bool> TouchAsync(int userId, Guid sessionId)
        {
            const string sql = @"
                UPDATE [atmparking].[dbo].[login_users]
                SET    [session_last_seen] = GETDATE()
                WHERE  [id] = @id AND [active_session_id] = @sid";
            await using var con = _db.GetConnection();
            await con.OpenAsync();
            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@id", userId);
            cmd.Parameters.AddWithValue("@sid", sessionId);
            var rows = await cmd.ExecuteNonQueryAsync();
            return rows > 0;
        }

        public async Task EndSessionAsync(int userId)
        {
            const string sql = @"
                UPDATE [atmparking].[dbo].[login_users]
                SET    [active_session_id] = NULL, [session_last_seen] = NULL
                WHERE  [id] = @id";
            await using var con = _db.GetConnection();
            await con.OpenAsync();
            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@id", userId);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}