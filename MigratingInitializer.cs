using System;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Data.SqlClient;
using System.Linq;
using MFramework.Common.Core.Collections.Extensions;
using MFramework.EF.Core;
using MFramework.EF.Core.Indexes;
using MFramework.EF.Resources;

namespace MFramework.EF.Migrations.SqlServer
{
	/// <summary>
	/// Base class for all migrating initializer for SQL Server
	/// that are based on Red Gate tools
	/// </summary>
	/// <typeparam name="TContext"></typeparam>
	public abstract class MigratingInitializer<TContext> : IMigratingInitializer<TContext>
		where TContext : ExtendedDbContext
	{
		private readonly INewVersionProvider<TContext> _versionProvider;

		/// <summary>
		/// New instance of the initializer
		/// </summary>
		/// <param name="versionProvider">Version provider to use</param>
		protected MigratingInitializer(INewVersionProvider<TContext> versionProvider)
		{
			MigrationsEnabled = true;
			_versionProvider = versionProvider;
		}

		/// <summary>
		/// Called just before migration
		/// </summary>
		/// <param name="context">Instance of DbContext being migrated</param>
		protected abstract void BeforeMigration(TContext context);

		/// <summary>
		/// Called just after migration
		/// </summary>
		/// <param name="context">Instance of DbContext being migrated</param>
		protected abstract void AfterMigration(TContext context);

		/// <summary>
		/// Initialize database
		/// </summary>
		/// <param name="context">DbContext to create database for</param>
		public void InitializeDatabase(TContext context)
		{
			if (MigrationsEnabled)
			{
				var contextAdapter = (IObjectContextAdapter)context;

				string newVersion = _versionProvider.GetNewVersion(context);

				if (contextAdapter.ObjectContext.DatabaseExists())
				{

					string oldVersion = GetOldVersion(context);
					if (oldVersion != newVersion)
					{
						BeforeMigration(context);

						string databaseName = GetDatabaseName(context.Database.Connection.ConnectionString);
						string temporaryDatabaseName = GetTemporaryDatabaseName(databaseName);

						string sourceConnectionString =
							GetTemporaryConnectionString(context.Database.Connection.ConnectionString, temporaryDatabaseName);

						CreateTemporaryDatabase(sourceConnectionString);

						var migrator = new DatabaseMigrator();
						migrator.Migrate(
							GetTemporaryConnectionString(sourceConnectionString, temporaryDatabaseName),
							GetTemporaryConnectionString(sourceConnectionString, databaseName));

						contextAdapter.ObjectContext.ExecuteStoreCommand(GetDropDatabaseStatement(temporaryDatabaseName),
																		 new object[] { });

						AfterMigration(context);

						UpdateVersion(context, newVersion);
					}
				}
				else
				{
					BeforeMigration(context);

					var creatingInitializer = new DropCreateDatabaseAlways<TContext>();
					creatingInitializer.InitializeDatabase(context);
					ApplyDefaults(context);
					ApplyIndexes(context);

					AfterMigration(context);

					UpdateVersion(context, newVersion);
				}



				MigrationsEnabled = false;
			}
		}

		private string GetOldVersion(TContext context)
		{
			return context.Set<DbVersion>().First().CurrentVersion;
		}

		private void UpdateVersion(TContext context, string newVersion)
		{
			var version = context.Set<DbVersion>().FirstOrDefault();
			if (version == null)
			{
				version = new DbVersion();
				context.Set<DbVersion>().Add(version);
			}
			version.CurrentVersion = newVersion;
			version.LastUpdatedOn = DateTime.Now;
			context.SaveChanges();
		}

		private static string GetDropDatabaseStatement(string databaseName)
		{
			var sql =
				"ALTER DATABASE {0} SET OFFLINE WITH ROLLBACK IMMEDIATE  " +
				"ALTER DATABASE {0} SET ONLINE " +
				"Drop Database {0} ";
			sql = string.Format(sql, databaseName);
			return sql;
		}

		private void CreateTemporaryDatabase(string sourceConnectionString)
		{
			var constructorInfo = typeof(TContext).GetConstructor(new[] { typeof(string) });
			if (constructorInfo != null)
			{
				var temporaryContext = (TContext)constructorInfo.Invoke(new object[] { sourceConnectionString });
				var creatingInitializer = new DropCreateDatabaseAlways<TContext>();
				creatingInitializer.InitializeDatabase(temporaryContext);
				ApplyDefaults(temporaryContext);
				ApplyIndexes(temporaryContext);
			}
			else
			{
				throw new MigrationException(string.Format(MainResource.ContextMissingConstructor, typeof(TContext).Name), null);
			}
		}

		private string GetTemporaryConnectionString(string connectionString, string databaseName)
		{
			var builder =
				new SqlConnectionStringBuilder
					{
						ConnectionString = connectionString,
						InitialCatalog = databaseName
					};
			return builder.ConnectionString;
		}

		private string GetTemporaryDatabaseName(string databaseName)
		{
			return string.Concat(databaseName, "_Migrations_",
								 Guid.NewGuid().ToString().Replace("-", ""));
		}

		private string GetDatabaseName(string connectionString)
		{
			var builder = new SqlConnectionStringBuilder { ConnectionString = connectionString };
			return builder.InitialCatalog;
		}

		private void ApplyDefaults(TContext context)
		{
			const string createDefaultSql = "ALTER TABLE {0} ADD CONSTRAINT DF_{0}_{1} DEFAULT {2} FOR {1}";
			const string checkTableSql = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.Tables WHERE TABLE_NAME='{0}'";
			var contextAdapter = (IObjectContextAdapter)context;
			var defaults = context.GetDefaults();
			defaults.ForEach(
				one =>
				{
					string tableName = string.Empty;
					one.PossibleTableNames.ForEach(
						table =>
						{
							if (contextAdapter.ObjectContext.ExecuteStoreQuery<string>(
								string.Format(checkTableSql, table), new object[] { }).FirstOrDefault() != null)
							{
								tableName = table;
							}
						});
					contextAdapter.ObjectContext.ExecuteStoreCommand(
						string.Format(createDefaultSql, tableName, one.ColumnName, one.DefaultValueExpression), new object[] { });

				});
		}

		private void ApplyIndexes(TContext context)
		{
			const string createIndexSql = "CREATE NONCLUSTERED INDEX {0} ON {1} ({2})";
			const string checkTableSql = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.Tables WHERE TABLE_NAME='{0}'";
			var contextAdapter = (IObjectContextAdapter)context;
			var indexes = context.GetIndexes().OrderBy(one => one.DelimitedTableNames).ThenBy(two => two.IndexName).ThenBy(three => three.OrdinalPoistion);
			string lastTableNames = string.Empty;
			string lastIndexName = string.Empty;
			string columns = string.Empty;
			string tableName = string.Empty;
			indexes.ForEach(
				one =>
				{
					if (one.DelimitedTableNames != lastTableNames || lastIndexName != one.IndexName)
					{
						tableName = string.Empty;
						//create index
						if (!string.IsNullOrEmpty(lastTableNames))
						{
							lastTableNames.Split(new string[] { "!" }, StringSplitOptions.RemoveEmptyEntries).ForEach(
								table =>
								{
									if (contextAdapter.ObjectContext.ExecuteStoreQuery<string>(
										string.Format(checkTableSql, table), new object[] { }).FirstOrDefault() != null)
									{
										tableName = table;
									}
								});
							if (columns.EndsWith(","))
							{
								columns = columns.Substring(0, columns.Length - 1);
							}
							contextAdapter.ObjectContext.ExecuteStoreCommand(
								string.Format(createIndexSql, lastIndexName, tableName, columns), new object[] { });
						}
						lastTableNames = one.DelimitedTableNames;
						lastIndexName = one.IndexName;
						columns = (one.Direction == IndexDirection.Ascending ? one.ColumnName + " ASC " : one.ColumnName + " DESC ") + ",";
					}
					else
					{
						columns = columns + (one.Direction == IndexDirection.Ascending ? one.ColumnName + " ASC " : one.ColumnName + " DESC ") + ",";
					}
				});
			// create last index
			if (!string.IsNullOrEmpty(columns))
			{
				var one = indexes.Last();
				one.PossibleTableNames.ForEach(
								table =>
								{
									if (contextAdapter.ObjectContext.ExecuteStoreQuery<string>(
										string.Format(checkTableSql, table), new object[] { }).FirstOrDefault() != null)
									{
										tableName = table;
									}
								});
				if (columns.EndsWith(","))
				{
					columns = columns.Substring(0, columns.Length - 1);
				}
				contextAdapter.ObjectContext.ExecuteStoreCommand(
					string.Format(createIndexSql, lastIndexName, tableName, columns), new object[] { });
			}
		}

		/// <summary>
		/// Indicates if migrations are enabled
		/// Turned off after the first run of the instance
		/// </summary>
		public bool MigrationsEnabled { get; set; }
	}
}
