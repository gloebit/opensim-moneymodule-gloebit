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
