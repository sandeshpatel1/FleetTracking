using Microsoft.Data.SqlClient;

namespace TrackingMVC.Data
{
    public class DbHelper
    {
        private readonly string _connectionString;

        public DbHelper(IConfiguration config)
        {
            _connectionString = config.GetConnectionString("TrackingDB")
                ?? throw new InvalidOperationException("TrackingDB connection string not found in appsettings.json");
        }

        public SqlConnection GetConnection() => new SqlConnection(_connectionString);
    }
}
