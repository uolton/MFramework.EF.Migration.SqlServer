using System;
using System.Data.SqlClient;
using RedGate.Shared.SQL;
using RedGate.Shared.SQL.ExecutionBlock;
using RedGate.SQLCompare.Engine;

namespace MFramework.EF.Migrations.SqlServer
{
    /// <summary>
    /// Database migrator class to be sued for SQL Server
    /// using Red Gate tools
    /// </summary>
    public class DatabaseMigrator : IDatabaseMigrator
    {
        private  Options _options = Options.Default | Options.IgnoreUsers | Options.ForceColumnOrder;

        /// <summary>
        /// Main function that performs migration
        /// </summary>
        /// <param name="sourceConnectionString">Connection string for source database</param>
        /// <param name="destinationConnectionString">Connection string for destination database</param>
        public void Migrate(string sourceConnectionString, string destinationConnectionString)
        {
            try
            {
                ConnectionProperties sourceConnectionProperties, destinationConnectionProperties;
                DBConnectionInformation info;
                var builder = new SqlConnectionStringBuilder { ConnectionString = sourceConnectionString };
                if (builder.IntegratedSecurity)
                {
                    sourceConnectionProperties = 
                        new ConnectionProperties(builder.DataSource, builder.InitialCatalog);
                }
                else
                {
                    sourceConnectionProperties = 
                        new ConnectionProperties(builder.DataSource, builder.InitialCatalog, builder.UserID, builder.Password);
                }

                builder.ConnectionString = destinationConnectionString;
                if (builder.IntegratedSecurity)
                {
                    destinationConnectionProperties = 
                        new ConnectionProperties(builder.DataSource, builder.InitialCatalog);
                    info = new DBConnectionInformation(builder.DataSource, builder.InitialCatalog);
                }
                else
                {
                    destinationConnectionProperties = 
                        new ConnectionProperties(builder.DataSource, builder.InitialCatalog, builder.UserID, builder.Password);
                    info = new DBConnectionInformation(builder.DataSource, builder.InitialCatalog, builder.UserID, builder.Password);
                }


                using (Database sourceDatabase = new Database(), destinationDatabase = new Database())
                {

                    sourceDatabase.Register(sourceConnectionProperties, _options);
                    destinationDatabase.Register(destinationConnectionProperties, _options);

                    Differences sourceVsDestination = sourceDatabase.CompareWith(destinationDatabase, _options);

                    foreach (Difference difference in sourceVsDestination)
                    {
                        difference.Selected = true;
                    }

                    var work = new Work();

                    work.BuildFromDifferences(sourceVsDestination, _options, true);

                    using (ExecutionBlock block = work.ExecutionBlock)
                    {
                        var executor = new BlockExecutor();
                        executor.ExecuteBlock(block, info);
                    }

                }
            }
            catch (Exception exception)
            {
                throw new MigrationException(exception);
            }
        }
    }
}
