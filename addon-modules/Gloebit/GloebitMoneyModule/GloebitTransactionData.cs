/*
 * Copyright (c) 2015 Gloebit LLC
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlTypes;
using System.Reflection;
using System.Xml;
using log4net;
using MySql.Data.MySqlClient;
using Nini.Config;
using OpenSim.Data.MySQL;
using OpenSim.Data.PGSQL;
using OpenSim.Data.SQLite;


namespace Gloebit.GloebitMoneyModule
{
    class GloebitTransactionData {

        private static IGloebitTransactionData m_impl;

        public static void Initialise(string storageProvider, string connectionString) {
            switch(storageProvider) {
                case "OpenSim.Data.SQLite.dll":
                    m_impl = new SQLiteImpl(connectionString);
                    break;
                case "OpenSim.Data.MySQL.dll":
                    m_impl = new MySQLImpl(connectionString);
                    break;
                case "OpenSim.Data.PGSQL.dll":
                    m_impl = new PGSQLImpl(connectionString);
                    break;
                default:
                    break;
            }
        }

        public static IGloebitTransactionData Instance {
            get { return m_impl; }
        }

        public interface IGloebitTransactionData {
            GloebitTransaction[] Get(string field, string key);

            GloebitTransaction[] Get(string[] fields, string[] keys);

            bool Store(GloebitTransaction txn);
        }

        private class SQLiteImpl : SQLiteGenericTableHandler<GloebitTransaction>, IGloebitTransactionData {
            public SQLiteImpl(string connectionString)
                : base(connectionString, "GloebitTransactions", "GloebitTransactionsSQLite")
            {
            }
            
            public override bool Store(GloebitTransaction txn)
            {
                // remove null datetimes as pgsql throws exceptions on null fields
                if (txn.enactedTime == null) {
                    txn.enactedTime = SqlDateTime.MinValue.Value;
                }
                if (txn.finishedTime == null) {
                    txn.finishedTime = SqlDateTime.MinValue.Value;
                }
                // call parent
                return base.Store(txn);
            }
        }

        private class MySQLImpl : MySQLGenericTableHandler<GloebitTransaction>, IGloebitTransactionData {
            public MySQLImpl(string connectionString)
                : base(connectionString, "GloebitTransactions", "GloebitTransactionsMySQL")
            {
            }
            
            public override bool Store(GloebitTransaction txn)
            {
                //            m_log.DebugFormat("[MYSQL GENERIC TABLE HANDLER]: Store(T row) invoked");
                
                using (MySqlCommand cmd = new MySqlCommand())
                {
                    string query = "";
                    List<String> names = new List<String>();
                    List<String> values = new List<String>();
                    
                    foreach (FieldInfo fi in m_Fields.Values)
                    {
                        names.Add(fi.Name);
                        values.Add("?" + fi.Name);
                        
                        // Temporarily return more information about what field is unexpectedly null for
                        // http://opensimulator.org/mantis/view.php?id=5403.  This might be due to a bug in the
                        // InventoryTransferModule or we may be required to substitute a DBNull here.
                        /*if (fi.GetValue(asset) == null)
                            throw new NullReferenceException(
                                                             string.Format(
                                                                           "[MYSQL GENERIC TABLE HANDLER]: Trying to store field {0} for {1} which is unexpectedly null",
                                                                           fi.Name, asset));*/
                        
                        cmd.Parameters.AddWithValue(fi.Name, fi.GetValue(txn));
                    }
                    
                    /*if (m_DataField != null)
                    {
                        Dictionary<string, string> data =
                        (Dictionary<string, string>)m_DataField.GetValue(row);
                        
                        foreach (KeyValuePair<string, string> kvp in data)
                        {
                            names.Add(kvp.Key);
                            values.Add("?" + kvp.Key);
                            cmd.Parameters.AddWithValue("?" + kvp.Key, kvp.Value);
                        }
                    }*/
                    
                    query = String.Format("replace into {0} (`", m_Realm) + String.Join("`,`", names.ToArray()) + "`) values (" + String.Join(",", values.ToArray()) + ")";
                    
                    cmd.CommandText = query;
                    
                    if (ExecuteNonQuery(cmd) > 0)
                        return true;
                    
                    return false;
                }
            }
        }

        private class PGSQLImpl : PGSQLGenericTableHandler<GloebitTransaction>, IGloebitTransactionData {
            private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

            public PGSQLImpl(string connectionString)
                : base(connectionString, "GloebitTransactions", "GloebitTransactionsPGSQL")
            {
            }
            
            public override bool Store(GloebitTransaction txn)
            {
		try {
                    // remove null datetimes as pgsql throws exceptions on null fields
                    if (txn.enactedTime == null) {
                        txn.enactedTime = SqlDateTime.MinValue.Value;
                    }
                    if (txn.finishedTime == null) {
                        txn.finishedTime = SqlDateTime.MinValue.Value;
                    }
                    //m_log.InfoFormat("GloebitTransactionData.PGSQLImpl: storing transaction type:{0}, SaleType:{2}, PayerEndingBalance:{3}, cTime:{4}, enactedTime:{5}, finishedTime:{6}", txn.TransactionType, txn.SaleType, txn.PayerEndingBalance, txn.cTime, txn.enactedTime, txn.finishedTime);
                    // call parent
                    return base.Store(txn);
		} catch(System.OverflowException e) {
                    m_log.ErrorFormat("GloebitTransactionData.PGSQLImpl: Failure storing transaction type:{0}, SaleType:{1}, PayerEndingBalance:{2}, cTime:{3}, enactedTime:{4}, finishedTime:{5}", txn.TransactionType, txn.SaleType, txn.PayerEndingBalance, txn.cTime, txn.enactedTime, txn.finishedTime);
		    throw;
		}
            }
        }
        
    }
}
