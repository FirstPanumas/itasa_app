using Npgsql;

namespace itasa_app
{
    public class Myconnection
    {
        public NpgsqlConnection GetConnection()
        {

            string host = "localhost";
            string user = "postgres";
            string password = "admin";
            string database = "postgres";
            string port = "5432";

            try
            {

                string strConn = string.Format("Host={0};Username={1};Password={2};Database={3};Port={4}", host, user, password, database, port);
                NpgsqlConnection conn = new NpgsqlConnection(strConn);
                conn.Open();

                Console.WriteLine("Npgsql Connecting");
                return conn;

            }
            catch (Exception ex)
            {
                Console.WriteLine("Npgsql Error Cant Connnect");
                throw new Exception(ex.Message + string.Format("Host={0};Username={1};Password={2};Database={3};Port={4}", host, user, password, database, port));
            }

        }
    }
}
