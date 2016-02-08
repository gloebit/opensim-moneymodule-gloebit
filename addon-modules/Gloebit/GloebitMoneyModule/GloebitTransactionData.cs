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
using System.Reflection;
using System.Text;
using MySql.Data.MySqlClient;
using Npgsql;
using Nini.Config;
using OpenSim.Data.MySQL;
using OpenSim.Data.PGSQL;
using OpenSim.Data.SQLite;


namespace Gloebit.GloebitMoneyModule
{
    class GloebitTransactionData {

        private static IGloebitTransactionData m_impl;

        public static void Initialise(IConfig config) {
            switch(config.GetString("StorageProvider")) {
                case "OpenSim.Data.SQLite.dll":
                    m_impl = new SQLiteImpl(config);
                    break;
                case "OpenSim.Data.MySQL.dll":
                    m_impl = new MySQLImpl(config);
                    break;
                case "OpenSim.Data.PGSQL.dll":
                    m_impl = new PGSQLImpl(config);
                    break;
                default:
                    break;
            }
        }

        public static IGloebitTransactionData Instance {
            get { return m_impl; }
        }

        public interface IGloebitTransactionData {
            GloebitAPI.Transaction[] Get(string field, string key);

            GloebitAPI.Transaction[] Get(string[] fields, string[] keys);

            bool Store(GloebitAPI.Transaction txn);
        }

        private class SQLiteImpl : SQLiteGenericTableHandler<GloebitAPI.Transaction>, IGloebitTransactionData {
            public SQLiteImpl(IConfig config)
                : base(config.GetString("ConnectionString"), "GloebitTransactions", "GloebitTransactionsSQLite")
            {
                /// TODO: Likely need to override Store() function to handle bools, DateTimes and nulls.
                /// Start with SQLiteGenericTableHandler impl and see MySql override below
            }
        }

        private class MySQLImpl : MySQLGenericTableHandler<GloebitAPI.Transaction>, IGloebitTransactionData {
            public MySQLImpl(IConfig config)
                : base(config.GetString("ConnectionString"), "GloebitTransactions", "GloebitTransactionsMySQL")
            {
            }
            
            public override bool Store(GloebitAPI.Transaction txn)
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

        private class PGSQLImpl : PGSQLGenericTableHandler<GloebitAPI.Transaction>, IGloebitTransactionData {
            public PGSQLImpl(IConfig config)
                : base(config.GetString("ConnectionString"), "GloebitTransactions", "GloebitTransactionsPGSQL")
            {
            }
            
            public override bool Store(GloebitAPI.Transaction txn)
            {
                List<string> constraintFields = GetConstraints();
                List<KeyValuePair<string, string>> constraints = new List<KeyValuePair<string, string>>();
                
                using (NpgsqlConnection conn = new NpgsqlConnection(m_ConnectionString))
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    
                    StringBuilder query = new StringBuilder();
                    List<String> names = new List<String>();
                    List<String> values = new List<String>();
                    
                    foreach (FieldInfo fi in m_Fields.Values)
                    {
                        names.Add(fi.Name);
                        values.Add(":" + fi.Name);
                        // Temporarily return more information about what field is unexpectedly null for
                        // http://opensimulator.org/mantis/view.php?id=5403.  This might be due to a bug in the
                        // InventoryTransferModule or we may be required to substitute a DBNull here.
                        /*if (fi.GetValue(row) == null)
                            throw new NullReferenceException(
                                    string.Format(
                                        "[PGSQL GENERIC TABLE HANDLER]: Trying to store field {0} for {1} which is unexpectedly null",
                                        fi.Name, row));*/
                        
                        if (constraintFields.Count > 0 && constraintFields.Contains(fi.Name))
                        {
                            constraints.Add(new KeyValuePair<string, string>(fi.Name, fi.GetValue(txn).ToString() ));
                        }
                        if (m_FieldTypes.ContainsKey(fi.Name)) {
                            cmd.Parameters.Add(m_database.CreateParameter(fi.Name, fi.GetValue(txn), m_FieldTypes[fi.Name]));
                        } else {
                            cmd.Parameters.Add(m_database.CreateParameter(fi.Name, fi.GetValue(txn)));
                        }
                    }
                    
                    /*if (m_DataField != null)
                    {
                        Dictionary<string, string> data =
                        (Dictionary<string, string>)m_DataField.GetValue(row);
                        
                        foreach (KeyValuePair<string, string> kvp in data)
                        {
                            if (constraintFields.Count > 0 && constraintFields.Contains(kvp.Key))
                            {
                                constraints.Add(new KeyValuePair<string, string>(kvp.Key, kvp.Key));
                            }
                            names.Add(kvp.Key);
                            values.Add(":" + kvp.Key);
                            
                            if (m_FieldTypes.ContainsKey(kvp.Key))
                            cmd.Parameters.Add(m_database.CreateParameter("" + kvp.Key, kvp.Value, m_FieldTypes[kvp.Key]));
                            else
                            cmd.Parameters.Add(m_database.CreateParameter("" + kvp.Key, kvp.Value));
                        }
                        
                    }*/
                    
                    query.AppendFormat("UPDATE {0} SET ", m_Realm);
                    int i = 0;
                    for (i = 0; i < names.Count - 1; i++)
                    {
                        query.AppendFormat("\"{0}\" = {1}, ", names[i], values[i]);
                    }
                    query.AppendFormat("\"{0}\" = {1} ", names[i], values[i]);
                    if (constraints.Count > 0)
                    {
                        List<string> terms = new List<string>();
                        for (int j = 0; j < constraints.Count; j++)
                        {
                            terms.Add(String.Format(" \"{0}\" = :{0}", constraints[j].Key));
                        }
                        string where = String.Join(" AND ", terms.ToArray());
                        query.AppendFormat(" WHERE {0} ", where);
                        
                    }
                    cmd.Connection = conn;
                    cmd.CommandText = query.ToString();
                    
                    conn.Open();
                    if (cmd.ExecuteNonQuery() > 0)
                    {
                        //m_log.WarnFormat("[PGSQLGenericTable]: Updating {0}", m_Realm);
                        return true;
                    }
                    else
                    {
                        // assume record has not yet been inserted
                        
                        query = new StringBuilder();
                        query.AppendFormat("INSERT INTO {0} (\"", m_Realm);
                        query.Append(String.Join("\",\"", names.ToArray()));
                        query.Append("\") values (" + String.Join(",", values.ToArray()) + ")");
                        cmd.Connection = conn;
                        cmd.CommandText = query.ToString();
                        
                        // m_log.WarnFormat("[PGSQLGenericTable]: Inserting into {0} sql {1}", m_Realm, cmd.CommandText);
                        
                        if (conn.State != ConnectionState.Open) {
                            conn.Open();
                        }
                        if (cmd.ExecuteNonQuery() > 0) {
                            return true;
                        }
                    }
                    
                    return false;
                }
            }
            
        }
    }
}
