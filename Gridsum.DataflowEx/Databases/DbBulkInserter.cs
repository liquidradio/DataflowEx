﻿using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace Gridsum.DataflowEx.Databases
{
    using System;

    /// <summary>
    /// The class helps you to bulk insert parsed objects to the database. 
    /// </summary>
    /// <typeparam name="T">The db-mapped type of parsed objects (usually generated by EF/linq2sql)</typeparam>
    public class DbBulkInserter<T> : Dataflow<T> where T:class
    {
        private readonly int m_bulkSize;
        private readonly string m_dbBulkInserterName;
        private readonly PostBulkInsertDelegate m_postBulkInsert;
        private readonly BatchBlock<T> m_batchBlock;
        private readonly ActionBlock<T[]> m_actionBlock;

        public DbBulkInserter(string connectionString, string destTable, DataflowOptions options, string destLabel, 
            int bulkSize = 4096 * 2, 
            string dbBulkInserterName = null,
            PostBulkInsertDelegate postBulkInsert = null) 
            : base(options)
        {
            m_bulkSize = bulkSize;
            m_dbBulkInserterName = dbBulkInserterName;
            m_postBulkInsert = postBulkInsert;
            m_batchBlock = new BatchBlock<T>(bulkSize);
            m_actionBlock = new ActionBlock<T[]>(async array =>
            {
                LogHelper.Logger.Debug(h => h("{3} starts bulk-inserting {0} {1} to db table {2}", array.Length, typeof(T).Name, destTable, this.Name));
                await DumpToDB(array, destTable, connectionString, destLabel);
                LogHelper.Logger.Info(h => h("{3} bulk-inserted {0} {1} to db table {2}", array.Length, typeof(T).Name, destTable, this.Name));
            });
            m_batchBlock.LinkTo(m_actionBlock, m_defaultLinkOption);

            RegisterChild(m_batchBlock);
            RegisterChild(m_actionBlock);
        }

        private async Task DumpToDB(IEnumerable<T> data, string destTable, string connectionString, string destLabel)
        {
            using (var bulkReader = new BulkDataReader<T>(TypeAccessorManager<T>.GetAccessorByDestLabel(destLabel, connectionString, destTable), data))
            {
                using (var conn = new SqlConnection(connectionString))
                {
                    await conn.OpenAsync();

                    using (var bulkCopy = new SqlBulkCopy(conn))
                    {
                        foreach (SqlBulkCopyColumnMapping map in bulkReader.ColumnMappings)
                        {
                            bulkCopy.ColumnMappings.Add(map);
                        }

                        bulkCopy.DestinationTableName = destTable;
                        bulkCopy.BulkCopyTimeout = 0;
                        bulkCopy.BatchSize = m_bulkSize;

                        // Write from the source to the destination.
                        await bulkCopy.WriteToServerAsync(bulkReader);
                    }

                    await this.OnPostBulkInsert(conn, destTable, destLabel);
                }
            }
        }

        public override ITargetBlock<T> InputBlock { get { return m_batchBlock; } }
        
        public override string Name
        {
            get {
                return m_dbBulkInserterName ?? base.Name;
            }
        }

        public override Tuple<int, int> BufferStatus
        {
            get
            {
                var bs = base.BufferStatus;
                return new Tuple<int, int>(bs.Item1 * m_bulkSize, bs.Item2 * m_bulkSize);
            }
        }

        protected virtual async Task OnPostBulkInsert(SqlConnection sqlConnection, string destTable, string destLabel)
        {
            if (m_postBulkInsert != null)
            {
                await m_postBulkInsert(sqlConnection, destTable, destLabel);
            }
        }
    }

    /// <summary>
    /// The handler which allows you to take control after a bulk insertion succeeds. (e.g. you may want to 
    /// execute a stored prodecure after every bulk insertion)
    /// </summary>
    /// <param name="connection">The connection used by previous bulk insert (already opened)</param>
    /// <param name="destTable">The destination table name of the bulk insertion</param>
    /// <param name="destLabel">The destination label tagged on inserted entities</param>
    /// <returns>A task represents the state of the post bulk insert job (so you can use await in the delegate)</returns>
    public delegate Task PostBulkInsertDelegate(SqlConnection connection, string destTable, string destLabel);
}