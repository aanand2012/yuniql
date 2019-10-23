﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Transactions;

namespace ArdiLabs.Yuniql
{
    public class MigrationService : IMigrationService
    {
        public void Run(string workingPath,
            string connectionString,
            string targetVersion,
            bool autoCreateDatabase,
            List<KeyValuePair<string, string>> tokens = null)
        {
            var targetSqlDbConnectionString = new SqlConnectionStringBuilder(connectionString);

            //check if database exists and auto-create when its not
            var masterSqlDbConnectionString = new SqlConnectionStringBuilder(connectionString);
            masterSqlDbConnectionString.InitialCatalog = "master";

            var targetDatabaseExists = IsTargetDatabaseExists(masterSqlDbConnectionString, targetSqlDbConnectionString.InitialCatalog);
            if (!targetDatabaseExists && autoCreateDatabase)
            {
                CreateDatabase(masterSqlDbConnectionString, targetSqlDbConnectionString.InitialCatalog);
                TraceService.Info($"Created database {targetSqlDbConnectionString.InitialCatalog} on {targetSqlDbConnectionString.DataSource}.");
            }

            //check if database has been pre-configured to support migration and setup when its not
            var targetDatabaseConfigured = IsTargetDatabaseConfigured(targetSqlDbConnectionString);
            if (!targetDatabaseConfigured)
            {
                ConfigureDatabase(targetSqlDbConnectionString);
                TraceService.Info($"Configured migration support of {targetSqlDbConnectionString.InitialCatalog} on {targetSqlDbConnectionString.DataSource}.");

                //runs all scripts in the _init folder
                RunNonVersionScripts(targetSqlDbConnectionString, Path.Combine(workingPath, "_init"), tokens);
                TraceService.Info($"Executed script files on {Path.Combine(workingPath, "_init")}");
            }

            //checks if target database already runs the latest version and skips work if it already is
            if (!IsTargetDatabaseLatest(targetSqlDbConnectionString, targetVersion))
            {
                //runs all scripts in the _pre folder and subfolders
                RunNonVersionScripts(targetSqlDbConnectionString, Path.Combine(workingPath, "_pre"), tokens);
                TraceService.Info($"Executed script files on {Path.Combine(workingPath, "_pre")}");

                //runs all scripts int the vxx.xx folders and subfolders
                RunVersionScripts(targetSqlDbConnectionString, workingPath, targetVersion, tokens);

                //runs all scripts in the _draft folder and subfolders
                RunNonVersionScripts(targetSqlDbConnectionString, Path.Combine(workingPath, "_draft"), tokens);
                TraceService.Info($"Executed script files on {Path.Combine(workingPath, "_draft")}");

                //runs all scripts in the _post folder and subfolders
                RunNonVersionScripts(targetSqlDbConnectionString, Path.Combine(workingPath, "_post"), tokens);
                TraceService.Info($"Executed script files on {Path.Combine(workingPath, "_post")}");
            }
            else
            {
                TraceService.Info($"Target database is updated. No changes made at {targetSqlDbConnectionString.InitialCatalog} on {targetSqlDbConnectionString.DataSource}.");
            }
        }

        public bool IsTargetDatabaseExists(SqlConnectionStringBuilder sqlConnectionString, string targetDatabaseName)
        {
            var sqlStatement = $"SELECT ISNULL(DB_ID (N'{targetDatabaseName}'),0);";
            var result = DbHelper.QuerySingleBool(sqlConnectionString, sqlStatement);

            return result;
        }

        private bool IsTargetDatabaseLatest(SqlConnectionStringBuilder sqlConnectionString, string targetVersion)
        {
            var cv = new LocalVersion(GetCurrentVersion(sqlConnectionString));
            var tv = new LocalVersion(targetVersion);

            return string.Compare(cv.SemVersion, tv.SemVersion) == 1 || //db has more updated than local version
                string.Compare(cv.SemVersion, tv.SemVersion) == 0;      //db has the same version as local version
        }

        private void CreateDatabase(SqlConnectionStringBuilder sqlConnectionString, string targetDatabaseName)
        {
            var sqlStatement = $"CREATE DATABASE {targetDatabaseName};";
            DbHelper.ExecuteNonQuery(sqlConnectionString, sqlStatement);
        }

        private bool IsTargetDatabaseConfigured(SqlConnectionStringBuilder sqlConnectionString)
        {
            var sqlStatement = $"SELECT ISNULL(OBJECT_ID('dbo.__YuniqlDbVersion'),0) IsDatabaseConfigured";
            var result = DbHelper.QuerySingleBool(sqlConnectionString, sqlStatement);

            return result;
        }

        private void ConfigureDatabase(SqlConnectionStringBuilder sqlConnectionString)
        {
            var sqlStatement = $@"
                    USE {sqlConnectionString.InitialCatalog};

                    IF OBJECT_ID('[dbo].[__YuniqlDbVersion]') IS NULL 
                    BEGIN
	                    CREATE TABLE [dbo].[__YuniqlDbVersion](
		                    [Id] [SMALLINT] IDENTITY(1,1),
		                    [Version] [NVARCHAR](32) NOT NULL,
		                    [DateInsertedUtc] [DATETIME] NOT NULL,
		                    [LastUpdatedUtc] [DATETIME] NOT NULL,
		                    [LastUserId] [NVARCHAR](128) NOT NULL,
		                    [Artifact] [VARBINARY](MAX) NULL,
		                    CONSTRAINT [PK___YuniqlDbVersion] PRIMARY KEY CLUSTERED ([Id] ASC),
		                    CONSTRAINT [IX___YuniqlDbVersion] UNIQUE NONCLUSTERED ([Version] ASC)
	                    );

	                    ALTER TABLE [dbo].[__YuniqlDbVersion] ADD  CONSTRAINT [DF___YuniqlDbVersion_DateInsertedUtc]  DEFAULT (GETUTCDATE()) FOR [DateInsertedUtc];
	                    ALTER TABLE [dbo].[__YuniqlDbVersion] ADD  CONSTRAINT [DF___YuniqlDbVersion_LastUpdatedUtc]  DEFAULT (GETUTCDATE()) FOR [LastUpdatedUtc];
	                    ALTER TABLE [dbo].[__YuniqlDbVersion] ADD  CONSTRAINT [DF___YuniqlDbVersion_LastUserId]  DEFAULT (SUSER_SNAME()) FOR [LastUserId];

	                    --creates default version
	                    INSERT INTO [dbo].[__YuniqlDbVersion] (Version) VALUES('v0.00');
                    END                
            ";

            TraceService.Debug($"Executing sql statement: {Environment.NewLine}{sqlStatement}");

            using (var connection = new SqlConnection(sqlConnectionString.ConnectionString))
            {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandType = CommandType.Text;
                command.CommandText = sqlStatement;
                command.CommandTimeout = 0;
                command.ExecuteNonQuery();
            }
        }

        private string GetCurrentVersion(SqlConnectionStringBuilder sqlConnectionString)
        {
            var sqlStatement = $"SELECT TOP 1 Version FROM [dbo].[__YuniqlDbVersion] ORDER BY Id DESC;";
            var result = DbHelper.QuerySingleString(sqlConnectionString, sqlStatement);

            return result;
        }

        private void IncrementVersion(SqlConnectionStringBuilder sqlConnectionString, string nextVersion)
        {
            var sqlStatement = $"INSERT INTO [dbo].[__YuniqlDbVersion] (Version) VALUES (N'{nextVersion}');";
            DbHelper.ExecuteScalar(sqlConnectionString, sqlStatement);
        }

        public List<DbVersion> GetAllDbVersions(SqlConnectionStringBuilder sqlConnectionString)
        {
            var result = new List<DbVersion>();

            var sqlStatement = $"SELECT Id, Version, DateInsertedUtc, LastUserId FROM [dbo].[__YuniqlDbVersion] ORDER BY Version ASC;";
            TraceService.Debug($"Executing sql statement: {Environment.NewLine}{sqlStatement}");

            using (var connection = new SqlConnection(sqlConnectionString.ConnectionString))
            {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandType = CommandType.Text;
                command.CommandText = sqlStatement;
                command.CommandTimeout = 0;

                var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var dbVersion = new DbVersion
                    {
                        Id = reader.GetInt16(0),
                        Version = reader.GetString(1),
                        DateInsertedUtc = reader.GetDateTime(2),
                        LastUserId = reader.GetString(3)
                    };
                    result.Add(dbVersion);
                }
            }
            return result;
        }

        private void RunNonVersionScripts(
            SqlConnectionStringBuilder sqlConnectionString,
            string directoryFullPath,
            List<KeyValuePair<string, string>> tokens = null)
        {
            var sqlScriptFiles = Directory.GetFiles(directoryFullPath, "*.sql").ToList();
            TraceService.Info($"Found the {sqlScriptFiles.Count} script files on {directoryFullPath}");
            TraceService.Info($"{string.Join(@"\r\n\t", sqlScriptFiles.Select(s => new FileInfo(s).Name))}");

            using (var connection = new SqlConnection(sqlConnectionString.ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        //execute all script files in the target folder
                        sqlScriptFiles.ForEach(scriptFile =>
                        {
                            //https://stackoverflow.com/questions/25563876/executing-sql-batch-containing-go-statements-in-c-sharp/25564722#25564722
                            var sqlStatementRaw = File.ReadAllText(scriptFile);
                            var sqlStatements = Regex.Split(sqlStatementRaw, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .ToList()
;
                            sqlStatements.ForEach(sqlStatement =>
                            {
                                //replace tokens with values from the cli
                                var tokeReplacementService = new TokenReplacementService();
                                sqlStatement = tokeReplacementService.Replace(tokens, sqlStatement);

                                TraceService.Debug($"Executing sql statement as part of : {scriptFile}{Environment.NewLine}{sqlStatement}");

                                var command = connection.CreateCommand();
                                command.Transaction = transaction;
                                command.CommandType = CommandType.Text;
                                command.CommandText = sqlStatement;
                                command.CommandTimeout = 0;
                                command.ExecuteNonQuery();
                            });

                            TraceService.Info($"Executed script file {scriptFile}.");
                        });

                        transaction.Commit();
                    }
                    catch (Exception)
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
        }

        private void RunVersionScripts(
            SqlConnectionStringBuilder targetSqlDbConnectionString,
            string workingPath,
            string targetVersion,
            List<KeyValuePair<string, string>> tokens = null)
        {
            var currentVersion = GetCurrentVersion(targetSqlDbConnectionString);
            var dbVersions = GetAllDbVersions(targetSqlDbConnectionString)
                    .Select(dv => dv.Version)
                    .OrderBy(v => v);

            //excludes all versions already executed
            var versionFolders = Directory.GetDirectories(workingPath, "v*.*")
                .Where(v => !dbVersions.Contains(new DirectoryInfo(v).Name))
                .ToList();

            //exclude all versions greater than the target version
            if (!string.IsNullOrEmpty(targetVersion))
            {
                versionFolders.RemoveAll(v =>
                {
                    var cv = new LocalVersion(new DirectoryInfo(v).Name);
                    var tv = new LocalVersion(targetVersion);

                    return string.Compare(cv.SemVersion, tv.SemVersion) == 1;
                });
            }

            //execute all sql scripts in the version folders
            if (versionFolders.Any())
            {
                versionFolders.Sort();
                versionFolders.ForEach(versionDirectory =>
                {
                    //using (var transactionScope =  new TransactionScope())
                    //{
                        try
                        {
                            //run scripts in all sub-directories
                            var versionSubDirectories = Directory.GetDirectories(versionDirectory, "*", SearchOption.AllDirectories).ToList();
                            versionSubDirectories.ForEach(versionSubDirectory =>
                            {
                                RunMigrationScriptsInternal(targetSqlDbConnectionString, versionSubDirectory, tokens);
                            });

                            //run all scripts in the current version folder
                            RunMigrationScriptsInternal(targetSqlDbConnectionString, versionDirectory, tokens);

                            //import csv files into tables of the the same filename as the csv
                            RunCsvImport(targetSqlDbConnectionString, versionDirectory);

                            //update db version
                            var versionName = new DirectoryInfo(versionDirectory).Name;
                            UpdateDbVersion(targetSqlDbConnectionString, versionName);

                            //commit all changes
                            //transactionScope.Complete();

                            TraceService.Info($"Completed migration to version {versionDirectory}");
                        }
                        catch (Exception)
                        {
                            //transactionScope.Dispose();
                            throw;
                        }
                    //}
                });
            }
            else
            {
                TraceService.Info($"Target database is updated. No migration step executed at {targetSqlDbConnectionString.InitialCatalog} on {targetSqlDbConnectionString.DataSource}.");
            }
        }

        private void RunCsvImport(SqlConnectionStringBuilder sqlConnectionString, string versionFullPath)
        {
            //execute all script files in the version folder
            var csvFiles = Directory.GetFiles(versionFullPath, "*.csv").ToList();
            TraceService.Info($"Found the {csvFiles.Count} csv files on {versionFullPath}");
            TraceService.Info($"{string.Join(@"\r\n\t", csvFiles.Select(s => new FileInfo(s).Name))}");

            using (var connection = new SqlConnection(sqlConnectionString.ConnectionString))
            {
                connection.Open();
                csvFiles.ForEach(csvFile =>
                {
                    var csvImportService = new CsvImportService();
                    csvImportService.Run(sqlConnectionString, csvFile);
                    TraceService.Info($"Imported csv file {csvFile}.");
                });
            }
        }

        private void RunMigrationScriptsInternal(SqlConnectionStringBuilder sqlConnectionString, string versionFullPath, List<KeyValuePair<string, string>> tokens = null)
        {
            var sqlScriptFiles = Directory.GetFiles(versionFullPath, "*.sql").ToList();
            TraceService.Info($"Found the {sqlScriptFiles.Count} script files on {versionFullPath}");
            TraceService.Info($"{string.Join(@"\r\n\t", sqlScriptFiles.Select(s=> new FileInfo(s).Name))}");

            using (var connection = new SqlConnection(sqlConnectionString.ConnectionString))
            {
                connection.Open();
                //execute all script files in the version folder
                sqlScriptFiles.ForEach(scriptFile =>
                {
                    //https://stackoverflow.com/questions/25563876/executing-sql-batch-containing-go-statements-in-c-sharp/25564722#25564722
                    var sqlStatementRaw = File.ReadAllText(scriptFile);
                    var sqlStatements = Regex.Split(sqlStatementRaw, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList()
;
                    sqlStatements.ForEach(sqlStatement =>
                    {
                        //replace tokens with values from the cli
                        var tokeReplacementService = new TokenReplacementService();
                        sqlStatement = tokeReplacementService.Replace(tokens, sqlStatement);

                        TraceService.Debug($"Executing sql statement as part of : {scriptFile}{Environment.NewLine}{sqlStatement}");

                        var command = connection.CreateCommand();
                        command.CommandType = CommandType.Text;
                        command.CommandText = sqlStatement;
                        command.CommandTimeout = 0;
                        command.ExecuteNonQuery();
                    });

                    TraceService.Info($"Executed script file {scriptFile}.");
                });
            }
        }

        private void UpdateDbVersion(SqlConnectionStringBuilder sqlConnectionString, string version)
        {
            using (var connection = new SqlConnection(sqlConnectionString.ConnectionString))
            {
                connection.Open();

                var incrementVersionSqlStatement = $"INSERT INTO [dbo].[__YuniqlDbVersion] (Version) VALUES ('{version}');";
                TraceService.Debug($"Executing sql statement: {Environment.NewLine}{incrementVersionSqlStatement}");

                var incrementVersioncommand = connection.CreateCommand();
                incrementVersioncommand.CommandType = CommandType.Text;
                incrementVersioncommand.CommandText = incrementVersionSqlStatement;
                incrementVersioncommand.CommandTimeout = 0;
                incrementVersioncommand.ExecuteNonQuery();
            }
        }
    }
}
