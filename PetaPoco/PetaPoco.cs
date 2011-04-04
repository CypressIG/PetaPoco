﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Configuration;
using System.Data.Common;
using System.Data;
using System.Text.RegularExpressions;
using System.Reflection;

namespace PetaPoco
{
	// Poco's marked [Explicit] require all column properties to be marked
	[AttributeUsage(AttributeTargets.Class)]
	public class ExplicitColumns : Attribute
	{
	}
	// For non-explicit pocos, causes a property to be ignored
	[AttributeUsage(AttributeTargets.Property)]
	public class Ignore : Attribute
	{
	}

	// For explicit pocos, marks property as a column
	[AttributeUsage(AttributeTargets.Property)]
	public class Column : Attribute
	{
		public Column() { }
		public Column(string name) { Name = name; }
		public string Name { get; set; }
	}

	// For explicit pocos, marks property as a column
	[AttributeUsage(AttributeTargets.Property)]
	public class ResultColumn : Column
	{
		public ResultColumn() { }
		public ResultColumn(string name) : base(name) {  }
	}

	// Specify the table name of a poco
	[AttributeUsage(AttributeTargets.Class)]
	public class TableName : Attribute
	{
		public TableName(string tableName)
		{
			Value = tableName;
		}
		public string Value { get; private set; }
	}

	// Specific the primary key of a poco class
	[AttributeUsage(AttributeTargets.Class)]
	public class PrimaryKey : Attribute
	{
		public PrimaryKey(string primaryKey)
		{
			Value = primaryKey;
		}

		public string Value { get; private set; }
	}

	// Results from paged request
	public class Page<T> where T:new()
	{
		public long CurrentPage { get; set; }
		public long TotalPages { get; set; }
		public long TotalItems { get; set; }
		public long ItemsPerPage { get; set; }
		public List<T> Items { get; set; }
	}

    public abstract class PocoFactory
    {
        public abstract IDbConnection CreateConnection();
        public abstract IDbCommand CreateCommand();
    }

    public class OraclePocoFactory : PocoFactory
    {
        private readonly string _assemblyName;
        private readonly string _connectionTypeName;
        private readonly string _commandTypeName;

        private Type _connectionType;
        private Type _commandType;

        public OraclePocoFactory(string assemblyName, string connectionTypeName, string commandTypeName)
        {
            _assemblyName = assemblyName;
            _connectionTypeName = connectionTypeName;
            _commandTypeName = commandTypeName;
        }

        public override IDbConnection CreateConnection()
        {
            if (_connectionType == null)
                _connectionType = ReflectHelper.TypeFromAssembly(_connectionTypeName, _assemblyName);

            if (_connectionType == null)
                throw new InvalidOperationException("Can't find Connection type: " + _connectionTypeName);

            return (IDbConnection)Activator.CreateInstance(_connectionType);
        }

        public override IDbCommand CreateCommand()
        {
            if (_commandType == null)
                _commandType = ReflectHelper.TypeFromAssembly(_commandTypeName, _assemblyName);

            var command = (IDbCommand)Activator.CreateInstance(_commandType);

            var oracleCommandBindByName = _commandType.GetProperty("BindByName");
            oracleCommandBindByName.SetValue(command, true, null);

            return command;
        }
    }

    public class DefaultPofoFactory : PocoFactory
    {
        private readonly DbProviderFactory _factory;

        public DefaultPofoFactory(DbProviderFactory factory)
        {
            _factory = factory;
        }

        public override IDbConnection CreateConnection()
        {
            return _factory.CreateConnection();
        }

        public override IDbCommand CreateCommand()
        {
            return _factory.CreateCommand();
        }
    }

	// Database class ... this is where most of the action happens
	public class Database
	{
		public Database(string connectionString, string providerName)
		{
			CommonConstruct(connectionString, providerName);
		}

		// Constructor
		public Database(string connectionStringName)
		{
			// Get connection string name
			if (connectionStringName == "")
				connectionStringName = ConfigurationManager.ConnectionStrings[0].Name;

			// Work out connection string and provider name
			var providerName = "System.Data.SqlClient";
			if (ConfigurationManager.ConnectionStrings[connectionStringName] != null)
			{
				if (!string.IsNullOrEmpty(ConfigurationManager.ConnectionStrings[connectionStringName].ProviderName))
					providerName = ConfigurationManager.ConnectionStrings[connectionStringName].ProviderName;
			}
			else
			{
				throw new InvalidOperationException("Can't find a connection string with the name '" + connectionStringName + "'");
			}

			// Store factory and connection string
			var connectionString = ConfigurationManager.ConnectionStrings[connectionStringName].ConnectionString;

			// Init
			CommonConstruct(connectionString, providerName);
		}

		void CommonConstruct(string connectionString, string providerName)
		{
			// Store settings
			_connectionString = connectionString;
			_providerName = providerName;
		    SetPocoFactory(providerName);
            SetInitialValues();
        }

        private void SetPocoFactory(string providerName)
        {
            if (!IsOracle())
            {
                _factory = new DefaultPofoFactory(DbProviderFactories.GetFactory(providerName));
            }
            else
            {
                _factory = new OraclePocoFactory("Oracle.DataAccess", "Oracle.DataAccess.Client.OracleConnection", "Oracle.DataAccess.Client.OracleCommand");
            }
        }

        private void SetInitialValues()
        {
            _transactionDepth = 0;

			// Check options
            if (_connectionString.IndexOf("Allow User Variables=true") >= 0 && IsMySql())
            {
                _paramPrefix = "?";
            }

            if (IsOracle())
            {
                _paramPrefix = ":";
            }
        }

        // Who are we talking too?
        public bool IsMySql() { return string.Compare(_providerName, "MySql.Data.MySqlClient", true) == 0; }
        public bool IsSqlServer() { return string.Compare(_providerName, "System.Data.SqlClient", true) == 0; }
        public bool IsOracle() { return string.Compare(_providerName, "Oracle.DataAccess.Client", true) == 0; }

		// Get the connection for this database
		public IDbConnection OpenConnection()
		{
			var c = _factory.CreateConnection();
			c.ConnectionString = _connectionString;
			c.Open();
			return c;
		}

		// Get a shared connection to be used when in a transaction
		ShareableConnection OpenSharedConnection()
		{
			if (_sharedConnection != null)
				return new ShareableConnection(_sharedConnection, true);
			else
				return new ShareableConnection(OpenConnection(), false);
		}

		// Helper to create a transaction scope
		public Transaction Transaction
		{
			get
			{
				return new Transaction(this);
			}
		}

		// Use by derived repo generated by T4 templates
		public virtual void OnBeginTransaction() { }
		public virtual void OnEndTransaction() { }

		// Start a new transaction, can be nested, every call must be
		//	matched by a call to AbortTransaction or CompleteTransaction
		// Use `using (var scope=db.Transaction) { scope.Complete(); }` to ensure correct semantics
		public void BeginTransaction()
		{
			_transactionDepth++;

			if (_transaction == null)
			{
				_sharedConnection = OpenConnection();
				_transaction = _sharedConnection.BeginTransaction();
				OnBeginTransaction();
			}

		}

		// Internal helper to cleanup transaction stuff
		void CleanupTransaction()
		{
			OnEndTransaction();

			_transaction.Dispose();
			_transaction = null;

			// Clean up connection
			_sharedConnection.Close();
			_sharedConnection.Dispose();
			_sharedConnection = null;
		}

		// Abort the entire outer most transaction scope
		public void AbortTransaction()
		{
			_transactionDepth--;

			if (_transaction != null)
			{
				// Rollback transaction
				_transaction.Rollback();
				CleanupTransaction();
			}
		}

		// Complete the transaction
		// To actually complete the whole transaction, every BeginTransaction must be matched
		// by a CompleteTransaction.
		public void CompleteTransaction()
		{
			_transactionDepth--;

			if (_transactionDepth == 0 && _transaction != null)
			{
				// Commit transaction
				_transaction.Commit();
				CleanupTransaction();
			}

		}

		// Add a parameter to a DB command
		static void AddParam(IDbCommand cmd, object item, string ParameterPrefix)
		{
			var p = cmd.CreateParameter();
			p.ParameterName = string.Format("{0}{1}", ParameterPrefix, cmd.Parameters.Count);
			if (item == null)
			{
				p.Value = DBNull.Value;
			}
			else
			{
				if (item.GetType() == typeof(Guid))
				{
					p.Value = item.ToString();
					p.DbType = DbType.String;
					p.Size = 4000;
				}
				else if (item.GetType() == typeof(string))
				{
					p.Size = (item as string).Length + 1;
					p.Value = item;
				}
				else
				{
					p.Value = item;
				}
			}

			cmd.Parameters.Add(p);
		}



		// Create a command
		public IDbCommand CreateCommand(IDbConnection connection, Sql sqlStatement)
		{
            // Set parameter prefix on Sql object
            sqlStatement.ParameterPrefix = _paramPrefix;

            var sql = sqlStatement.SQL;
            var args = sqlStatement.Arguments;

            // If we're in MySQL "Allow User Variables", we need to fix up parameter prefixes
			if (_paramPrefix == "?")
			{
				// Convert "@parameter" -> "?parameter"
				Regex paramReg = new Regex(@"(?<!@)@\w+");
				sql = paramReg.Replace(sql, m => "?" + m.Value.Substring(1));

				// Convert @@uservar -> @uservar and @@@systemvar -> @@systemvar
				sql = sql.Replace("@@", "@");
			}

			// Save the last sql and args
			_lastSql = sql;
			_lastArgs = args;

			IDbCommand result = null;
			result = _factory.CreateCommand();
			result.Connection = connection;
            result.CommandText = ModifySql(sql);
			result.Transaction = _transaction;
			if (args.Length > 0)
			{
				foreach (var item in args)
				{
					AddParam(result, item, _paramPrefix);
				}
			}
			return result;
		}

        public virtual string ModifySql(string sql)
        {
            return sql;
        }

		// Create a command
        IDbCommand CreateCommand(ShareableConnection connection, Sql sqlStatement)
		{
			return CreateCommand(connection.Connection, sqlStatement);
		}

        // Create a command
        IDbCommand CreateCommand(ShareableConnection connection, string sql, params object[] args)
        {
            var sqlStatement = new Sql(sql, args);
            return CreateCommand(connection.Connection, sqlStatement);
        }

		public virtual object ConvertValue(PropertyInfo dest, object src)
		{
			if (src == null || src.GetType() == typeof(DBNull))
				return null;

			if (src.GetType() == typeof(DateTime))
				return new DateTime(((DateTime)src).Ticks, DateTimeKind.Utc);

			if (!dest.PropertyType.IsAssignableFrom(src.GetType()))
				return Convert.ChangeType(src, dest.PropertyType, null);

			return src;
		}

        public virtual PocoColumn GetPropertyName(PocoData pd, string columnName)
        {
            PocoColumn pc;
            if (!pd.Columns.TryGetValue(columnName, out pc))
                pc = null;

            return pc;
        }

		// Create a poco object for the current record in a data reader
		T CreatePoco<T>(IDataReader r, PocoData pd, ref PocoColumn[] ColumnMap) where T : new()
		{
			var record = new T();

			// Create column map first time through
			if (ColumnMap == null)
			{
				var map = new List<PocoColumn>();
				for (var i = 0; i < r.FieldCount; i++)
				{
					var name = r.GetName(i);
				    var pc = GetPropertyName(pd, name);
					map.Add(pc);
				}
				ColumnMap = map.ToArray();
			}

			System.Diagnostics.Debug.Assert(ColumnMap.Length == r.FieldCount);

			for (var i = 0; i < r.FieldCount; i++)
			{
				PocoColumn pc = ColumnMap[i];
				if (pc == null)
					continue;

				try
				{
					pc.PropertyInfo.SetValue(record, ConvertValue(pc.PropertyInfo, r[i]), null);
				}
				catch (Exception x)
				{
					throw new Exception(string.Format("Failed to set property '{0}' for column '{1}' on object of type '{2}' - {3}",
																pc.PropertyInfo.Name, pc.ColumnName, typeof(T).Name, x.Message));
				}
			}

			return record;
		}

		public virtual void OnException(Exception x)
		{
			System.Diagnostics.Debug.WriteLine(x.ToString());
			System.Diagnostics.Debug.WriteLine(LastCommand);
		}

		// Execute a non-query command
		public int Execute(string sql, params object[] args)
		{
            return Execute(new Sql(sql, args));
		}

		string _paramPrefix = "@";

		public int Execute(Sql sql)
		{
            try
            {
                using (var conn = OpenSharedConnection())
                {
                    using (var cmd = CreateCommand(conn, sql))
                    {
                        return cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception x)
            {
                OnException(x);
                throw;
            }
		}

		// Execute and cast a scalar property
		public T ExecuteScalar<T>(string sql, params object[] args)
		{
            return ExecuteScalar<T>(new Sql(sql, args));
		}

		public T ExecuteScalar<T>(Sql sql)
		{
            try
            {
                using (var conn = OpenSharedConnection())
                {
                    using (var cmd = CreateCommand(conn, sql))
                    {
                        object val = cmd.ExecuteScalar();
                        return (T)Convert.ChangeType(val, typeof(T));
                    }
                }
            }
            catch (Exception x)
            {
                OnException(x);
                throw;
            }
		}

		string AddSelectClause<T>(string sql)
		{
			// Already present?
			if (sql.Trim().StartsWith("SELECT", StringComparison.InvariantCultureIgnoreCase))
				return sql;

			// Get the poco data for this type
			var pd = PocoData.ForType(typeof(T));
			return string.Format("SELECT {0} FROM {1} {2}", pd.QueryColumns, pd.TableName, sql);
		}

		// Return a typed list of pocos
		public List<T> Fetch<T>(string sql, params object[] args) where T : new()
		{
			try
			{
				using (var conn = OpenSharedConnection())
				{
					using (var cmd = CreateCommand(conn, AddSelectClause<T>(sql), args))
					{
						using (var r = cmd.ExecuteReader())
						{
							var l = new List<T>();
							var pd = PocoData.ForType(typeof(T));
							PocoColumn[] ColumnMap = null;
							while (r.Read())
							{
								l.Add(CreatePoco<T>(r, pd, ref ColumnMap));
							}
							return l;
						}
					}
				}
			}
			catch (Exception x)
			{
				OnException(x);
				throw;
			}
		}

		// Warning: scary regex follows
		static Regex rxColumns = new Regex(@"^\s*SELECT\s+((?:\((?>\((?<depth>)|\)(?<-depth>)|.?)*(?(depth)(?!))\)|.)*?)(?<!,\s+)\bFROM\b",
							RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);
		static Regex rxOrderBy = new Regex(@"\bORDER\s+BY\s+(?:\((?>\((?<depth>)|\)(?<-depth>)|.?)*(?(depth)(?!))\)|[\w\(\)\.])+(?:\s+(?:ASC|DESC))?(?:\s*,\s*(?:\((?>\((?<depth>)|\)(?<-depth>)|.?)*(?(depth)(?!))\)|[\w\(\)\.])+(?:\s+(?:ASC|DESC))?)*",
							RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

		public static bool SplitSqlForPaging(string sql, out string sqlCount, out string sqlSelectRemoved, out string sqlOrderBy)
		{
			sqlSelectRemoved = null;
			sqlCount = null;
			sqlOrderBy = null;

			// Extract the columns from "SELECT <whatever> FROM"
			var m = rxColumns.Match(sql);
			if (!m.Success)
				return false;

			// Save column list and replace with COUNT(*)
			Group g = m.Groups[1];
			sqlCount = sql.Substring(0, g.Index) + "COUNT(*) " + sql.Substring(g.Index + g.Length);
			sqlSelectRemoved = sql.Substring(g.Index);

			// Look for an "ORDER BY <whatever>" clause
			m = rxOrderBy.Match(sqlCount);
			if (!m.Success)
				return false;

			g = m.Groups[0];
			sqlOrderBy = g.ToString();
			sqlCount = sqlCount.Substring(0, g.Index) + sqlCount.Substring(g.Index + g.Length);

			return true;
		}

	
		public Page<T> Page<T>(long page, long itemsPerPage, string sql, params object[] args) where T : new()
		{
			// Add auto select clause
			sql=AddSelectClause<T>(sql);

			// Split the SQL into the bits we need
			string sqlCount, sqlSelectRemoved, sqlOrderBy;
			if (!SplitSqlForPaging(sql, out sqlCount, out sqlSelectRemoved, out sqlOrderBy))
				throw new Exception("Unable to parse SQL statement for paged query");

			// Setup the paged result
			var result = new Page<T>();
			result.CurrentPage = page;
			result.ItemsPerPage = itemsPerPage;
			result.TotalItems = ExecuteScalar<long>(sqlCount, args);
			result.TotalPages = result.TotalItems / itemsPerPage;
			if ((result.TotalItems % itemsPerPage)!=0)
				result.TotalPages++;


			// Build the SQL for the actual final result
			string sqlPage;
			if (IsSqlServer() || IsOracle())
			{
				// Ugh really?
				sqlSelectRemoved = rxOrderBy.Replace(sqlSelectRemoved, "");
				sqlPage = string.Format("SELECT * FROM (SELECT ROW_NUMBER() OVER ({0}) rn, {1}) paged WHERE rn>{2} AND rn<={3}",
										sqlOrderBy, sqlSelectRemoved, (page-1) * itemsPerPage, page * itemsPerPage);
			}
			else
			{
				// Nice
				sqlPage = string.Format("{0}\nLIMIT {1} OFFSET {2}", sql, itemsPerPage, (page-1) * itemsPerPage);
			}

			// Get the records
			result.Items = Fetch<T>(sqlPage, args);

			// Done
			return result;
		}

		public Page<T> Page<T>(long page, long itemsPerPage, Sql sql) where T : new()
		{
			return Page<T>(page, itemsPerPage, sql.SQL, sql.Arguments);
		}

		// Return an enumerable collection of pocos
		public IEnumerable<T> Query<T>(string sql, params object[] args) where T : new()
		{
			using (var conn = OpenSharedConnection())
			{
				using (var cmd = CreateCommand(conn, AddSelectClause<T>(sql), args))
				{
					IDataReader r;
					var pd = PocoData.ForType(typeof(T));
					PocoColumn[] ColumnMap=null;
					try
					{
						r = cmd.ExecuteReader();
					}
					catch (Exception x)
					{
						OnException(x);
						throw;
					}
					using (r)
					{
						while (true)
						{
							T poco;
							try
							{
								if (!r.Read())
									yield break;
								poco=CreatePoco<T>(r, pd, ref ColumnMap);
							}
							catch (Exception x)
							{
								OnException(x);
								throw;
							}

							yield return poco;
						}
					}
				}
			}
		}

		public T Single<T>(string sql, params object[] args) where T : new()
		{
			return Query<T>(sql, args).Single();
		}
		public T SingleOrDefault<T>(string sql, params object[] args) where T : new()
		{
			return Fetch<T>(sql, args).SingleOrDefault();
		}
		public T First<T>(string sql, params object[] args) where T : new()
		{
			return Query<T>(sql, args).First();
		}
		public T FirstOrDefault<T>(string sql, params object[] args) where T : new()
		{
			return Query<T>(sql, args).FirstOrDefault();
		}
		public List<T> Fetch<T>(Sql sql) where T : new()
		{
			return Fetch<T>(sql);
		}
		public IEnumerable<T> Query<T>(Sql sql) where T : new()
		{
			return Query<T>(sql);
		}
		public T Single<T>(Sql sql) where T : new()
		{
			return Query<T>(sql).Single();
		}
		public T SingleOrDefault<T>(Sql sql) where T : new()
		{
			return Query<T>(sql).SingleOrDefault();
		}
		public T First<T>(Sql sql) where T : new()
		{
			return Query<T>(sql).First();
		}
		public T FirstOrDefault<T>(Sql sql) where T : new()
		{
			return Query<T>(sql).FirstOrDefault();
		}


		// Insert a poco into a table.  If the poco has a property with the same name 
		// as the primary key the id of the new record is assigned to it.  Either way,
		// the new id is returned.
		public object Insert(string tableName, string primaryKeyName, object poco)
		{
			try
			{
				using (var conn = OpenSharedConnection())
				{
					using (var cmd = CreateCommand(conn, ""))
					{
						var pd = PocoData.ForType(poco.GetType());
						var names = new List<string>();
						var values = new List<string>();
						var index = 0;
						foreach (var i in pd.Columns)
						{
							// Don't insert the primary key or result only columns
							if ((primaryKeyName != null && i.Key == primaryKeyName) || i.Value.ResultColumn)
								continue;

							names.Add(i.Key);
							values.Add(string.Format("{0}{1}", _paramPrefix, index++));
							AddParam(cmd, i.Value.PropertyInfo.GetValue(poco, null), _paramPrefix);
						}

						cmd.CommandText = string.Format("INSERT INTO {0} ({1}) VALUES ({2}); SELECT @@IDENTITY AS NewID;",
								tableName,
								string.Join(",", names.ToArray()),
								string.Join(",", values.ToArray())
								);

						_lastSql = cmd.CommandText;
						_lastArgs = values.ToArray();

						// Insert the record, should get back it's ID
						var id = cmd.ExecuteScalar();

						// Assign the ID back to the primary key property
						if (primaryKeyName != null)
						{
							PocoColumn pc;
							if (pd.Columns.TryGetValue(primaryKeyName, out pc))
							{
								pc.PropertyInfo.SetValue(poco, Convert.ChangeType(id, pc.PropertyInfo.PropertyType), null);
							}
						}

						return id;
					}
				}
			}
			catch (Exception x)
			{
				OnException(x);
				throw;
			}
		}

		// Insert an annotated poco object
		public object Insert(object poco)
		{
			var pd = PocoData.ForType(poco.GetType());
			return Insert(pd.TableName, pd.PrimaryKey, poco);
		}

		public int Update(string tableName, string primaryKeyName, object poco)
		{
			return Update(tableName, primaryKeyName, poco, null);
		}


		// Update a record with values from a poco.  primary key value can be either supplied or read from the poco
		public int Update(string tableName, string primaryKeyName, object poco, object primaryKeyValue)
		{
			try
			{
				using (var conn = OpenSharedConnection())
				{
					using (var cmd = CreateCommand(conn, ""))
					{
						var sb = new StringBuilder();
						var index = 0;
						var pd = PocoData.ForType(poco.GetType());
						foreach (var i in pd.Columns)
						{
							// Don't update the primary key, but grab the value if we don't have it
							if (i.Key == primaryKeyName)
							{
								if (primaryKeyValue == null)
									primaryKeyValue = i.Value.PropertyInfo.GetValue(poco, null);
								continue;
							}

							// Dont update result only columns
							if (i.Value.ResultColumn)
								continue;

							// Build the sql
							if (index > 0)
								sb.Append(", ");
							sb.AppendFormat("{0} = {1}{2}", i.Key, _paramPrefix, index++);

							// Store the parameter in the command
							AddParam(cmd, i.Value.PropertyInfo.GetValue(poco, null), _paramPrefix);
						}

						cmd.CommandText = string.Format("UPDATE {0} SET {1} WHERE {2} = {3}{4}",
								tableName,
								sb.ToString(),
								primaryKeyName,
								_paramPrefix,
								index++
								);
						AddParam(cmd, primaryKeyValue, _paramPrefix);

						_lastSql = cmd.CommandText;
						_lastArgs = new object[] { primaryKeyValue };

						// Do it
						return cmd.ExecuteNonQuery();
					}
				}
			}
			catch (Exception x)
			{
				OnException(x);
				throw;
			}
		}

		public int Update(object poco)
		{
			return Update(poco, null);
		}

		// Update an annotated poco object
		public int Update(object poco, object primaryKeyValue)
		{
			var pd = PocoData.ForType(poco.GetType());
			return Update(pd.TableName, pd.PrimaryKey, poco, primaryKeyValue);
		}

		public int Update<T>(string sql, params object[] args)
		{
			var pd = PocoData.ForType(typeof(T));
			return Execute(string.Format("UPDATE {0} {1}", pd.TableName, sql), args);
		}

		public int Update<T>(Sql sql)
		{
			var pd = PocoData.ForType(typeof(T));
			return Execute(new Sql(string.Format("UPDATE {0}", pd.TableName)).Append(sql));
		}

		public int Delete(string tableName, string primaryKeyName, object poco)
		{
			return Delete(tableName, primaryKeyName, poco, null);
		}

		// Delete a record, using the primary key value from a poco, or supplied
		public int Delete(string tableName, string primaryKeyName, object poco, object primaryKeyValue)
		{
			// If primary key value not specified, pick it up from the object
			if (primaryKeyValue == null)
			{
				var pd = PocoData.ForType(poco.GetType());
				PocoColumn pc;
				if (pd.Columns.TryGetValue(primaryKeyName, out pc))
				{
					primaryKeyValue = pc.PropertyInfo.GetValue(poco, null);
				}
			}

			// Do it
			var sql = string.Format("DELETE FROM {0} WHERE {1}=@0", tableName, primaryKeyName);
			return Execute(sql, primaryKeyValue);
		}

		// Delete an annotated poco
		public int Delete(object poco)
		{
			var pd = PocoData.ForType(poco.GetType());
			return Delete(pd.TableName, pd.PrimaryKey, poco);
		}

		public int Delete<T>(string sql, params object[] args)
		{
			var pd = PocoData.ForType(typeof(T));
			return Execute(string.Format("DELETE FROM {0} {1}", pd.TableName, sql), args);
		}

		public int Delete<T>(Sql sql)
		{
			var pd = PocoData.ForType(typeof(T));
			return Execute(new Sql(string.Format("DELETE FROM {0}", pd.TableName)).Append(sql));
		}

		// Check if a poco represents a new record
		public bool IsNew(string primaryKeyName, object poco)
		{
			// If primary key value not specified, pick it up from the object
			var pd = PocoData.ForType(poco.GetType());
			PropertyInfo pi;
			PocoColumn pc;
			if (pd.Columns.TryGetValue(primaryKeyName, out pc))
			{
				pi = pc.PropertyInfo;
			}
			else
			{
				pi = poco.GetType().GetProperty(primaryKeyName);
				if (pi == null)
					throw new ArgumentException("The object doesn't have a property matching the primary key column name '{0}'", primaryKeyName);
			}

			// Get it's value
			var pk = pi.GetValue(poco, null);
			if (pk == null)
				return true;

			var type = pk.GetType();

			if (type.IsValueType)
			{
				// Common primary key types
				if (type == typeof(long))
					return (long)pk == 0;
				else if (type == typeof(ulong))
					return (ulong)pk == 0;
				else if (type == typeof(int))
					return (int)pk == 0;
				else if (type == typeof(uint))
					return (int)pk == 0;

				// Create a default instance and compare
				return pk == Activator.CreateInstance(pk.GetType());
			}
			else
			{
				return pk == null;
			}
		}

		// Same as above but for decorated pocos
		public bool IsNew(object poco)
		{
			var pd = PocoData.ForType(poco.GetType());
			return IsNew(pd.PrimaryKey, poco);
		}

		// Insert new record or Update existing record
		public void Save(string tableName, string primaryKeyName, object poco)
		{
			if (IsNew(primaryKeyName, poco))
			{
				Insert(tableName, primaryKeyName, poco);
			}
			else
			{
				Update(tableName, primaryKeyName, poco);
			}
		}

		// Same as above for decorated pocos
		public void Save(object poco)
		{
			var pd = PocoData.ForType(poco.GetType());
			Save(pd.TableName, pd.PrimaryKey, poco);
		}

		public string LastSQL { get { return _lastSql; } }
		public object[] LastArgs { get { return _lastArgs; } }

		public string LastCommand
		{
			get
			{
				var sb = new StringBuilder();
				if (_lastSql == null)
					return "";
				sb.Append(_lastSql);
				if (_lastArgs != null)
				{
					sb.Append("\r\n\r\n");
					for (int i = 0; i < _lastArgs.Length; i++)
					{
						sb.AppendFormat("{0} - {1}\r\n", i, _lastArgs[i].ToString());
					}
				}
				return sb.ToString();
			}
		}

	    public class PocoColumn
		{
			public string ColumnName;
			public PropertyInfo PropertyInfo;
			public bool ResultColumn;
		}

	    public class PocoData
		{
			static Dictionary<Type, PocoData> m_PocoData = new Dictionary<Type, PocoData>();
			public static PocoData ForType(Type t)
			{
				lock (m_PocoData)
				{
					PocoData pd;
					if (!m_PocoData.TryGetValue(t, out pd))
					{
						pd = new PocoData(t);
						m_PocoData.Add(t, pd);
					}
					return pd;
				}
			}

			public PocoData(Type t)
			{
				// Get the table name
				var a = t.GetCustomAttributes(typeof(TableName), true);
				TableName = a.Length == 0 ? t.Name : (a[0] as TableName).Value;

				// Get the primary key
				a = t.GetCustomAttributes(typeof(PrimaryKey), true);
				PrimaryKey = a.Length == 0 ? "ID" : (a[0] as PrimaryKey).Value;

				// Work out bound properties
				bool ExplicitColumns = t.GetCustomAttributes(typeof(ExplicitColumns), true).Length > 0;
				Columns = new Dictionary<string, PocoColumn>(StringComparer.OrdinalIgnoreCase);
				foreach (var pi in t.GetProperties())
				{
					// Work out if properties is to be included
					var ColAttrs = pi.GetCustomAttributes(typeof(Column), true);
					if (ExplicitColumns)
					{
						if (ColAttrs.Length == 0)
							continue;
					}
					else
					{
						if (pi.GetCustomAttributes(typeof(Ignore), true).Length != 0)
							continue;
					}

					// Work out the DB column name
					var pc = new PocoColumn();
					if (ColAttrs.Length > 0)
					{
						var colattr = (Column)ColAttrs[0];
						pc.ColumnName = colattr.Name;
						if ((colattr as ResultColumn)!=null)
							pc.ResultColumn=true;
					}
					if (pc.ColumnName == null)
						pc.ColumnName = pi.Name;
					pc.PropertyInfo = pi;

					// Store it
					Columns.Add(pc.ColumnName, pc);
				}

				// Build column list for automatic select
				QueryColumns = string.Join(", ", (from c in Columns where !c.Value.ResultColumn select c.Key).ToArray());
			}

			public string TableName { get; private set; }
			public string PrimaryKey { get; private set; }
			public string QueryColumns { get; private set; }
			public Dictionary<string, PocoColumn> Columns { get; private set; }
		}


		// ShareableConnection represents either a shared connection used by a transaction,
		// or a one-off connection if not in a transaction.
		// Non-shared connections are disposed 
		class ShareableConnection : IDisposable
		{
			public ShareableConnection(IDbConnection connection, bool shared)
			{
				_connection = connection;
				_shared = shared;
			}

			public IDbConnection Connection
			{
				get
				{
					return _connection;
				}
			}

			IDbConnection _connection;
			bool _shared;

			public void Dispose()
			{
				if (!_shared)
				{
					_connection.Dispose();
				}
			}
		}


		// Member variables
		string _connectionString;
		string _providerName;
		PocoFactory _factory;
		IDbConnection _sharedConnection;
		IDbTransaction _transaction;
		int _transactionDepth;
		string _lastSql;
		object[] _lastArgs;
	}

	// Transaction object helps maintain transaction depth counts
	public class Transaction : IDisposable
	{
		public Transaction(Database db)
		{
			_db = db;
			_db.BeginTransaction();
		}

		public void Complete()
		{
			_db.CompleteTransaction();
			_db = null;
		}

		public void Dispose()
		{
			if (_db != null)
				_db.AbortTransaction();
		}

		Database _db;
	}

	// Simple helper class for building SQL statments
	// eg:
	//   new Sql()
	//		.Select("id", "title")
	//		.From("articles")
	//		.Where("date_created>@0", DateTime.UtcNow)
	//		.OrderBy("title")
	public class Sql
	{
		public Sql()
		{
		}

		public Sql(string sql, params object[] args)
		{
			_sql = sql;
			_args = args;
		}

		string _sql;
		object[] _args;
		Sql _rhs;
		string _sqlFinal;
		object[] _argsFinal;

		void Build()
		{
			// already built?
			if (_sqlFinal != null)
				return;

			// Build it
			var sb = new StringBuilder();
			var args = new List<object>();
			Build(sb, args);
			_sqlFinal = sb.ToString();
			_argsFinal = args.ToArray();
		}

        private string _paramterPrefix = "@";
        public string ParameterPrefix { get { return _paramterPrefix; } set { _paramterPrefix = value; } }

		public string SQL
		{
			get
			{
				Build();
				return _sqlFinal;
			}
		}

		public object[] Arguments
		{
			get
			{
				Build();
				return _argsFinal;
			}
		}

		public Sql Append(Sql sql)
		{
			if (_rhs != null)
				_rhs.Append(sql);
			else
				_rhs = sql;

			return this;
		}

		public Sql Append(string sql, params object[] args)
		{
			return Append(new Sql(sql, args));
		}

		public Sql Where(string sql, params object[] args)
		{
			return Append(new Sql("WHERE " + sql, args));
		}

		public Sql OrderBy(params object[] args)
		{
			return Append(new Sql("ORDER BY " + String.Join(", ", (from x in args select x.ToString()).ToArray())));
		}

		public Sql Select(params object[] args)
		{
			return Append(new Sql("SELECT " + String.Join(", ", (from x in args select x.ToString()).ToArray())));
		}

		public Sql From(params object[] args)
		{
			return Append(new Sql("FROM " + String.Join(", ", (from x in args select x.ToString()).ToArray())));
		}

		public void Build(StringBuilder sb, List<object> args)
		{
			if (!String.IsNullOrEmpty(_sql))
			{
				// Add SQL to the string
				if (sb.Length > 0)
				{
					sb.Append("\n");
				}

				var rxParams = new Regex(@"(?<!@)@\w+");
				var sql = rxParams.Replace(_sql, m =>
				{
					string param = m.Value.Substring(1);

					int paramIndex;
					if (int.TryParse(param, out paramIndex))
					{
						// Numbered parameter
						if (paramIndex < 0 || paramIndex >= _args.Length)
						{
							throw new ArgumentOutOfRangeException(string.Format("Parameter '@{0}' specified but only {1} parameters supplied (in `{2}`)", paramIndex, _args.Length, _sql));
						}

						args.Add(_args[paramIndex]);

					}
					else
					{
						// Look for a property on one of the arguments with this name
						bool found = false;
						foreach (var o in _args)
						{
							var pi = o.GetType().GetProperty(param);
							if (pi != null)
							{
								args.Add(pi.GetValue(o, null));
								found = true;
								break;
							}
						}

						// Check found
						if (!found)
						{
							throw new ArgumentException(string.Format("Parameter '@{0}' specified but none of the passed arguments have a property with this name (in '{1}')", param, _sql));
						}
					}
                    return _paramterPrefix + (args.Count - 1).ToString();
				}
				);

				sb.Append(sql);
			}

			// Now do rhs
			if (_rhs != null)
				_rhs.Build(sb, args);
		}
	}

    public class ReflectHelper
    {
        public static Type TypeFromAssembly(string typeName, string assemblyName)
        {
            try
            {
                // Try to get the type from an already loaded assembly
                Type type = Type.GetType(typeName);

                if (type != null)
                {
                    return type;
                }

                if (assemblyName == null)
                {
                    // No assembly was specified for the type, so just fail
                    string message = "Could not load type " + typeName + ". Possible cause: no assembly name specified.";
                    throw new TypeLoadException(message);
                }

                Assembly assembly = Assembly.Load(assemblyName);

                if (assembly == null)
                {
                    throw new InvalidOperationException("Can't find assembly: " + assemblyName);
                }

                type = assembly.GetType(typeName);

                if (type == null)
                {
                    return null;
                }

                return type;
            }
            catch (Exception)
            {
                return null;
            }
        }

    }
}
