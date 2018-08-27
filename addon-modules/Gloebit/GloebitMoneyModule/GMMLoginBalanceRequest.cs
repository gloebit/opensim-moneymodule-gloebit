/*
 * Copyright (c) 2015 Gloebit LLC
 *
 * Licensed under the EUPL version 1.2 
 * or any later version approved by Gloebit via a public statement of acceptance
 */

/*
 * GMMLoginBalanceRequest.cs
 * Helper class for GloebitMoneyModule to prevent sending messages
 * twice to a user who just logged in because the balance is requested
 * twice.
 */


using System;
using System.Collections;
using System.Collections.Generic;
using OpenMetaverse;


namespace Gloebit.GloebitMoneyModule {

    /*********************************************************
     ********** LOGIN BALANCE REQUEST helper class ***********
     *********************************************************/

    /// <summary>
    /// Class which is a hack to deal with the fact that a balance request is made
    /// twice when a user logs into a GMM enabled region (once for connect to region and once by viewer after login).
    /// This causes the balance to be reqeusted twice, and if not authed, the user to be asked to auth twice.
    /// This class is designed solely for preventing the second request in that single case.
    /// </summary>
    public class LoginBalanceRequest
    {
        // Create a static map of agent IDs to LRBHs
        private static Dictionary<UUID, LoginBalanceRequest> s_LoginBalanceRequestMap = new Dictionary<UUID, LoginBalanceRequest>();
        private bool m_IgnoreNextBalanceRequest = false;
        private DateTime m_IgnoreTime = DateTime.UtcNow;
        private static int numSeconds = -10;

        private LoginBalanceRequest()
        {
            this.m_IgnoreNextBalanceRequest = false;
            this.m_IgnoreTime = DateTime.UtcNow;
        }

        public bool IgnoreNextBalanceRequest
        {
            get {
                if (m_IgnoreNextBalanceRequest && justLoggedIn()) {
                    m_IgnoreNextBalanceRequest = false;
                    return true;
                }
                return false;
            }
            set {
                if (value) {
                    m_IgnoreNextBalanceRequest = true;
                    m_IgnoreTime = DateTime.UtcNow;
                } else {
                    m_IgnoreNextBalanceRequest = false;
                }
            }
        }

        public static LoginBalanceRequest Get(UUID agentID) {
            LoginBalanceRequest lbr;
            lock (s_LoginBalanceRequestMap) {
                s_LoginBalanceRequestMap.TryGetValue(agentID, out lbr);
                if (lbr == null) {
                    lbr = new LoginBalanceRequest();
                    s_LoginBalanceRequestMap[agentID] = lbr;
                }
            }
            return lbr;
        }

        public static bool ExistsAndJustLoggedIn(UUID agentID) {
            // If an lbr exists and is recent.
            bool exists;
            LoginBalanceRequest lbr;
            lock (s_LoginBalanceRequestMap) {
                exists = s_LoginBalanceRequestMap.TryGetValue(agentID, out lbr);
            }
            return exists && lbr.justLoggedIn();
        }

        private bool justLoggedIn() {
            return (m_IgnoreTime.CompareTo(DateTime.UtcNow.AddSeconds(numSeconds)) > 0);
        }

        public static void Cleanup(UUID agentID) {
            lock (s_LoginBalanceRequestMap) {
                s_LoginBalanceRequestMap.Remove(agentID);
            }
        }
    }
}
