/*
 * GloebitCallbackSignature.cs is part of OpenSim-MoneyModule-Gloebit
 * Copyright (C) 2026 Gloebit LLC
 *
 * OpenSim-MoneyModule-Gloebit is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * OpenSim-MoneyModule-Gloebit is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with OpenSim-MoneyModule-Gloebit.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Security.Cryptography;
using System.Text;

namespace Gloebit.GloebitMoneyModule
{
    /// <summary>
    /// Signs the enact/consume/cancel callback urls this module gives Gloebit, so the
    /// /gloebit/transaction handler only acts on urls this module built.
    /// The signature is an HMAC-SHA256 of the transaction id and requested state, keyed with the app's secret,
    /// which only this module and Gloebit know.  Gloebit calls the urls exactly as given, so it needs no change.
    /// </summary>
    public static class GloebitCallbackSignature
    {
        /// <summary>Name of the query argument carrying the signature.</summary>
        public const string QueryArg = "sig";

        /// <summary>
        /// Returns the hex signature for the callback requesting state for transactionID.
        /// </summary>
        public static string Compute(string secret, string transactionID, string state)
        {
            string message = String.Format("gloebit-hold-callback\n{0}\n{1}", transactionID.ToLowerInvariant(), state);
            byte[] hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret ?? String.Empty), Encoding.UTF8.GetBytes(message));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Reports whether signature is the one this module would have put on the callback requesting state for transactionID.
        /// </summary>
        public static bool IsValid(string secret, string transactionID, string state, string signature)
        {
            if (String.IsNullOrEmpty(secret) || String.IsNullOrEmpty(transactionID) || String.IsNullOrEmpty(state) || String.IsNullOrEmpty(signature)) {
                return false;
            }
            byte[] expected = Encoding.ASCII.GetBytes(Compute(secret, transactionID, state));
            byte[] actual = Encoding.ASCII.GetBytes(signature.ToLowerInvariant());
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
    }
}
