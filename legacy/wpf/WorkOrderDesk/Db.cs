using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Data.SqlClient;

namespace WorkOrderDesk
{
    // Plain ADO.NET helpers. Screens write their SQL in code-behind on purpose (period style).
    public static class Db
    {
        public static SqlConnection Open()
        {
            var c = new SqlConnection(ConfigurationManager.ConnectionStrings["AdventureWorks"].ConnectionString);
            c.Open();
            return c;
        }

        public static DataTable Query(string sql, params SqlParameter[] ps)
        {
            using (var c = Open())
            using (var cmd = new SqlCommand(sql, c))
            {
                cmd.Parameters.AddRange(ps);
                var t = new DataTable();
                new SqlDataAdapter(cmd).Fill(t);
                return t;
            }
        }

        public static void Proc(string name, params SqlParameter[] ps)
        {
            using (var span = App.Source.StartActivity("WorkOrderDesk " + name, ActivityKind.Client))
            using (var c = Open())
            {
                // SESSION_CONTEXT lives on this connection, so the procedure below sees it and the outbox trigger
                // copies it into the row. SQL Server itself emits no spans; the outbox row is the carrier.
                if (span != null)
                    using (var ctx = new SqlCommand("EXEC sp_set_session_context N'traceparent', @tp", c))
                    {
                        ctx.Parameters.AddWithValue("@tp", span.Id);
                        ctx.ExecuteNonQuery();
                    }
                using (var cmd = new SqlCommand(name, c) { CommandType = CommandType.StoredProcedure })
                {
                    cmd.Parameters.AddRange(ps);
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }
}
