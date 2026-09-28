using Microsoft.Data.SqlClient;
using TrackingMVC.Models;

namespace TrackingMVC.Data
{
    /// <summary>
    /// Effective page access = role default, overridden per-user if a row
    /// exists in dbo.user_page_access for that (user, page). "admin" role
    /// always short-circuits to "everything allowed" — the whole point of
    /// this table is to restrict USERS, not admins.
    /// </summary>
    public class PagePermissionRepository
    {
        private readonly DbHelper _db;
        public PagePermissionRepository(DbHelper db) => _db = db;

        // ── Single-page check, used by [RequirePageAccess] on every request ──
        public async Task<bool> HasAccessAsync(int userId, string role, string pageKey)
        {
            if (string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase))
                return true;

            var overrideVal = await GetOverrideAsync(userId, pageKey);
            if (overrideVal.HasValue) return overrideVal.Value;

            return PageAccess.RoleDefaults.TryGetValue(role, out var pages) && pages.Contains(pageKey);
        }

        private async Task<bool?> GetOverrideAsync(int userId, string pageKey)
        {
            const string sql = @"SELECT [is_allowed] FROM [dbo].[user_page_access]
                                  WHERE [user_id] = @uid AND [page_key] = @pk";
            await using var con = _db.GetConnection();
            await con.OpenAsync();
            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@uid", userId);
            cmd.Parameters.AddWithValue("@pk", pageKey);
            var result = await cmd.ExecuteScalarAsync();
            return result == null ? (bool?)null : Convert.ToBoolean(result);
        }

        // ── Full effective set for one user — used to decide which nav links to show ──
        public async Task<HashSet<string>> GetEffectivePagesAsync(int userId, string role)
        {
            if (string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase))
                return new HashSet<string>(PageAccess.AllPageKeys, StringComparer.OrdinalIgnoreCase);

            var effective = new HashSet<string>(
                PageAccess.RoleDefaults.TryGetValue(role, out var defaults) ? defaults : Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            const string sql = @"SELECT [page_key],[is_allowed] FROM [dbo].[user_page_access] WHERE [user_id] = @uid";
            await using var con = _db.GetConnection();
            await con.OpenAsync();
            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@uid", userId);
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var key = rdr["page_key"].ToString()!;
                if (Convert.ToBoolean(rdr["is_allowed"])) effective.Add(key);
                else effective.Remove(key);
            }
            return effective;
        }

        // ── Admin grid: every non-admin user, with role default + overrides merged ──
        public async Task<List<UserPageAccessRow>> GetAllUsersWithAccessAsync()
        {
            var users = new List<UserPageAccessRow>();

            await using var con = _db.GetConnection();
            await con.OpenAsync();

            const string userSql = @"SELECT [id],[username],[full_name],[role]
                                      FROM [dbo].[login_users]
                                      WHERE LOWER([role]) <> 'admin'
                                      ORDER BY [username]";
            await using (var cmd = new SqlCommand(userSql, con))
            await using (var rdr = await cmd.ExecuteReaderAsync())
            {
                while (await rdr.ReadAsync())
                {
                    users.Add(new UserPageAccessRow
                    {
                        UserId = Convert.ToInt32(rdr["id"]),
                        Username = rdr["username"].ToString()!,
                        FullName = rdr["full_name"] == DBNull.Value ? "" : rdr["full_name"].ToString()!,
                        Role = rdr["role"].ToString()!
                    });
                }
            }

            if (users.Count == 0) return users;

            var byUser = users.ToDictionary(u => u.UserId);
            const string ovSql = @"SELECT [user_id],[page_key],[is_allowed] FROM [dbo].[user_page_access]";
            await using (var cmd = new SqlCommand(ovSql, con))
            await using (var rdr = await cmd.ExecuteReaderAsync())
            {
                while (await rdr.ReadAsync())
                {
                    var uid = Convert.ToInt32(rdr["user_id"]);
                    if (!byUser.TryGetValue(uid, out var u)) continue;
                    u.Overrides[rdr["page_key"].ToString()!] = Convert.ToBoolean(rdr["is_allowed"]);
                }
            }

            foreach (var u in users)
            {
                var defaults = PageAccess.RoleDefaults.TryGetValue(u.Role, out var d) ? d : Array.Empty<string>();
                foreach (var key in PageAccess.AllPageKeys)
                    u.EffectivePages[key] = u.Overrides.TryGetValue(key, out var ov) ? ov : defaults.Contains(key);
            }

            return users;
        }

        // Always writes one explicit override row per page key for this user —
        // every save "pins" the full set the admin submitted, rather than only
        // storing diffs from the role default. Simpler to reason about, and it
        // means unchecking a role-default page actually revokes it instead of
        // silently no-opping because "there's nothing to override".
        public async Task SaveUserAccessAsync(int userId, HashSet<string> allowedKeys, int updatedByUserId)
        {
            await using var con = _db.GetConnection();
            await con.OpenAsync();
            await using var txn = con.BeginTransaction();
            try
            {
                const string upsert = @"
                    MERGE [dbo].[user_page_access] AS target
                    USING (SELECT @uid AS user_id, @pk AS page_key) AS src
                      ON target.user_id = src.user_id AND target.page_key = src.page_key
                    WHEN MATCHED THEN
                        UPDATE SET is_allowed = @allowed, updated_by = @updatedBy, updated_at = GETDATE()
                    WHEN NOT MATCHED THEN
                        INSERT (user_id, page_key, is_allowed, updated_by, updated_at)
                        VALUES (@uid, @pk, @allowed, @updatedBy, GETDATE());";

                foreach (var key in PageAccess.AllPageKeys)
                {
                    await using var cmd = new SqlCommand(upsert, con, txn);
                    cmd.Parameters.AddWithValue("@uid", userId);
                    cmd.Parameters.AddWithValue("@pk", key);
                    cmd.Parameters.AddWithValue("@allowed", allowedKeys.Contains(key));
                    cmd.Parameters.AddWithValue("@updatedBy", updatedByUserId);
                    await cmd.ExecuteNonQueryAsync();
                }
                await txn.CommitAsync();
            }
            catch
            {
                await txn.RollbackAsync();
                throw;
            }
        }

        public async Task ResetUserToRoleDefaultAsync(int userId)
        {
            const string sql = "DELETE FROM [dbo].[user_page_access] WHERE [user_id] = @uid";
            await using var con = _db.GetConnection();
            await con.OpenAsync();
            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@uid", userId);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
