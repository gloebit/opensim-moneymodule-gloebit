/*
 * Copyright (c) 2015 Gloebit LLC
 *
 * Licensed under the EUPL version 1.2 
 * or any later version approved by Gloebit via a public statement of acceptance
 */

using System;
using Nini.Config;
using OpenSim.Data.MySQL;
using OpenSim.Data.PGSQL;
using OpenSim.Data.SQLite;

namespace Gloebit.GloebitMoneyModule
{
    class GloebitUserData {

        private static IGloebitUserData m_impl;

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

        public static IGloebitUserData Instance {
            get { return m_impl; }
        }

        public interface IGloebitUserData {
            GloebitUser[] Get(string field, string key);

            GloebitUser[] Get(string[] fields, string[] keys);

            bool Store(GloebitUser user);
        }

        private class SQLiteImpl : SQLiteGenericTableHandler<GloebitUser>, IGloebitUserData {
            public SQLiteImpl(string connectionString)
                : base(connectionString, "GloebitUsers", "GloebitUsersSQLite")
            {
            }
        }

        private class MySQLImpl : MySQLGenericTableHandler<GloebitUser>, IGloebitUserData {
            public MySQLImpl(string connectionString)
                : base(connectionString, "GloebitUsers", "GloebitUsersMySQL")
            {
            }
        }

        private class PGSQLImpl : PGSQLGenericTableHandler<GloebitUser>, IGloebitUserData {
            public PGSQLImpl(string connectionString)
                : base(connectionString, "GloebitUsers", "GloebitUsersPGSQL")
            {
            }
        }
    }
}
