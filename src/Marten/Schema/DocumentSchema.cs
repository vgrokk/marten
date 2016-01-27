using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Baseline;
using Marten.Events;
using Marten.Generation;
using Marten.Schema.Sequences;
using Marten.Services;

namespace Marten.Schema
{
    public class DocumentSchema : IDocumentSchema, IDisposable
    {
        private readonly IDocumentSchemaCreation _creation;

        private readonly ConcurrentDictionary<Type, DocumentMapping> _documentMappings =
            new ConcurrentDictionary<Type, DocumentMapping>();

        private readonly ConcurrentDictionary<Type, IDocumentStorage> _documentTypes =
            new ConcurrentDictionary<Type, IDocumentStorage>();

        private readonly ICommandRunner _runner;

        public DocumentSchema(ICommandRunner runner, IDocumentSchemaCreation creation)
        {
            _creation = creation;
            _runner = runner;

            Sequences = new SequenceFactory(this, _runner, _creation);

            Events = new EventGraph();
        }

        public StoreOptions StoreOptions { get; set; } = new StoreOptions();


        public void Dispose()
        {
        }

        public DocumentMapping MappingFor(Type documentType)
        {
            return _documentMappings.GetOrAdd(documentType, type => new DocumentMapping(type, StoreOptions));
        }

        public void EnsureStorageExists(Type documentType)
        {
            StorageFor(documentType);
        }

        public IDocumentStorage StorageFor(Type documentType)
        {
            return _documentTypes.GetOrAdd(documentType, type =>
            {
                var mapping = MappingFor(documentType);
                var storage = DocumentStorageBuilder.Build(this, mapping);

                if (shouldRegenerate(mapping))
                {
                    _creation.CreateSchema(this, mapping);
                }

                return storage;
            });
        }

        private bool shouldRegenerate(DocumentMapping mapping)
        {
            if (!DocumentTables().Contains(mapping.TableName)) return true;

            var existing = TableSchema(mapping.TableName);
            var expected = mapping.ToTable(this);

            return !expected.Equals(existing);
        }

        public EventGraph Events { get; }

        public IEnumerable<string> SchemaTableNames()
        {
            var sql =
                "select table_name from information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema') ";

            return _runner.GetStringList(sql);
        }

        public string[] DocumentTables()
        {
            return SchemaTableNames().Where(x => x.StartsWith("mt_doc_")).ToArray();
        }

        public IEnumerable<string> SchemaFunctionNames()
        {
            return findFunctionNames().ToArray();
        }

        public void Alter(Action<MartenRegistry> configure)
        {
            var registry = new MartenRegistry();
            configure(registry);

            Alter(registry);
        }

        public void Alter<T>() where T : MartenRegistry, new()
        {
            Alter(new T());
        }

        public void Alter(MartenRegistry registry)
        {
            // TODO -- later, latch on MartenRegistry type? May not really matter
            registry.Alter(this);
        }

        public ISequences Sequences { get; }

        public PostgresUpsertType UpsertType { get; set; } = PostgresUpsertType.Legacy;

        public void WriteDDL(string filename)
        {
            var sql = ToDDL();
            new FileSystem().WriteStringToFile(filename, sql);
        }

        public string ToDDL()
        {
            var writer = new StringWriter();

            _documentMappings.Values.Each(x => SchemaBuilder.WriteSchemaObjects(x, this, writer));

            writer.WriteLine(SchemaBuilder.GetText("mt_hilo"));

            return writer.ToString();
        }

        public TableDefinition TableSchema(string tableName)
        {
            if (!DocumentTables().Contains(tableName.ToLower()))
                throw new Exception($"No Marten table exists named '{tableName}'");


            var columns = findTableColumns(tableName);
            var pkName = primaryKeysFor(tableName).SingleOrDefault();

            return new TableDefinition(tableName, pkName, columns);
        }

        private string[] primaryKeysFor(string tableName)
        {
            var sql = @"
SELECT a.attname, format_type(a.atttypid, a.atttypmod) AS data_type
FROM pg_index i
JOIN   pg_attribute a ON a.attrelid = i.indrelid
                     AND a.attnum = ANY(i.indkey)
WHERE i.indrelid = ?::regclass
AND i.indisprimary; 
";

            return _runner.GetStringList(sql, tableName).ToArray();
        }

        private IEnumerable<TableColumn> findTableColumns(string tableName)
        {
            Func<IDataReader, TableColumn> transform = r => new TableColumn(r.GetString(0), r.GetString(1));

            var sql = "select column_name, data_type from information_schema.columns where table_name = ? order by ordinal_position";
            return
                _runner.Fetch(
                    sql,
                    transform, tableName);
        }


        private IEnumerable<string> findFunctionNames()
        {
            return _runner.Execute(conn =>
            {
                var sql = @"
SELECT routine_name
FROM information_schema.routines
WHERE specific_schema NOT IN ('pg_catalog', 'information_schema')
AND type_udt_name != 'trigger';
";

                var command = conn.CreateCommand();
                command.CommandText = sql;

                var list = new List<string>();
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        list.Add(reader.GetString(0));
                    }

                    reader.Close();
                }

                return list;
            });
        }
    }
}