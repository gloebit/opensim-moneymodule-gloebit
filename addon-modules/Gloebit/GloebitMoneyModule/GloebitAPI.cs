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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Web;
using log4net;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

using OpenSim.Framework;

namespace Gloebit.GloebitMoneyModule {

    public class GloebitAPI {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public readonly string m_key;
        private string m_keyAlias;
        private string m_secret;
        public readonly Uri m_url;
        
        public interface IAsyncEndpointCallback {
            void exchangeAccessTokenCompleted(bool success, User user, OSDMap responseDataMap);
            void transactU2UCompleted (OSDMap responseDataMap, User sender, User recipient, Transaction transaction, TransactionStage stage, TransactionFailure failure);
            void createSubscriptionCompleted(OSDMap responseDataMap, Subscription subscription);
            void createSubscriptionAuthorizationCompleted(OSDMap responseDataMap, Subscription subscription, User sender, IClientAPI client);
        }
        
        public static IAsyncEndpointCallback m_asyncEndpointCallbacks;

        public interface IAssetCallback {
            bool processAssetEnactHold(Transaction txn, out string returnMsg);
            bool processAssetConsumeHold(Transaction txn, out string returnMsg);
            bool processAssetCancelHold(Transaction txn, out string returnMsg);
        }
        
        public static IAssetCallback m_assetCallbacks;

        public class User {
            public string PrincipalID;
            public string GloebitID;
            public string GloebitToken;

            // TODO - update tokenMap to be a proper LRU Cache and hold User objects
            private static Dictionary<string, User> s_userMap = new Dictionary<string, User>();
            
            private object userLock = new object();

            public User() {
            }

            private User(string principalID, string gloebitID, string token) {
                this.PrincipalID = principalID;
                this.GloebitID = gloebitID;
                this.GloebitToken = token;
            }
            
            private User(User copyFrom) {
                this.PrincipalID = copyFrom.PrincipalID;
                this.GloebitID = copyFrom.GloebitID;
                this.GloebitToken = copyFrom.GloebitToken;
            }

            // TODO: should probably return User class to using strings instead of UUIDs to make it more generic.
            public static User Get(UUID agentID) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] in User.Get");
                string agentIdStr = agentID.ToString();
                
                User u;
                lock(s_userMap) {
                    s_userMap.TryGetValue(agentIdStr, out u);
                }
                
                if (u == null) {
                    m_log.InfoFormat("[GLOEBITMONEYMODULE] Looking for prior user for {0}", agentIdStr);
                    User[] users = GloebitUserData.Instance.Get("PrincipalID", agentIdStr);

                    switch(users.Length) {
                        case 1:
                            u = users[0];
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] FOUND USER TOKEN! {0} valid token? {1}", u.PrincipalID, !String.IsNullOrEmpty(u.GloebitToken));
                            break;
                        case 0:
                            u = new User(agentIdStr, String.Empty, null);
                            break;
                        default:
                           throw new Exception(String.Format("[GLOEBITMONEYMODULE] Failed to find exactly one prior token for {0}", agentIdStr));
                    }
                    // TODO - use the Gloebit identity service for userId
                    
                    // Store in map and return User
                    lock(s_userMap) {
                        // Make sure no one else has already loaded this user
                        User alreadyLoadedUser;
                        s_userMap.TryGetValue(agentIdStr, out alreadyLoadedUser);
                        if (alreadyLoadedUser == null) {
                            s_userMap[agentIdStr] = u;
                        } else {
                            u = alreadyLoadedUser;
                        }
                    }
                }

                // Create a thread local copy of the user to return.
                User localUser;
                lock (u.userLock) {
                    localUser = new User(u);
                }
                
                return localUser;
            }

            public static User Authorize(UUID agentId, string token, string gloebitID) {
                string agentIdStr = agentId.ToString();
                
                // TODO: I think there has to be a better way to do this, but I'm not finding it right now.
                // By calling Get, we make sure that the user is in the map and has any additional data users store.
                User localUser = User.Get(agentId);
                User u;
                lock (s_userMap) {
                    s_userMap.TryGetValue(agentIdStr, out u);
                }
                if (u == null) {
                    u = localUser;  // User logged out.  Still want to store token.  Don't want to add back to map.
                }
                lock (u.userLock) {
                    u.GloebitToken = token;
                    u.GloebitID = gloebitID;
                    GloebitUserData.Instance.Store(u);
                    localUser = new User(u);
                }
                
                return localUser;
            }

            public void InvalidateToken() {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.User.InvalidateToken() {0}, valid token? {1}", PrincipalID, !String.IsNullOrEmpty(GloebitToken));
                
                if(!String.IsNullOrEmpty(GloebitToken)) {
                    User u;
                    lock (s_userMap) {
                        s_userMap.TryGetValue(PrincipalID, out u);
                    }
                    if (u == null) {
                        u = this;   // User logged out.  Still want to invalidate token.  Don't want to add back to map.
                    }
                    lock (u.userLock) {
                        if (GloebitToken != u.GloebitToken) {
                            // Someone else invalidated it already or authorized it.
                            // Don't overwrite
                            // TODO: should we set this equal to a copy of u before we return?
                            return;
                        } else {
                            u.GloebitToken = String.Empty;
                            GloebitUserData.Instance.Store(u);
                            // TODO: should we set this equal to a copy of u before we return?
                        }
                    }
                }
            }
            
            // TODO: do we need an Update function to update the local user from the one in the map?
            // Ideally, users are thrown away after use, but we should review.
            
            public static void Cleanup(UUID agentId) {
                string agentIdStr = agentId.ToString();
                lock(s_userMap) {
                    s_userMap.Remove(agentIdStr);
                }
            }
        }
        
        public class Transaction {
            
            // Primary Key value
            public UUID TransactionID;
            
            // Common, vital transaction details
            public UUID PayerID;
            public string PayerName;    // TODO: do we need to ensure this is not larger than the db field on hypergrid? - VARCHAR(255)
            public UUID PayeeID;
            public string PayeeName;    // TODO: do we need to ensure this is not larger than the db field on hypergrid? - VARCHAR(255)
            public int Amount;

            // Transaction classification info
            public int TransactionType;
            public string TransactionTypeString;
            
            // Subscription info
            public bool IsSubscriptionDebit;
            public UUID SubscriptionID;
            
            // Object info required when enacting/consume/canceling, delivering, and handling subscriptions
            public UUID PartID;         // UUID of object
            public string PartName;     // object name
            public string PartDescription;
            
            // Details required by IBuySellModule when delivering an object
            public UUID CategoryID;     // Appears to be a folder id used when saleType is copy
            public uint LocalID;        // Region specific ID of object.  Unclear why this is passed instead of UUID
            public int SaleType;        // object, copy, or contents
            
            // Storage of submission/response from Gloebit
            public bool Submitted;
            public bool ResponseReceived;
            public bool ResponseSuccess;
            public string ResponseStatus;
            public string ResponseReason;
            public int PayerEndingBalance; // balance returned by transact when fully successful.
            
            // State variables used internally in GloebitAPI
            public bool enacted;
            public bool consumed;
            public bool canceled;
            
            // Timestamps for reporting
            public DateTime cTime;
            public DateTime? enactedTime;
            public DateTime? finishedTime;
            
            private static Dictionary<string, Transaction> s_transactionMap = new Dictionary<string, Transaction>();
            private static Dictionary<string, Transaction> s_pendingTransactionMap = new Dictionary<string, Transaction>(); // tracks assets currently being worked on so that two state functions are not enacted at the same time.
            
            // Necessary for use with standard db serialization system
            // See Create() to generate a new transaction record
            // See Get() to retrieve an existing transaction record
            public Transaction() {
            }
            
            private Transaction(UUID transactionID, UUID payerID, string payerName, UUID payeeID, string payeeName, int amount, int transactionType, string transactionTypeString, bool isSubscriptionDebit, UUID subscriptionID, UUID partID, string partName, string partDescription, UUID categoryID, uint localID, int saleType) {
                
                // Primary Key value
                this.TransactionID = transactionID;
                
                // Common, vital transaction details
                this.PayerID = payerID;
                this.PayerName = payerName;
                this.PayeeID = payeeID;
                this.PayeeName = payeeName;
                this.Amount = amount;
                
                // Transaction classification info
                this.TransactionType = transactionType;
                this.TransactionTypeString = transactionTypeString;
                
                // Subscription info
                this.IsSubscriptionDebit = isSubscriptionDebit;
                this.SubscriptionID = subscriptionID;
                
                // Storage of submission/response from Gloebit
                this.Submitted = false;
                this.ResponseReceived = false;
                this.ResponseSuccess = false;
                this.ResponseStatus = String.Empty;
                this.ResponseReason = String.Empty;
                this.PayerEndingBalance = -1;
                
                
                // Object info required when enacting/consume/canceling, delivering, and handling subscriptions
                this.PartID = partID;
                this.PartName = partName;
                this.PartDescription = partDescription;
                
                // Details required by IBuySellModule when delivering an object
                this.CategoryID = categoryID;
                this.LocalID = localID;
                this.SaleType = saleType;
                
                // State variables used internally in GloebitAPI
                this.enacted = false;
                this.consumed = false;
                this.canceled = false;
                
                // Timestamps for reporting
                this.cTime = DateTime.UtcNow;
                this.enactedTime = null; // set to null instead of DateTime.MinValue to avoid crash on reading 0 timestamp
                this.finishedTime = null; // set to null instead of DateTime.MinValue to avoid crash on reading 0 timestamp
                // TODO: We have made these nullable and initialize to null.  We could alternatively choose a time that is not zero
                // and avoid any potential conficts from allowing null.
                // On MySql, I had to set the columns to allow NULL, otherwise, inserting null defaulted to the current local time.
                // On PGSql, I set the columns to allow NULL, but haven't tested.
                // On SQLite, I don't think that you can set them to allow NULL explicitely, and haven't checked defaults.
            }
            
            // Creates a new transaction
            // First verifies that a transaction with this ID does not already exist
            // --- If existing txn is found, returns null
            // Creates new Transaction, stores it in the cache and db
            public static Transaction Create(UUID transactionID, UUID payerID, string payerName, UUID payeeID, string payeeName, int amount, int transactionType, string transactionTypeString, bool isSubscriptionDebit, UUID subscriptionID, UUID partID, string partName, string partDescription, UUID categoryID, uint localID, int saleType)
            {
                // Create the Transaction
                Transaction txn = new Transaction(transactionID, payerID, payerName, payeeID, payeeName, amount, transactionType, transactionTypeString, isSubscriptionDebit, subscriptionID, partID, partName, partDescription, categoryID, localID, saleType);
                
                // Ensure that a transaction does not already exist with this ID before storing it
                string transactionIDstr = transactionID.ToString();
                Transaction existingTxn = Get(transactionIDstr);
                if (existingTxn != null) {
                    // Record in DB store with this id -- return null
                    return null;
                }
                // lock cache and ensure there is still no existing record before storing this txn.
                lock(s_transactionMap) {
                    if (s_transactionMap.TryGetValue(transactionIDstr, out existingTxn)) {
                        return null;
                    } else {
                        // Store the Transaction in the fast access cache
                        s_transactionMap[transactionIDstr] = txn;
                    }
                }
                
                // Store the Transaction to the persistent DB
                GloebitTransactionData.Instance.Store(txn);
                
                return txn;
            }
            
            public static Transaction Get(UUID transactionID) {
                return Get(transactionID.ToString());
            }
            
            public static Transaction Get(string transactionIDStr) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] in Transaction.Get");
                Transaction transaction = null;
                lock(s_transactionMap) {
                    s_transactionMap.TryGetValue(transactionIDStr, out transaction);
                }
                
                if(transaction == null) {
                    m_log.InfoFormat("[GLOEBITMONEYMODULE] Looking for prior transaction for {0}", transactionIDStr);
                    Transaction[] transactions = GloebitTransactionData.Instance.Get("TransactionID", transactionIDStr);
                    
                    switch(transactions.Length) {
                        case 1:
                            transaction = transactions[0];
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] FOUND TRANSACTION! {0} {1} {2}", transaction.TransactionID, transaction.PayerID, transaction.PayeeID);
                            lock(s_transactionMap) {
                                s_transactionMap[transactionIDStr] = transaction;
                            }
                            return transaction;
                        case 0:
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] Could not find transaction matching tID:{0}", transactionIDStr);
                            return null;
                        default:
                            throw new Exception(String.Format("[GLOEBITMONEYMODULE] Failed to find exactly one transaction for {0}", transactionIDStr));
                            return null;
                    }
                }
                
                return transaction;
            }
            
            public Uri BuildEnactURI(Uri baseURI) {
                UriBuilder enact_uri = new UriBuilder(baseURI);
                enact_uri.Path = "gloebit/transaction";
                enact_uri.Query = String.Format("id={0}&state={1}", this.TransactionID, "enact");
                return enact_uri.Uri;
            }
            public Uri BuildConsumeURI(Uri baseURI) {
                UriBuilder consume_uri = new UriBuilder(baseURI);
                consume_uri.Path = "gloebit/transaction";
                consume_uri.Query = String.Format("id={0}&state={1}", this.TransactionID, "consume");
                return consume_uri.Uri;
            }
            public Uri BuildCancelURI(Uri baseURI) {
                UriBuilder cancel_uri = new UriBuilder(baseURI);
                cancel_uri.Path = "gloebit/transaction";
                cancel_uri.Query = String.Format("id={0}&state={1}", this.TransactionID, "cancel");
                return cancel_uri.Uri;
            }
            
            /**************************************************/
            /******* ASSET STATE MACHINE **********************/
            /**************************************************/
            
            public static bool ProcessStateRequest(string transactionIDstr, string stateRequested, out string returnMsg) {
                bool result = false;
                
                // Retrieve asset
                Transaction myTxn = Transaction.Get(UUID.Parse(transactionIDstr));
                
                // If no matching transaction, return false
                // TODO: is this what we want to return?
                if (myTxn == null) {
                    returnMsg = "No matching transaction found.";
                    return false;
                }
                
                // Attempt to avoid race conditions (not sure if even possible)
                bool alreadyProcessing = false;
                lock(s_pendingTransactionMap) {
                    alreadyProcessing = s_pendingTransactionMap.ContainsKey(transactionIDstr);
                    if (!alreadyProcessing) {
                        // add to race condition protection
                        s_pendingTransactionMap[transactionIDstr] = myTxn;
                    }
                }
                if (alreadyProcessing) {
                    returnMsg = "pending";  // DO NOT CHANGE --- this message needs to be returned to Gloebit to know it is a retryable error
                    return false;
                }
                
                // Call proper state processor
                switch (stateRequested) {
                    case "enact":
                        result = myTxn.enactHold(out returnMsg);
                        break;
                    case "consume":
                        result = myTxn.consumeHold(out returnMsg);
                        if (result) {
                            lock(s_transactionMap) {
                                s_transactionMap.Remove(transactionIDstr);
                            }
                        }
                        break;
                    case "cancel":
                        result = myTxn.cancelHold(out returnMsg);
                        if (result) {
                            lock(s_transactionMap) {
                                s_transactionMap.Remove(transactionIDstr);
                            }
                        }
                        break;
                    default:
                        // no recognized state request
                        returnMsg = "Unrecognized state request";
                        result = false;
                        break;
                }
                
                // remove from race condition protection
                lock(s_pendingTransactionMap) {
                    s_pendingTransactionMap.Remove(transactionIDstr);
                }
                return result;
            }
            
            private bool enactHold(out string returnMsg) {
                if (this.canceled) {
                    // getting a delayed enact sent before cancel.  return false.
                    returnMsg = "Enact: already canceled";
                    return false;
                }
                if (this.consumed) {
                    // getting a delayed enact sent before consume.  return true.
                    returnMsg = "Enact: already consumed";
                    return true;
                }
                if (this.enacted) {
                    // already enacted. return true.
                    returnMsg = "Enact: already enacted";
                    return true;
                }
                // First reception of enact for asset.  Do specific enact functionality
                this.enacted = m_assetCallbacks.processAssetEnactHold(this, out returnMsg); // Do I need to grab the money module for this?
                
                // TODO: remove this after testing.
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.enactHold: {0}", this.enacted);
                if (this.enacted) {
                    m_log.InfoFormat("TransactionID: {0}", this.TransactionID);
                    m_log.InfoFormat("PayerID: {0}", this.PayerID);
                    m_log.InfoFormat("PayeeID: {0}", this.PayeeID);
                    m_log.InfoFormat("PartID: {0}", this.PartID);
                    m_log.InfoFormat("PartName: {0}", this.PartName);
                    m_log.InfoFormat("CategoryID: {0}", this.CategoryID);
                    m_log.InfoFormat("LocalID: {0}", this.LocalID);
                    m_log.InfoFormat("SaleType: {0}", this.SaleType);
                    m_log.InfoFormat("Amount: {0}", this.Amount);
                    m_log.InfoFormat("PayerEndingBalance: {0}", this.PayerEndingBalance);
                    m_log.InfoFormat("enacted: {0}", this.enacted);
                    m_log.InfoFormat("consumed: {0}", this.consumed);
                    m_log.InfoFormat("canceled: {0}", this.canceled);
                    m_log.InfoFormat("cTime: {0}", this.cTime);
                    m_log.InfoFormat("enactedTime: {0}", this.enactedTime);
                    m_log.InfoFormat("finishedTime: {0}", this.finishedTime);

                    // TODO: Should we store and update the time even if it fails to track time enact attempted/failed?
                    this.enactedTime = DateTime.UtcNow;
                    GloebitTransactionData.Instance.Store(this);
                }
                return this.enacted;
            }
            
            private bool consumeHold(out string returnMsg) {
                if (this.canceled) {
                    // Should never get a delayed consume after a cancel.  return false.
                    returnMsg = "Consume: already canceled";
                    return false;
                }
                if (!this.enacted) {
                    // Should never get a consume before we've enacted.  return false.
                    returnMsg = "Consume: Not yet enacted";
                    return false;
                }
                if (this.consumed) {
                    // already consumed. return true.
                    returnMsg = "Cosume: Already consumed";
                    return true;
                }
                // First reception of consume for asset.  Do specific consume functionality
                this.consumed = m_assetCallbacks.processAssetConsumeHold(this, out returnMsg); // Do I need to grab the money module for this?
                if (this.consumed) {
                    this.finishedTime = DateTime.UtcNow;
                    GloebitTransactionData.Instance.Store(this);
                }
                return this.consumed;
            }
            
            private bool cancelHold(out string returnMsg) {
                if (this.consumed) {
                    // Should never get a delayed cancel after a consume.  return false.
                    returnMsg = "Cancel: already consumed";
                    return false;
                }
                if (!this.enacted) {
                    // Hasn't enacted.  No work to undo.  return true.
                    returnMsg = "Cancel: not yet enacted";
                    // don't return here.  Still want to process cancel which will need to assess if enacted.
                    //return true;
                }
                if (this.canceled) {
                    // already canceled. return true.
                    returnMsg = "Cancel: already canceled";
                    return true;
                }
                // First reception of cancel for asset.  Do specific cancel functionality
                this.canceled = m_assetCallbacks.processAssetCancelHold(this, out returnMsg); // Do I need to grab the money module for this?
                if (this.canceled) {
                    this.finishedTime = DateTime.UtcNow;
                    GloebitTransactionData.Instance.Store(this);
                }
                return this.canceled;
            }
        }
        
        public class Subscription {
            
            // These 3 make up the primary key -- allows sim to swap back and forth between apps or GlbEnvs without getting errors
            public UUID ObjectID;       // ID of object with an LLGiveMoney or LLTransferLinden's script - local subscription ID
            public string AppKey;       // AppKey active when created
            public string GlbApiUrl;    // GlbEnv Url active when created
            
            public UUID SubscriptionID; // ID returned by create-subscription Gloebit endpoint
            public bool Enabled;        // enabled returned by Gloebit Endpoint - if not enabled, can't use.
            public DateTime ctime;      // time of creation
            
            // TODO: Are these necessary beyond sending to Gloebit? - can be rebuilt from object
            // TODO: a name or description change doesn't necessarily change the the UUID of the object --- how to deal with this?
            // TODO: name and description could be empty/blank --
            public string ObjectName;   // Name of object - treated as subscription_name by Gloebit
            public string Description;  // subscription_description - (should include object descriptin, but may include additional details)
            // TODO: additional details --- how to store --- do we need to store?
            
            private static Dictionary<string, Subscription> s_subscriptionMap = new Dictionary<string, Subscription>();
            
            public Subscription() {
            }
            
            private Subscription(UUID objectID, string appKey, string apiURL, string objectName, string objectDescription) {
                this.ObjectID = objectID;
                this.AppKey = appKey;
                this.GlbApiUrl = apiURL;
                
                this.ObjectName = objectName;
                this.Description = objectDescription;
                
                // Set defaults until we fill them in
                SubscriptionID = UUID.Zero;
                this.ctime = DateTime.UtcNow;
                this.Enabled = false;
                
                m_log.InfoFormat("[GLOEBITMONEYMODULE] in Subscription() oID:{0}, oN:{1}, oD:{2}", ObjectID, ObjectName, Description);
                
            }
            
            public static Subscription Init(UUID objectID, string appKey, string apiUrl, string objectName, string objectDescription) {
                string objectIDstr = objectID.ToString();
                
                Subscription s = new Subscription(objectID, appKey, apiUrl, objectName, objectDescription);
                lock(s_subscriptionMap) {
                    s_subscriptionMap[objectIDstr] = s;
                }
                
                GloebitSubscriptionData.Instance.Store(s);
                return s;
            }
            
            public static Subscription[] Get(UUID objectID) {
                return Get(objectID.ToString());
            }
            
            public static Subscription[] Get(string objectIDStr) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] in Subscription.Get");
                Subscription subscription = null;
                lock(s_subscriptionMap) {
                    s_subscriptionMap.TryGetValue(objectIDStr, out subscription);
                }
                
                /*if(subscription == null) {*/
                m_log.InfoFormat("[GLOEBITMONEYMODULE] Looking for subscriptions for {0}", objectIDStr);
                Subscription[] subscriptions = GloebitSubscriptionData.Instance.Get("ObjectID", objectIDStr);
                    /*
                    Subscription[] subsForAppWithKey = new Subscription[];
                    foreach (Subscription sub in subscriptions) {
                        if (sub.AppKey = "appkey" && sub.GlbApiUrl = "url") {
                            subsForAppWithKey.Append(sub);
                        }
                    }
                     */
                bool cacheDuplicate = false;
                m_log.InfoFormat("[GLOEBITMONEYMODULE] Found {0} subscriptions for {0} saved in the DB", subscriptions.Length, objectIDStr);
                if (subscription != null) {
                    m_log.InfoFormat("[GLOEBITMONEYMODULE] Found 1 cached subscriptions for {0}", subscriptions.Length, objectIDStr);
                    if (subscriptions.Length == 0) {
                        subscriptions = new Subscription[1];
                        subscriptions[0] = subscription;
                    } else {
                        for (int i = 0; i < subscriptions.Length; i++) {
                            if (subscriptions[i].ObjectID == subscription.ObjectID &&
                                subscriptions[i].AppKey == subscription.AppKey &&
                                subscriptions[i].GlbApiUrl == subscription.GlbApiUrl)
                            {
                                cacheDuplicate = true;
                                subscriptions[i] = subscription;
                                m_log.InfoFormat("[GLOEBITMONEYMODULE] Cached subscription was in db.  Replacing with cached version.");
                                break;
                            }
                        }
                        if (!cacheDuplicate) {
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] Combining Cached subscription with those from db.");
                            Subscription[] dbSubs = subscriptions;
                            subscriptions = new Subscription[dbSubs.Length + 1];
                            subscriptions[0] = subscription;
                            for (int i = 1; i < subscriptions.Length; i++) {
                                subscriptions[i] = dbSubs[i-1];
                            }
                        }
                        
                    }
                    
                } else {
                     m_log.InfoFormat("[GLOEBITMONEYMODULE] Found no cached subscriptions for {0}", subscriptions.Length, objectIDStr);
                }
                
                m_log.InfoFormat("[GLOEBITMONEYMODULE] Returning {0} subscriptions for {0}", subscriptions.Length, objectIDStr);
                return subscriptions;
                 /*
                    switch(subscriptions.Length) {
                        case 1:
                            subscription = subsForAppWithKey[0];
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] FOUND SUBSCRIPTION! {0} {1} {2} {3}", subscription.ObjectID, subscription.AppKey, subscription.GlbApiUrl, subscription.SubscriptionID);
                            lock(s_subscriptionMap) {
                                s_subscriptionMap[objectIDStr] = subscription;
                            }
                            return subscription;
                        case 0:
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] Could not find subscription matching oID:{0}", objectIDStr);
                            return null;
                        default:
                            throw new Exception(String.Format("[GLOEBITMONEYMODULE] Failed to find exactly one subscription for {0} {1} {2}", objectIDStr));
                            return null;
                    }
                }
                
                return subscription;
                  */
            }
            public static Subscription Get(UUID objectID, string appKey, Uri apiUrl) {
                return Get(objectID.ToString(), appKey, apiUrl.ToString());
            }
            
            public static Subscription Get(string objectIDStr, string appKey, Uri apiUrl) {
                return Get(objectIDStr, appKey, apiUrl.ToString());
            }
            
            public static Subscription Get(UUID objectID, string appKey, string apiUrl) {
                return Get(objectID.ToString(), appKey, apiUrl);
            }
            
            public static Subscription Get(string objectIDStr, string appKey, string apiUrl) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] in Subscription.Get");
                Subscription subscription = null;
                lock(s_subscriptionMap) {
                    s_subscriptionMap.TryGetValue(objectIDStr, out subscription);
                }
                
                if(subscription == null) {
                    m_log.InfoFormat("[GLOEBITMONEYMODULE] Looking for prior subscription for {0} {1} {2}", objectIDStr, appKey, apiUrl);
                    string[] keys = new string[] {"ObjectID", "AppKey", "GlbApiUrl"};
                    string[] values = new string[] {objectIDStr, appKey, apiUrl};
                    Subscription[] subscriptions = GloebitSubscriptionData.Instance.Get(keys, values);
                    
                    switch(subscriptions.Length) {
                        case 1:
                            subscription = subscriptions[0];
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] FOUND SUBSCRIPTION in DB! oID:{0} appKey:{1} url:{2} sID:{3} oN:{4} oD:{5}", subscription.ObjectID, subscription.AppKey, subscription.GlbApiUrl, subscription.SubscriptionID, subscription.ObjectName, subscription.Description);
                            lock(s_subscriptionMap) {
                                s_subscriptionMap[objectIDStr] = subscription;
                            }
                            return subscription;
                        case 0:
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] Could not find subscription matching oID:{0} appKey:{1} apiUrl:{2}", objectIDStr, appKey, apiUrl);
                            return null;
                        default:
                            throw new Exception(String.Format("[GLOEBITMONEYMODULE] Failed to find exactly one subscription for {0} {1} {2}", objectIDStr, appKey, apiUrl));
                            return null;
                    }
                }
                m_log.InfoFormat("[GLOEBITMONEYMODULE] FOUND SUBSCRIPTION in cache! oID:{0} appKey:{1} url:{2} sID:{3} oN:{4} oD:{5}", subscription.ObjectID, subscription.AppKey, subscription.GlbApiUrl, subscription.SubscriptionID, subscription.ObjectName, subscription.Description);
                return subscription;
            }
            
            public static Subscription GetBySubscriptionID(string subscriptionIDStr, string apiUrl) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] in Subscription.GetBySubscriptionID");
                Subscription subscription = null;
                Subscription localSub = null;
                
                
                m_log.InfoFormat("[GLOEBITMONEYMODULE] Looking for prior subscription for {0} {1}", subscriptionIDStr, apiUrl);
                string[] keys = new string[] {"SubscriptionID", "GlbApiUrl"};
                string[] values = new string[] {subscriptionIDStr, apiUrl};
                Subscription[] subscriptions = GloebitSubscriptionData.Instance.Get(keys, values);
                
                    
                switch(subscriptions.Length) {
                    case 1:
                        subscription = subscriptions[0];
                        m_log.InfoFormat("[GLOEBITMONEYMODULE] FOUND SUBSCRIPTION in DB! oID:{0} appKey:{1} url:{2} sID:{3} oN:{4} oD:{5}", subscription.ObjectID, subscription.AppKey, subscription.GlbApiUrl, subscription.SubscriptionID, subscription.ObjectName, subscription.Description);
                        lock(s_subscriptionMap) {
                            s_subscriptionMap.TryGetValue(subscription.ObjectID.ToString(), out localSub);
                            if (localSub == null) {
                                s_subscriptionMap[subscription.ObjectID.ToString()] = subscription;
                            }
                        }
                        if (localSub == null) {
                            // do nothing.  already added subscription to cache in lock
                        } else if (localSub.Equals(subscription)) {
                            // return cached sub instead of new sub from DB
                            subscription = localSub;
                        } else {
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] mapped Subscription is not equal to DB return --- shouldn't happen.  Investigate.");
                            m_log.ErrorFormat("Local Sub\n sID:{0}\n oID:{1}\n appKey:{2}\n apiUrl:{3}\n oN:{4}\n oD:{5}\n enabled:{6}\n ctime:{7}", localSub.SubscriptionID, localSub.ObjectID, localSub.AppKey, localSub.GlbApiUrl, localSub.ObjectName, localSub.Description, localSub.Enabled, localSub.ctime);
                            m_log.ErrorFormat("DB Sub\n sID:{0}\n oID:{1}\n appKey:{2}\n apiUrl:{3}\n oN:{4}\n oD:{5}\n enabled:{6}\n ctime:{7}", subscription.SubscriptionID, subscription.ObjectID, subscription.AppKey, subscription.GlbApiUrl, subscription.ObjectName, subscription.Description, subscription.Enabled, subscription.ctime);
                            // still return cached sub instead of new sub from DB
                            subscription = localSub;
                        }
                        return subscription;
                    case 0:
                        m_log.InfoFormat("[GLOEBITMONEYMODULE] Could not find subscription matching sID:{0} apiUrl:{1}", subscriptionIDStr, apiUrl);
                        return null;
                    default:
                        throw new Exception(String.Format("[GLOEBITMONEYMODULE] Failed to find exactly one subscription for {0} {1}", subscriptionIDStr, apiUrl));
                        return null;
                }
                m_log.InfoFormat("[GLOEBITMONEYMODULE] FOUND SUBSCRIPTION in cache! oID:{0} appKey:{1} url:{2} sID:{3} oN:{4} oD:{5}", subscription.ObjectID, subscription.AppKey, subscription.GlbApiUrl, subscription.SubscriptionID, subscription.ObjectName, subscription.Description);
                return subscription;
            }
            
            public override bool Equals(Object obj)
            {
                //Check for null and compare run-time types.
                if ((obj == null) || ! this.GetType().Equals(obj.GetType())) {
                    return false;
                }
                else {
                    Subscription s = (Subscription) obj;
                    // TODO: remove these info logs once we understand why things are not always equal
                    // m_log.InfoFormat("[GLOEBITMONEYMODULE] Subscription.Equals()");
                    // m_log.InfoFormat("ObjectID:{0}", (ObjectID == s.ObjectID));
                    // m_log.InfoFormat("AppKey:{0}", (AppKey == s.AppKey));
                    // m_log.InfoFormat("GlbApiUrl:{0}", (GlbApiUrl == s.GlbApiUrl));
                    // m_log.InfoFormat("ObjectName:{0}", (ObjectName == s.ObjectName));
                    // m_log.InfoFormat("Description:{0}", (Description == s.Description));
                    // m_log.InfoFormat("SubscriptionID:{0}", (SubscriptionID == s.SubscriptionID));
                    // m_log.InfoFormat("Enabled:{0}", (Enabled == s.Enabled));
                    // m_log.InfoFormat("ctime:{0}", (ctime == s.ctime));
                    // m_log.InfoFormat("ctime Equals:{0}", (ctime.Equals(s.ctime)));
                    // m_log.InfoFormat("ctime CompareTo:{0}", (ctime.CompareTo(s.ctime)));
                    // m_log.InfoFormat("ctime ticks:{0} == {1}", ctime.Ticks, s.ctime.Ticks);
                    
                    // NOTE: intentionally does not compare ctime as db truncates miliseconds to zero.
                    return ((ObjectID == s.ObjectID) &&
                            (AppKey == s.AppKey) &&
                            (GlbApiUrl == s.GlbApiUrl) &&
                            (ObjectName == s.ObjectName) &&
                            (Description == s.Description) &&
                            (SubscriptionID == s.SubscriptionID) &&
                            (Enabled == s.Enabled));
                }
            }
            

        }
        
        private delegate void CompletionCallback(OSDMap responseDataMap);

        private class GloebitRequestState {
            
            // Web request variables
            public HttpWebRequest request;
            public Stream responseStream;
            
            // Variables for storing Gloebit response stream data asynchronously
            public const int BUFFER_SIZE = 1024;    // size of buffer for max individual stream read events
            public byte[] bufferRead;               // buffer read to by stream read events
            public Decoder streamDecoder;           // Decoder for converting buffer to string in parts
            public StringBuilder responseData;      // individual buffer reads compiled/appended to full data

            public CompletionCallback continuation;
            
            // TODO: What to do when error states are reached since there is no longer a return?  Should we store an error state in a member variable?
            
            // Preferred constructor - use if we know the endpoint and agentID at creation time.
            public GloebitRequestState(HttpWebRequest req, CompletionCallback continuation)
            {
                request = req;
                responseStream = null;
                
                bufferRead = new byte[BUFFER_SIZE];
                streamDecoder = Encoding.UTF8.GetDecoder();     // Create Decoder for appropriate enconding type.
                responseData = new StringBuilder(String.Empty);

                this.continuation = continuation;
            }
            
        }

        public GloebitAPI(string key, string keyAlias, string secret, Uri url, IAsyncEndpointCallback asyncEndpointCallbacks, IAssetCallback assetCallbacks) {
            m_key = key;
            m_keyAlias = keyAlias;
            m_secret = secret;
            m_url = url;
            m_asyncEndpointCallbacks = asyncEndpointCallbacks;
            m_assetCallbacks = assetCallbacks;
        }
        
        /************************************************/
        /******** OAUTH2 AUTHORIZATION FUNCTIONS ********/
        /************************************************/


        /// <summary>
        /// Helper function to build the auth redirect callback url consistently everywhere.
        /// <param name="baseURI">The base url where this server's http services can be accessed.</param>
        /// <param name="agentId">The uuid of the agent being authorized.</param>
        /// </summary>
        private static Uri BuildAuthCallbackURL(Uri baseURI, UUID agentId) {
            UriBuilder redirect_uri = new UriBuilder(baseURI);
            redirect_uri.Path = "gloebit/auth_complete";
            redirect_uri.Query = String.Format("agentId={0}", agentId);
            return redirect_uri.Uri;
        }

        /// <summary>
        /// Request Authorization for this grid/region to enact Gloebit functionality on behalf of the specified OpenSim user.
        /// Sends Authorize URL to user which will launch a Gloebit authorize dialog.  If the user launches the URL and approves authorization from a Gloebit account, an authorization code will be returned to the redirect_uri.
        /// This is how a user links a Gloebit account to this OpenSim account.
        /// </summary>
        /// <param name="user">OpenSim User for which this region/grid is asking for permission to enact Gloebit functionality.</param>
        public void Authorize(IClientAPI user, Uri baseURI) {

            //********* BUILD AUTHORIZE QUERY ARG STRING ***************//
            ////Dictionary<string, string> auth_params = new Dictionary<string, string>();
            OSDMap auth_params = new OSDMap();

            auth_params["client_id"] = m_key;
            if(!String.IsNullOrEmpty(m_keyAlias)) {
                auth_params["r"] = m_keyAlias;
            }

            auth_params["scope"] = "user balance transact";
            auth_params["redirect_uri"] = BuildAuthCallbackURL(baseURI, user.AgentId).ToString();
            auth_params["response_type"] = "code";
            auth_params["user"] = user.Name;
            auth_params["uid"] = user.AgentId.ToString();
            // TODO - make use of 'state' param for XSRF protection
            // auth_params["state"] = ???;

            string query_string = BuildURLEncodedParamString(auth_params);

            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Authorize query_string: {0}", query_string);

            //********** BUILD FULL AUTHORIZE REQUEST URI **************//

            Uri request_uri = new Uri(m_url, String.Format("oauth2/authorize?{0}", query_string));
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Authorize request_uri: {0}", request_uri);
            
            //*********** SEND AUTHORIZE REQUEST URI TO USER ***********//
            // currently can not launch browser directly for user, so send in message
            
            // TODO: move this to GMM interface.
            // TODO: Shouldn't this be an interface function from the GMM since launching a web page will be specific to the integration?
            SendUrlToClient(user, "AUTHORIZE GLOEBIT", "To use Gloebit currency, please authorize Gloebit to link to your avatar's account on this web page:", request_uri);

        }
        
        /// <summary>
        /// Begins request to exchange an authorization code granted from the Authorize endpoint for an access token necessary for enacting Gloebit functionality on behalf of this OpenSim user.
        /// This begins the second phase of the OAuth2 process.  It is activated by the redirect_uri of the Authorize function.
        /// This occurs completely behind the scenes for security purposes.
        /// </summary>
        /// <returns>The authenticated User object containing the access token necessary for enacting Gloebit functionality on behalf of this OpenSim user.</returns>
        /// <param name="user">OpenSim User for which this region/grid is asking for permission to enact Gloebit functionality.</param>
        /// <param name="auth_code">Authorization Code returned to the redirect_uri from the Gloebit Authorize endpoint.</param>
        public void ExchangeAccessToken(User user, string auth_code, Uri baseURI) {
            
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.ExchangeAccessToken AgentID:{0}", user.PrincipalID);
            
            UUID agentID = UUID.Parse(user.PrincipalID);
            
            // ************ BUILD EXCHANGE ACCESS TOKEN POST REQUEST ******** //
            OSDMap auth_params = new OSDMap();

            auth_params["client_id"] = m_key;
            auth_params["client_secret"] = m_secret;
            auth_params["code"] = auth_code;
            auth_params["grant_type"] = "authorization_code";
            auth_params["scope"] = "user balance transact";
            auth_params["redirect_uri"] = BuildAuthCallbackURL(baseURI, agentID).ToString();
            
            HttpWebRequest request = BuildGloebitRequest("oauth2/access-token", "POST", null, "application/x-www-form-urlencoded", auth_params);
            if (request == null) {
                // ERROR
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.oauth2/access-token failed to create HttpWebRequest");
                // TODO: signal error
                return;
            }
            
            // **** Asynchronously make web request **** //
            IAsyncResult r = request.BeginGetResponse(GloebitWebResponseCallback,
                new GloebitRequestState(request,
                    delegate(OSDMap responseDataMap) {
                        // ************ PARSE AND HANDLE EXCHANGE ACCESS TOKEN RESPONSE ********* //

                        string token = responseDataMap["access_token"];
                        string app_user_id = responseDataMap["app_user_id"];
                        bool success = false;
                        // TODO - do something to handle the "refresh_token" field properly
                        if(!String.IsNullOrEmpty(token)) {
                            success = true;
                            user = User.Authorize(agentID, token, app_user_id);
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CompleteExchangeAccessToken Success User:{0}", user);
                        } else {
                            success = false;
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CompleteExchangeAccessToken error: {0}, reason: {1}", responseDataMap["error"], responseDataMap["reason"]);
                            // TODO: signal error;
                        }
                        m_asyncEndpointCallbacks.exchangeAccessTokenCompleted(success, user, responseDataMap);
                    }));
        }
        
        
        /***********************************************/
        /********* GLOEBIT FUNCTIONAL ENDPOINS *********/
        /***********************************************/
        
        // ******* GLOEBIT BALANCE ENDPOINTS ********* //
        // requires "balance" in scope of authorization token
        

        /// <summary>
        /// Requests the Gloebit balance for the OpenSim user with this OpenSim agentID.
        /// Returns zero if a link between this OpenSim user and a Gloebit account have not been created and the user has not granted authorization to this grid/region.
        /// Requires "balance" in scope of authorization token.
        /// </summary>
        /// <returns>The Gloebit balance for the Gloebit accunt the user has linked to this OpenSim agentID on this grid/region.  Returns zero if a link between this OpenSim user and a Gloebit account has not been created and the user has not granted authorization to this grid/region.</returns>
        /// <param name="user">User object for the OpenSim user for whom the balance request is being made. <see cref="GloebitAPI.User.Get(UUID)"/></param>
        /// <param name="invalidatedToken">Bool set to true if request fails due to a bad token which we have invalidated.  Eventually, this should be a more general error interface</param>
        /// <returns>Double balance of user or 0.0 if fails for any reason</returns>
        public double GetBalance(User user, out bool invalidatedToken) {
            
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.balance for agentID:{0}", user.PrincipalID);
            
            invalidatedToken = false;
            
            //************ BUILD GET BALANCE GET REQUEST ********//
            
            HttpWebRequest request = BuildGloebitRequest("balance", "GET", user);
            if (request == null) {
                // ERROR
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.balance failed to create HttpWebRequest");
                return 0;
            }
            
            //************ PARSE AND HANDLE GET BALANCE RESPONSE *********//
            
            HttpWebResponse response = (HttpWebResponse) request.GetResponse();
            string status = response.StatusDescription;
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.balance status:{0}", status);
            using(StreamReader response_stream = new StreamReader(response.GetResponseStream())) {
                string response_str = response_stream.ReadToEnd();

                OSDMap responseData = (OSDMap)OSDParser.DeserializeJson(response_str);
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.balance responseData:{0}", responseData.ToString());

                if (responseData["success"]) {
                    double balance = responseData["balance"].AsReal();
                    return balance;
                } else {
                    string reason = responseData["reason"];
                    switch(reason) {
                        case "unknown token1":
                        case "unknown token2":
                            // The token is invalid (probably the user revoked our app through the website)
                            // so force a reauthorization next time.
                            user.InvalidateToken();
                            invalidatedToken = true;
                            break;
                        default:
                            m_log.ErrorFormat("Unknown error getting balance, reason: '{0}'", reason);
                            break;
                    }
                    return 0.0;
                }
            }

        }
        
        // ******* GLOEBIT TRANSACT ENDPOINTS ********* //
        // requires "transact" in scope of authorization token

        /// <summary>
        /// Begins Gloebit transaction request for the gloebit amount specified from the sender to the owner of the Gloebit app this module is connected to.
        /// </summary>
        /// <param name="sender">User object for the user sending the gloebits. <see cref="GloebitAPI.User.Get(UUID)"/></param>
        /// <param name="senderName">OpenSim Name of the user on this grid sending the gloebits.</param>
        /// <param name="amount">quantity of gloebits to be transacted.</param>
        /// <param name="description">Description of purpose of transaction recorded in Gloebit transaction histories.</param>
        public void Transact(User sender, string senderName, int amount, string description) {
            
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.transact senderID:{0} senderName:{1} amount:{2} description:{3}", sender.PrincipalID, senderName, amount, description);
            
            UUID transactionId = UUID.Random();

            OSDMap transact_params = new OSDMap();

            transact_params["version"] = 1;
            transact_params["application-key"] = m_key;
            transact_params["request-created"] = (int)(DateTime.UtcNow.Ticks / 10000000);  // TODO - figure out if this is in the right units
            transact_params["username-on-application"] = String.Format("{0} - {1}", senderName, sender.PrincipalID);

            transact_params["transaction-id"] = transactionId.ToString();
            transact_params["gloebit-balance-change"] = amount;
            transact_params["asset-code"] = description;
            transact_params["asset-quantity"] = 1;
            
            transact_params["app-user-id"] = sender.GloebitID;
            
            HttpWebRequest request = BuildGloebitRequest("transact", "POST", sender, "application/json", transact_params);
            if (request == null) {
                // ERROR
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.transact failed to create HttpWebRequest");
                return;
                // TODO once we return, return error value
            }
            
            // **** Asynchronously make web request **** //
            IAsyncResult r = request.BeginGetResponse(GloebitWebResponseCallback,
                new GloebitRequestState(request, 
                    delegate(OSDMap responseDataMap) {
                        m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact response: {0}", responseDataMap);

                        //************ PARSE AND HANDLE TRANSACT RESPONSE *********//

                        bool success = (bool)responseDataMap["success"];
                        // TODO: if success=false: id, balance, product-count are invalid.  Do not set balance.
                        double balance = responseDataMap["balance"].AsReal();
                        string reason = responseDataMap["reason"];
                        m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact success: {0} balance: {1} reason: {2}", success, balance, reason);
                        // TODO - update the user's balance
                    }));
        }
        

        

        // TODO: does recipient have to authorize app?  Do they need to become a merchant on that platform or opt in to agreeing to receive gloebits?  How do they currently authorize sale on a grid?
        // TODO: Should we pass a bool for charging a fee or the actual fee % -- to the module owner --- could always charge a fee.  could be % set in app for when charged.  could be % set for each transaction type in app.
        // TODO: Should we always charge our fee, or have a bool or transaction type for occasions when we may not charge?
        // TODO: Do we need an endpoint for reversals/refunds, or just an admin interface from Gloebit?
        
        /// <summary>
        /// Asynchronously request Gloebit transaction from the sender to the recipient with the details specified in txn.
        /// </summary>
        /// <remarks>
        /// Asynchronous.  See alternate synchronous transaction if caller needs immediate success/failure response regarding transaction in synchronous flow.
        /// Upon async response: parses response data, records response in txn, creates TransactionStage and TransactionFailure from response strings,
        /// handles any necessary failure processing, and calls TransactU2UCompleted callback with response data for module to process and message user.
        /// </remarks>
        /// <param name="txn">Transaction representing local transaction we are requesting.  This is prebuilt by GMM, and already includes most transaciton details such as amount, payer/payee id and name.  <see cref="GloebitAPI.Transaction"/></param>
        /// <param name="description">Description of purpose of transaction recorded in Gloebit transaction histories.  Should eventually be added to txn and removed as parameter</param>
        /// <param name="descMap">Map of platform, location & transaction descriptors for tracking/querying and transaciton history details.  For more details, <see cref="GloebitMoneyModule.buildBaseTransactionDescMap"/> helper function.</param>
        /// <param name="sender">User object for the user sending the gloebits. <see cref="GloebitAPI.User.Get(UUID)"/></param>
        /// <param name="recipient">User object for the user receiving the gloebits. <see cref="GloebitAPI.User.Get(UUID)"/></param>
        /// <param name="recipientEmail">Email address of the user on this grid receiving the gloebits.  Empty string if user created account without email.</param>
        /// <param name="baseURI">Asset representing local transaction part requiring processing via callbacks.</param>
        /// <returns>true if async transactU2U web request was built and submitted successfully; false if failed to submit request;  If true, IAsyncEndpointCallback transactU2UCompleted should eventually be called with additional details on state of request.</returns>
        public bool TransactU2U(Transaction txn, string description, OSDMap descMap, User sender, User recipient, string recipientEmail, Uri baseURI) {

            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U senderID:{0} senderName:{1} recipientID:{2} recipientName:{3} recipientEmail:{4} amount:{5} description:{6} baseURI:{7}", sender.PrincipalID, txn.PayerName, recipient.PrincipalID, txn.PayeeName, recipientEmail, txn.Amount, description, baseURI);
            
            // ************ IDENTIFY GLOEBIT RECIPIENT ******** //
            // 1. If the recipient has authed ever, we'll have a recipient.GloebitID to use.
            // 2. If not, and the recipeint's account is on this grid, Get the email from the profile for the account.
            
            // ************ BUILD AND SEND TRANSACT U2U POST REQUEST ******** //
            
            // TODO: Assert that txn != null
            // TODO: Assert that transactionId != UUID.Zero
            
            // TODO: move away from OSDMap to a standard C# dictionary
            OSDMap transact_params = new OSDMap();
            PopulateTransactParams(transact_params, sender.GloebitID, txn, description, recipientEmail, recipient.GloebitID, descMap, baseURI);
            
            HttpWebRequest request = BuildGloebitRequest("transact-u2u", "POST", sender, "application/json", transact_params);
            if (request == null) {
                // ERROR
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U failed to create HttpWebRequest");
                return false;
                // TODO once we return, return error value
            }

            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U about to BeginGetResponse");
            // **** Asynchronously make web request **** //
            IAsyncResult r = request.BeginGetResponse(GloebitWebResponseCallback,
			                                          new GloebitRequestState(request, 
			                        delegate(OSDMap responseDataMap) {
                                        
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U response: {0}", responseDataMap);

                //************ PARSE AND HANDLE TRANSACT-U2U RESPONSE *********//

                // read response and store in txn
                PopulateTransactResponse(txn, responseDataMap);
                                        
                // Build Stage & Failure arguments
                 TransactionStage stage = TransactionStage.BEGIN;
                 TransactionFailure failure = TransactionFailure.NONE;
                // TODO: should we pass the txn instead of the individual string args here?
                PopulateTransactStageAndFailure(out stage, out failure, txn.ResponseSuccess, txn.ResponseStatus, txn.ResponseReason);
                // TODO: consider making stage & failure part of the GloebitTransactions table.
                // still pass explicitly to make sure they can't be modified before callback uses them.
                                        
                // Handle any necessary functional adjustments based on failures
                ProcessTransactFailure(txn, failure, sender);

                // TODO - decide if we really want to issue this callback even if the token was invalid
                m_asyncEndpointCallbacks.transactU2UCompleted(responseDataMap, sender, recipient, txn, stage, failure);
            }));
            
            // Successfully submitted transaction request to Gloebit
            txn.Submitted = true;
            // TODO: if we add stage to txn, we should set it to TransactionStage.SUBMIT here.
            GloebitTransactionData.Instance.Store(txn);
            return true;
        }
        
        /// <summary>
        /// Synchronously request Gloebit transaction from the sender to the recipient with the details specified in txn.
        /// </summary>
        /// <remarks>
        /// Synchronous.  See alternate, and preferred, asynchronous transaction if caller does not need immediate success/failure response
        /// regarding transaction in synchronous flow.
        /// Upon sync response: parses response data, records response in txn, creates TransactionStage and TransactionFailure from response strings,
        /// handles any necessary failure processing, and calls TransactU2UCompleted callback with response data for module to process and message user.
        /// </remarks>
        /// <param name="txn">Transaction representing local transaction we are requesting.  This is prebuilt by GMM, and already includes most transaciton details such as amount, payer/payee id and name.  <see cref="GloebitAPI.Transaction"/></param>
        /// <param name="description">Description of purpose of transaction recorded in Gloebit transaction histories.  Should eventually be added to txn and removed as parameter</param>
        /// <param name="descMap">Map of platform, location & transaction descriptors for tracking/querying and transaciton history details.  For more details, <see cref="GloebitMoneyModule.buildBaseTransactionDescMap"/> helper function.</param>
        /// <param name="sender">User object for the user sending the gloebits. <see cref="GloebitAPI.User.Get(UUID)"/></param>
        /// <param name="recipient">User object for the user receiving the gloebits. <see cref="GloebitAPI.User.Get(UUID)"/></param>
        /// <param name="recipientEmail">Email address of the user on this grid receiving the gloebits.  Empty string if user created account without email.</param>
        /// <param name="baseURI">Asset representing local transaction part requiring processing via callbacks.</param>
        /// <param name="stage">TransactionStage handed back to caller representing stage of transaction that failed or completed.</param>
        /// <param name="failure">TransactionFailure handed back to caller representing specific transaction failure, or NONE.</param>
        /// <returns>
        /// true if sync transactU2U web request was built and submitted successfully and returned a successful response from the web service.
        /// --- successful response means that all Gloebit components of the transaction enacted successfully, and transaction would only fail if local
        ///     component enaction failed.  (Note: possibiliy of only "queue" success if resubmitted)
        /// false if failed to submit request, or if response returned false.
        /// --- See out parameters stage and failure for details on failure.
        /// If true, or if false in any stage after SUBMIT, IAsyncEndpointCallback transactU2UCompleted will be called with additional details on state of request prior to this function returning.
        /// </returns>
        public bool TransactU2USync(Transaction txn, string description, OSDMap descMap, User sender, User recipient, string recipientEmail, Uri baseURI, out TransactionStage stage, out TransactionFailure failure)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U-Sync senderID:{0} senderName:{1} recipientID:{2} recipientName:{3} recipientEmail:{4} amount:{5} description:{6} baseURI:{7}", sender.PrincipalID, txn.PayerName, recipient.PrincipalID, txn.PayeeName, recipientEmail, txn.Amount, description, baseURI);
            
            // ************ IDENTIFY GLOEBIT RECIPIENT ******** //
            // TODO: How do we identify recipient?  Get email from profile from OpenSim UUID?
            // TODO: If we use emails, we may need to make sure account merging works for email/3rd party providers.
            // TODO: If we allow anyone to receive, need to ensure that gloebits received are locked down until user authenticates as merchant.
            
            // ************ BUILD AND SEND TRANSACT U2U POST REQUEST ******** //
            
            // TODO: Assert that txn != null
            // TODO: Assert that transactionId != UUID.Zero
            
            // TODO: move away from OSDMap to a standard C# dictionary
            OSDMap transact_params = new OSDMap();
            PopulateTransactParams(transact_params, sender.GloebitID, txn, description, recipientEmail, recipient.GloebitID, descMap, baseURI);
            
            HttpWebRequest request = BuildGloebitRequest("transact-u2u", "POST", sender, "application/json", transact_params);
            if (request == null) {
                // ERROR
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U-Sync failed to create HttpWebRequest");
                stage = TransactionStage.SUBMIT;
                failure = TransactionFailure.BUILD_WEB_REQUEST_FAILED;
                return false;
            }
            
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U-Sync about to GetResponse");
            // **** Synchronously make web request **** //
            HttpWebResponse response = (HttpWebResponse) request.GetResponse();
            string status = response.StatusDescription;
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U-Sync status:{0}", status);
            // TODO: think we should set submitted here to status
            if (response.StatusCode == HttpStatusCode.OK) {
                // Successfully submitted transaction request to Gloebit
                txn.Submitted = true;
                // TODO: if we add stage to txn, we should set it to TransactionStage.SUBMIT here.
                GloebitTransactionData.Instance.Store(txn);
                // TODO: should this alert that submission was successful?
            } else {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U-Sync status not OK.  How to handle?");
                stage = TransactionStage.SUBMIT;
                failure = TransactionFailure.SUBMISSION_FAILED;
                return false;
            }
            
            //************ PARSE AND HANDLE TRANSACT-U2U RESPONSE *********//
            using(StreamReader response_stream = new StreamReader(response.GetResponseStream())) {
                // **** Synchronously read response **** //
                string response_str = response_stream.ReadToEnd();
                
                OSDMap responseDataMap = (OSDMap)OSDParser.DeserializeJson(response_str);
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U-Sync responseData:{0}", responseDataMap.ToString());
                
                // read response and store in txn
                PopulateTransactResponse(txn, responseDataMap);
                
                // Populate Stage & Failure arguments based on response
                // TODO: should we pass the txn instead of the individual string args here?
                PopulateTransactStageAndFailure(out stage, out failure, txn.ResponseSuccess, txn.ResponseStatus, txn.ResponseReason);
                // TODO: consider making stage & failure part of the GloebitTransactions table.
                // still pass explicitly to make sure they can't be modified before callback uses them.
                
                // Handle any necessary functional adjustments based on failures
                ProcessTransactFailure(txn, failure, sender);
                
                // TODO - decide if we really want to issue this callback even if the token was invalid
                m_asyncEndpointCallbacks.transactU2UCompleted(responseDataMap, sender, recipient, txn, stage, failure);
                
                if (failure == TransactionFailure.NONE) {
                    // success.  could also check txn.ResponseSuccess or just return txn.ResponseSuccess
                    return true;
                } else {
                    // failure.
                    return false;
                }
            }
        }
        
        /* Transact U2U Helper Functions */
        
        /// <summary>
        /// Builds the form parameters in the format that the GloebitAPI TransactU2U endpoint expects for this transaction.
        /// </summary>
        /// <param name="transact_params">OSDMap which will be populated with form parameters.</param>
        /// <param name="txn">Transaction representing local transaction we are create transact_params from.</param>
        /// <param name="description">Description of purpose of transaction recorded in Gloebit transaction histories.  Should eventually be added to txn and removed as parameter</param>
        /// <param name="recipientEmail">Email of the user being paid gloebits.  May be empty.</param>
        /// <param name="recipientGloebitID">UUID from the Gloebit system of user being paid.  May be empty.</param>
        /// <param name="recipient">User object for the user receiving the gloebits. <see cref="GloebitAPI.User.Get(UUID)"/></param>
        /// <param name="recipientEmail">Email address of the user on this grid receiving the gloebits.  Empty string if user created account without email.</param>
        /// <param name="descMap">Map of platform, location & transaction descriptors for tracking/querying and transaciton history details.  For more details, <see cref="GloebitMoneyModule.buildBaseTransactionDescMap"/> helper function.</param>
        /// <param name="baseURI">Asset representing local transaction part requiring processing via callbacks.</param>
        private void PopulateTransactParams(OSDMap transact_params, string senderGloebitID, Transaction txn, string description, string recipientEmail, string recipientGloebitID, OSDMap descMap, Uri baseURI)
        {
            /***** Base Params *****/
            transact_params["version"] = 1;
            transact_params["application-key"] = m_key;
            transact_params["request-created"] = (int)(DateTime.UtcNow.Ticks / 10000000);  // TODO - figure out if this is in the right units
            //transact_params["username-on-application"] = String.Format("{0} - {1}", senderName, sender.PrincipalID);
            transact_params["username-on-application"] = txn.PayerName;
            transact_params["transaction-id"] = txn.TransactionID.ToString();
            
            // TODO: make payerID required in all txns and move to base params section
            transact_params["buyer-id-on-application"] = txn.PayerID;
            transact_params["app-user-id"] = senderGloebitID;
            
            /***** Asset Params *****/
            // TODO: should only build this if asset, not product txn.  u2u txn probably has to be asset.
            transact_params["gloebit-balance-change"] = txn.Amount;
            // TODO: move description into GloebitAPI.Transaction and remove from arguments.
            transact_params["asset-code"] = description;
            transact_params["asset-quantity"] = 1;
            
            /***** Product Params *****/
            // GMM doesn't use this, so here for eventual complete client api.  u2u txn probably has to be asset.
            // If product is used instead of asset:
            //// product is required
            //// product-quantity is optional positive integer (assumed 1 if not supplied)
            //// character-id is optional character_id
            
            /***** Callback Params *****/
            // TODO: add a bool to transaction for whether to register callbacks.  For now, this always happens.
            transact_params["asset-enact-hold-url"] = txn.BuildEnactURI(baseURI);
            transact_params["asset-consume-hold-url"] = txn.BuildConsumeURI(baseURI);
            transact_params["asset-cancel-hold-url"] = txn.BuildCancelURI(baseURI);
            // m_log.InfoFormat("[GLOEBITMONEYMODULE] asset-enact-hold-url:{0}", transact_params["asset-enact-hold-url"]);
            // m_log.InfoFormat("[GLOEBITMONEYMODULE] asset-consume-hold-url:{0}", transact_params["asset-consume-hold-url"]);
            // m_log.InfoFormat("[GLOEBITMONEYMODULE] asset-cancel-hold-url:{0}", transact_params["asset-cancel-hold-url"]);
            
            /***** U2U specific transact params *****/
            transact_params["seller-name-on-application"] = txn.PayeeName;
            transact_params["seller-id-on-application"] = txn.PayeeID;
            if (!String.IsNullOrEmpty(recipientGloebitID) && recipientGloebitID != UUID.Zero.ToString()) {
                transact_params["seller-id-from-gloebit"] = recipientGloebitID;
            }
            if (!String.IsNullOrEmpty(recipientEmail)) {
                transact_params["seller-email-address"] = recipientEmail;
            }
            
            // TODO: make descmap optional or required in all txns and move to own section
            if (descMap != null) {
                transact_params["platform-desc-names"] = descMap["platform-names"];
                transact_params["platform-desc-values"] = descMap["platform-values"];
                transact_params["location-desc-names"] = descMap["location-names"];
                transact_params["location-desc-values"] = descMap["location-values"];
                transact_params["transaction-desc-names"] = descMap["transaction-names"];
                transact_params["transaction-desc-values"] = descMap["transaction-values"];
            }
            
            /***** Subscription Params *****/
            if (txn.IsSubscriptionDebit) {
                transact_params["automated-transaction"] = true;
                transact_params["subscription-id"] = txn.SubscriptionID;
            }
        }
        
        /// <summary>
        /// Given the response from a TransactU2U web reqeust, retrieves and stores vital information in the transaction object and data store.
        /// </summary>
        /// <param name="txn">Transaction representing local transaction for which we received the response.</param>
        /// <param name="responseDataMap">OSDMap containing the web response body.</param>
        private void PopulateTransactResponse(Transaction txn, OSDMap responseDataMap)
        {
            // Get response data
            bool success = (bool)responseDataMap["success"];
            // NOTE: if success=false: id, balance, product-count are invalid.
            double balance = responseDataMap["balance"].AsReal();
            string reason = responseDataMap["reason"];
            // TODO: ensure status is always sent
            string status = "";
            if (responseDataMap.ContainsKey("status")) {
                status = responseDataMap["status"];
            }
            
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U response recieved success: {0} balance: {1} status: {2} reason: {3}", success, balance, status, reason);
            
            // Store response data in GloebitAPI.Transaction record
            txn.ResponseReceived = true;
            txn.ResponseSuccess = success;
            txn.ResponseStatus = responseDataMap["status"];
            txn.ResponseReason = reason;
            if (success) {
                txn.PayerEndingBalance = (int)balance;
            }
            GloebitTransactionData.Instance.Store(txn);
        }
        
        /// <summary>
        /// Convert required TransactU2U response data of success, status and reason into simpler to manage stage and failure parameters.
        /// </summary>
        /// <param name="stage">TransactionStage out parameter will be set to stage completed or failed in.</param>
        /// <param name="failure">TransactionFailure out parameter will be set to specific failure or NONE.</param>
        /// <param name="success">Bool representing success or failure of the TransactU2U api call.</param>
        /// <param name="status">String status returned by TransactU2U api call.</param>
        /// <param name="reason">String reason returned by the Transact U2U call detailing failure or "success".</param>
        private void PopulateTransactStageAndFailure(out TransactionStage stage, out TransactionFailure failure, bool success, string status, string reason)
        {
            stage = TransactionStage.BEGIN;
            failure = TransactionFailure.NONE;
            
            // Build Stage & Failure arguments
            // TODO: eventually, these can be passed directly from server and just converted and passed through to interface func.
            // TODO: Do we want logs here or in GMM?
            if (success) {
                if (reason == "success") {                          /* successfully queued, early enacted all non-asset transaction parts */
                    switch (reason) {
                        case "success":
                            // TODO: could make a new stage here: EARLY_ENACT, or more accurately, ENACT_GLOEBIT is complete.
                        case "resubmitted":
                            // TODO: this is truly only queued.  see transaction processor.  Early-enact not tried.
                        default:
                            // unhandled response.
                            stage = TransactionStage.QUEUE;
                            failure = TransactionFailure.NONE;
                            break;
                    }
                }
            } else if (status == "queued") {                                /* successfully queued.  an early enact failed */
                // This is a complex error/flow response which we should really consider if there is a better way to handle.
                // Is this always a permanent failure?  Could this succeed in queue if user purchased gloebits at same time?
                // Can anything other than insufficient funds cause this problem?  Internet Issue?
                stage = TransactionStage.ENACT_GLOEBIT;
                // TODO: perhaps the stage should be queue here, and early_enact error as this is not being enacted by a transaction processor.
                
                if (reason == "insufficient balance") {                     /* permanent failure - actionable by buyer */
                    failure = TransactionFailure.INSUFFICIENT_FUNDS;
                } else if (reason == "pending") {                           /* queue will retry enacts */
                    // may not be possible.  May only be "pending" if includes a charge part which these will not.
                    failure = TransactionFailure.ENACTING_GLOEBIT_FAILED;
                } else {                                                    /* perm failure - assumes tp will get same response form part.enact */
                    // Shouldn't ever get here.
                    failure = TransactionFailure.ENACTING_GLOEBIT_FAILED;
                }
            } else {                                                        /* failure prior to successful queing.  Something requires fixing */
                if (reason == "unknown OAuth2 token") {                     /* Invalid Token.  May have been revoked by user or expired */
                    stage = TransactionStage.AUTHENTICATE;
                    failure = TransactionFailure.AUTHENTICATION_FAILED;
                } else if (status == "queuing-failed") {                    /* failed to queue.  net or processor error */
                    stage = TransactionStage.QUEUE;
                    failure = TransactionFailure.QUEUEING_FAILED;
                } else if (status == "failed") {                            /* race condition - already queued */
                    // nothing to tell user.  buyer doesn't need to know it was double submitted
                    stage = TransactionStage.QUEUE;
                    failure = TransactionFailure.RACE_CONDITION;
                } else if (status == "cannot-spend") {                      /* Buyer's gloebit account is locked and not allowed to spend gloebits */
                    stage = TransactionStage.VALIDATE;
                    failure = TransactionFailure.PAYER_ACCOUNT_LOCKED;
                } else if (status == "cannot-receive") {                    /* Seller's gloebit account can not receive gloebits */
                    stage = TransactionStage.VALIDATE;
                    failure = TransactionFailure.PAYEE_CANNOT_RECEIVE;
                } else if (status == "unknown-merchant") {                  /* can not identify merchant from params supplied by app */
                    stage = TransactionStage.VALIDATE;
                    failure = TransactionFailure.PAYEE_CANNOT_BE_IDENTIFIED;
                } else if (reason == "transaction is missing parameters needed to identify gloebit account of seller - supply at least one of seller-email-address or seller-id-from-gloebit.") {
                    // TODO: handle this better in the long run
                    stage = TransactionStage.VALIDATE;
                    failure = TransactionFailure.PAYEE_CANNOT_BE_IDENTIFIED;
                } else if (reason == "Transaction with automated-transaction=True is missing subscription-id") {
                    stage = TransactionStage.VALIDATE;
                    failure = TransactionFailure.FORM_MISSING_SUBSCRIPTION_ID;
                } else if (status == "unknown-subscription") {
                    stage = TransactionStage.VALIDATE;
                    failure = TransactionFailure.SUBSCRIPTION_NOT_FOUND;
                } else if (status == "unknown-subscription-authorization") {
                    stage = TransactionStage.VALIDATE;
                    failure = TransactionFailure.SUBSCRIPTION_AUTH_NOT_FOUND;
                } else if (status == "subscription-authorization-pending") {
                    stage = TransactionStage.VALIDATE;
                    failure = TransactionFailure.SUBSCRIPTION_AUTH_PENDING;
                } else if (status == "subscription-authorization-declined") {
                    stage = TransactionStage.VALIDATE;
                    failure = TransactionFailure.SUBSCRIPTION_AUTH_DECLINED;
                } else {                                                    /* App issue --- Something needs fixing by app */
                    stage = TransactionStage.VALIDATE;
                    failure = TransactionFailure.FORM_GENERIC_ERROR;
                }
            }
            
            // TODO: consider making stage & failure part of the GloebitTransactions table and storing them here.
            // still pass explicitly to make sure they can't be modified before callback uses them.
        }
        
        /// <summary>
        /// Handle any functional adjustments required after a TransactU2U failure.  Currently soley invalidates the token
        /// if failure was due to a bad OAuth2 token.
        /// </summary>
        /// <param name="txn">Transaction that failed.</param>
        /// <param name="failure">TransactionFailure detailing specific failure or NONE.</param>
        /// <param name="sender">User object for payer containing necessary details and OAuth2 token.</param>
        private void ProcessTransactFailure(Transaction txn, TransactionFailure failure, User sender)
        {
            switch (failure) {
                case TransactionFailure.NONE:
                    // default - no error - proceed
                    break;
                case TransactionFailure.AUTHENTICATION_FAILED:
                    // The token is invalid (probably the user revoked our app through the website)
                    // so force a reauthorization next time.
                    sender.InvalidateToken();
                    break;
                case TransactionFailure.SUBSCRIPTION_AUTH_NOT_FOUND:
                case TransactionFailure.SUBSCRIPTION_AUTH_PENDING:
                case TransactionFailure.SUBSCRIPTION_AUTH_DECLINED:
                    // TODO: why are we explicitly logging this here?  Should this be moved to GMM?
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U Subscription-Auth issue: '{0}'", txn.ResponseReason);
                    break;
                default:
                    // TODO: why are we logging this here?  Should this be moved to GMM?
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U Unknown error posting transaction, reason: '{0}'", txn.ResponseReason);
                    break;
            }
        }
        
        
        /// <summary>
        /// Request a new subscription be created by Gloebit for this app.
        /// Subscriptions are required for any recurring, unattended/automated payments that a user will sign up for.
        /// Upon completion of this request, the interface function CreateSubscriptionCompleted will be called with the results.
        /// If successful, an ID will be created and returnd by Gloebit which should be used for requesting user authorization and
        /// creating transactions under this subscription code.
        /// </summary>
        /// <param name="subscription">Local GloebitAPI.Subscription with the details for this subscription.</param>
        /// <param name="baseURI">Callback URI -- not currently used.  Included in case we add callback ability.</param>
        /// <returns>
        /// True if the request was successfully submitted to Gloebit;
        /// False if submission fails.
        /// See CreateSubscriptionCompleted for async callback with relevant results of this api call.
        /// </returns>
        public bool CreateSubscription(Subscription subscription, Uri baseURI) {
            
            //TODO stop logging auth_code
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscription Subscription:{0}", subscription);
            
            // ************ BUILD EXCHANGE ACCESS TOKEN POST REQUEST ******** //
            OSDMap sub_params = new OSDMap();

            sub_params["client_id"] = m_key;
            sub_params["client_secret"] = m_secret;
            
            sub_params["application-key"] = m_key;  // TODO: consider getting rid of this.
            if (m_key != subscription.AppKey) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscription GloebitAPI.m_key:{0} differs from Subscription.AppKey:{1}", m_key, subscription.AppKey);
                return false;
            }
            sub_params["local-id"] = subscription.ObjectID;
            sub_params["name"] = subscription.ObjectName;
            sub_params["description"] = subscription.Description;
            // TODO: should we add additional-details to sub_params?
            
            HttpWebRequest request = BuildGloebitRequest("create-subscription", "POST", null, "application/json", sub_params);
            if (request == null) {
                // ERROR
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscription failed to create HttpWebRequest");
                // TODO: signal error
                return false;
            }
            
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscription about to BeginGetResponse");
            // **** Asynchronously make web request **** //
            IAsyncResult r = request.BeginGetResponse(GloebitWebResponseCallback,
			                                          new GloebitRequestState(request, 
			                        delegate(OSDMap responseDataMap) {
                                        
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscription response: {0}", responseDataMap);

                //************ PARSE AND HANDLE CREATE SUBSCRIPTION RESPONSE *********//

                // Grab fields always included in response
                bool success = (bool)responseDataMap["success"];
                string reason = responseDataMap["reason"];
                string status = responseDataMap["status"];
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscription success: {0} reason: {1} status: {2}", success, reason, status);
                
                if (success) {
                    string subscriptionIDStr = responseDataMap["id"];
                    bool enabled = (bool) responseDataMap["enabled"];
                    subscription.SubscriptionID = UUID.Parse(subscriptionIDStr);
                    subscription.Enabled = enabled;
                    GloebitSubscriptionData.Instance.Store(subscription);
                    if (status == "duplicate") {
                        m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscription duplicate request to create subscription");
                    }
                } else {
                    switch(reason) {
                        case "Unexpected DB insert integrity error.  Please try again.":
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscription failed from {0}", reason);
                            break;
                        case "different subscription exists with this app-subscription-id":
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscription failed due to different subscription with same object id -- subID:{0} name:{1} desc:{2} ad:{3} enabled:{4} ctime:{5}",
                                              responseDataMap["existing-subscription-id"], responseDataMap["existing-subscription-name"], responseDataMap["existing-subscription-description"], responseDataMap["existing-subscription-additional_details"], responseDataMap["existing-subscription-enabled"], responseDataMap["existing-subscription-ctime"]);
                            break;
                        case "Unknown DB Error":
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscription failed from {0}", reason);
                            break;
                        default:
                            m_log.ErrorFormat("Unknown error posting create subscription, reason: '{0}'", reason);
                            break;
                    }
                }

                // TODO - decide if we really want to issue this callback even if the token was invalid
                m_asyncEndpointCallbacks.createSubscriptionCompleted(responseDataMap, subscription);
            }));
            
            return true;
        }
    
        
        /// <summary>
        /// Request creation of a pending authorization for an existing subscription for this application.
        /// A subscription authorization must be created and then approved by the user before a recurring, unattended/automated payment
        /// can be requested for this user by this app.  The authorization is for a single, specific subscription.
        /// The authorization is not only specific to the Gloebit account linked to this user, but also to the app account by the id of the use ron this app.
        /// A subscription for this app must already have been created via CreateSubscription.
        /// Upon completion of this request, the interface function CreateSubscriptionAuthorizationCompleted will be called with the results.
        /// The application does not need to store SubscriptionAuthorizations locally.  A transaction can be submitted without knowledge of an
        /// existing approved authorization.  If an approval exists, the transaction will process.  If not, the transaction will fail with relevant
        /// information provided to the transaction completed async callback function.
        /// </summary>
        /// <param name="sub">Local GloebitAPI.Subscription with the details for this subscription.</param>
        /// <param name="sender"> GloebitAPI.User of the user for whom we're creating a pending subscription authorization request.</param>
        /// <param name="senderName"> String of the user name on the app.  This is supplied to display back to the user which app account they are authorizing.</param>
        /// <param name="baseURI">Callback URI -- not currently used.  Included in case we add callback ability.</param>
        /// <param name="client"> IClientAPI for this user.  provided to pass through to CreateSubscriptionAuthorizationCompleted.</param>
        /// <returns>
        /// True if the request was successfully submitted to Gloebit;
        /// False if submission fails.
        /// See CreateSubscriptionAuthorizationCompleted for async callback with relevant results of this api call.
        /// </returns>
        public bool CreateSubscriptionAuthorization(Subscription sub, User sender, string senderName, Uri baseURI, IClientAPI client) {

            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization subscriptionID:{0} senderID:{1} senderName:{2} baseURI:{3}", sub.SubscriptionID, sender.PrincipalID, senderName, baseURI);
            
            
            // ************ BUILD AND SEND CREATE SUBSCRIPTION AUTHORIZATION POST REQUEST ******** //
            
            OSDMap sub_auth_params = new OSDMap();
            
            sub_auth_params["application-key"] = m_key;
            sub_auth_params["request-created"] = (int)(DateTime.UtcNow.Ticks / 10000000);  // TODO - figure out if this is in the right units
            //sub_auth_params["username-on-application"] = String.Format("{0} - {1}", senderName, sender.PrincipalID);
            sub_auth_params["username-on-application"] = senderName;
            sub_auth_params["user-id-on-application"] = sender.PrincipalID;
            if (!String.IsNullOrEmpty(sender.GloebitID) && sender.GloebitID != UUID.Zero.ToString()) {
                sub_auth_params["app-user-id"] = sender.GloebitID;
            }
            // TODO: should we add additional-details to sub_auth_params?
            sub_auth_params["subscription-id"] = sub.SubscriptionID;
            
            HttpWebRequest request = BuildGloebitRequest("create-subscription-authorization", "POST", sender, "application/json", sub_auth_params);
            if (request == null) {
                // ERROR
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization failed to create HttpWebRequest");
                return false;
                // TODO once we return, return error value
            }

            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization about to BeginGetResponse");
            // **** Asynchronously make web request **** //
            IAsyncResult r = request.BeginGetResponse(GloebitWebResponseCallback,
			                                          new GloebitRequestState(request, 
			                        delegate(OSDMap responseDataMap) {
                                        
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization response: {0}", responseDataMap);

                //************ PARSE AND HANDLE CREATE SUBSCRIPTION AUTHORIZATION RESPONSE *********//

                // Grab fields always included in response
                bool success = (bool)responseDataMap["success"];
                string reason = responseDataMap["reason"];
                string status = responseDataMap["status"];
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization success: {0} reason: {1} status: {2}", success, reason, status);
                
                if (success) {
                    string subscriptionAuthIDStr = responseDataMap["id"];
                    // TODO: if we decide to store auths, this would be a place to do so.
                    // sub.SubscriptionID = UUID.Parse(subscriptionIDStr);
                    // GloebitSubscriptionData.Instance.Store(sub);
                    if (status == "duplicate") {
                        m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization duplicate request to create subscription");
                    } else if (status == "duplicate-and-already-approved-by-user") {
                        m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization duplicate request to create subscription - subscription has already been approved by user.");
                    } else if (status == "duplicate-and-previously-declined-by-user") {
                        m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization SUCCESS & FAILURE - user previously declined authorization -- consider if app should re-request or if that is harrassing user or has Gloebit API reset this automatcially?. status:{0} reason:{1}", status, reason);
                    }
                    
                    string sPending = responseDataMap["pending"];
                    string sEnabled = responseDataMap["enabled"];
                    m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization SUCCESS pending:{0}, enabled:{1}.", sPending, sEnabled);
                } else {
                    switch(status) {
                        case "cannot-transact":
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization FAILED - no transact permissions on this user. status:{0} reason:{1}", status, reason);
                            break;
                        case "subscription-not-found":
                        case "mismatched-application-key":
                        case "mis-matched-subscription-ids":
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization FAILED - could not properly identify subscription - status:{0} reason:{1}", status, reason);
                            break;
                        case "subscription-disabled":
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization FAILED - app has disabled this subscription. status:{0} reason:{1}", status, reason);
                            break;
                        case "duplicate-and-previously-declined-by-user":
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization FAILED - user previously declined authorization -- consider if app should re-request or if that is harrassing user. status:{0} reason:{1}", status, reason);
                            break;
                        default:
                            switch(reason) {
                                case "Unexpected DB insert integrity error.  Please try again.":
                                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization FAILED from {0}", reason);
                                    break;
                                case "Unknown DB Error":
                                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization failed from {0}", reason);
                                    break;
                                default:
                                    m_log.ErrorFormat("Unknown error posting create subscription authorization, reason: '{0}'", reason);
                                    break;
                            }
                            break;
                    }
                }

                // TODO - decide if we really want to issue this callback even if the token was invalid
                m_asyncEndpointCallbacks.createSubscriptionAuthorizationCompleted(responseDataMap, sub, sender, client);
            }));
            
            return true;
        }
        

        /// <summary>
        /// Builds a URI for a user to purchase gloebits
        /// </summary>
        /// <returns>The fully constructed url with arguments for receiving a callback when the purchase is complete.</returns>
        public Uri BuildPurchaseURI(Uri callbackBaseURL, User u) {
            UriBuilder purchaseUri = new UriBuilder(m_url);
            purchaseUri.Path = "/purchase";
            if (callbackBaseURL != null) {
                // TODO: this whole url should be built in GMM, not GAPI
                // could do a try/catch here with the errors that UriBuilder can throw to also prevent crash from poorly formatted server uri.
                UriBuilder callbackUrl = new UriBuilder(callbackBaseURL);
                callbackUrl.Path = "/gloebit/buy_complete";
                callbackUrl.Query = String.Format("agentId={0}", u.PrincipalID);
                purchaseUri.Query = String.Format("reset&r={0}&inform={1}", m_keyAlias, callbackUrl.Uri);
            } else {
                purchaseUri.Query = String.Format("reset&r={0}", m_keyAlias);
            }
            return purchaseUri.Uri;
        }
 
        /***********************************************/
        /********* GLOEBIT API HELPER FUNCTIONS ********/
        /***********************************************/
    
        // TODO: OSDMap or Dictionary for params
    
        /// <summary>
        /// Build an HTTPWebRequest for a Gloebit endpoint.
        /// </summary>
        /// <param name="relative_url">endpoint & query args.</param>
        /// <param name="method">HTTP method for request -- eg: "GET", "POST".</param>
        /// <param name="user">User object for this authenticated user if one exists.</param>
        /// <param name="content_type">content type of post/put request  -- eg: "application/json", "application/x-www-form-urlencoded".</param>
        /// <param name="paramMap">parameter map for body of request.</param>
        private HttpWebRequest BuildGloebitRequest(string relativeURL, string method, User user, string contentType = "", OSDMap paramMap = null) {
            
            // combine Gloebit base url with endpoint and query args in relative url.
            Uri requestURI = new Uri(m_url, relativeURL);
        
            // Create http web request from URL
            HttpWebRequest request = (HttpWebRequest) WebRequest.Create(requestURI);
        
            // Add authorization header
            if (user != null && user.GloebitToken != "") {
                request.Headers.Add("Authorization", String.Format("Bearer {0}", user.GloebitToken));
            }
        
            // Set request method and body
            request.Method = method;
            switch (method) {
                case "GET":
                    m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.BuildGloebitRequest GET relativeURL:{0}", relativeURL);
                    break;
                case "POST":
                case "PUT":
                    string paramString = "";
                    byte[] postData = null;
                    request.ContentType = contentType;
                
                    // Build paramString in proper format
                    if (paramMap != null) {
                        if (contentType == "application/x-www-form-urlencoded") {
                            paramString = BuildURLEncodedParamString(paramMap);
                        } else if (contentType == "application/json") {
                            paramString = OSDParser.SerializeJsonString(paramMap);
                        } else {
                            // ERROR - we are not handling this content type properly
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.BuildGloebitRequest relativeURL:{0}, unrecognized content type:{1}", relativeURL, contentType);
                            return null;
                        }
                
                        // Byte encode paramString and write to requestStream
                        postData = System.Text.Encoding.UTF8.GetBytes(paramString);
                        request.ContentLength = postData.Length;
                        // TODO: look into BeginGetRequestStream()
                        using (Stream s = request.GetRequestStream()) {
                            s.Write(postData, 0, postData.Length);
                        }
                    } else {
                        // Probably should be a GET request if it has no paramMap
                        m_log.WarnFormat("[GLOEBITMONEYMODULE] GloebitAPI.BuildGloebitRequest relativeURL:{0}, Empty paramMap on {1} request", relativeURL, method);
                    }
                    break;
                default:
                    // ERROR - we are not handling this request type properly
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.BuildGloebitRequest relativeURL:{0}, unrecognized web request method:{1}", relativeURL, method);
                    return null;
            }
            return request;
        }
        
        /// <summary>
        /// Build an application/x-www-form-urlencoded string from the paramMap.
        /// </summary>
        /// <param name="ParamMap">Parameters to be encoded.</param>
        private string BuildURLEncodedParamString(OSDMap paramMap) {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.BuildURLEncodedParamString building from paramMap:{0}:", paramMap);
            StringBuilder paramBuilder = new StringBuilder();
            foreach (KeyValuePair<string, OSD> p in (OSDMap)paramMap) {
                if(paramBuilder.Length != 0) {
                    paramBuilder.Append('&');
                }
                paramBuilder.AppendFormat("{0}={1}", HttpUtility.UrlEncode(p.Key), HttpUtility.UrlEncode(p.Value.ToString()));
            }
            return( paramBuilder.ToString() );
        }
        
        /***********************************************/
        /** GLOEBIT ASYNCHRONOUS API HELPER FUNCTIONS **/
        /***********************************************/
        
        /// <summary>
        /// Handles asynchronous return from web request BeginGetResponse.
        /// Retrieves response stream and asynchronously begins reading response stream.
        /// </summary>
        /// <param name="ar">State details compiled as this web request is processed.</param>
        public void GloebitWebResponseCallback(IAsyncResult ar) {
            
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.GloebitWebResponseCallback");
            
            // Get the RequestState object from the async result.
            GloebitRequestState myRequestState = (GloebitRequestState) ar.AsyncState;
            HttpWebRequest req = myRequestState.request;
            
            // Call EndGetResponse, which produces the WebResponse object
            //  that came from the request issued above.
            try
            {
                HttpWebResponse resp = (HttpWebResponse)req.EndGetResponse(ar);

                //  Start reading data from the response stream.
                // TODO: look into BeginGetResponseStream();
                Stream responseStream = resp.GetResponseStream();
                myRequestState.responseStream = responseStream;

                // TODO: Do I need to check the CanRead property before reading?

                //  Begin reading response into myRequestState.BufferRead
                // TODO: May want to make use of iarRead for calls by syncronous functions
                IAsyncResult iarRead = responseStream.BeginRead(myRequestState.bufferRead, 0, GloebitRequestState.BUFFER_SIZE, GloebitReadCallBack, myRequestState);

                // TODO: on any failure/exception, propagate error up and provide to user in friendly error message.
            }
            catch (ArgumentNullException e) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.GloebitWebResponseCallback ArgumentNullException e:{0}", e.Message);
            }
            catch (WebException e) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.GloebitWebResponseCallback WebException e:{0} URI:{1}", e.Message, req.RequestUri);
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] response:{0}", e.Response);
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] e:{0}", e.ToString ());
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] source:{0}", e.Source);
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] stack_trace:{0}", e.StackTrace);
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] status:{0}", e.Status);
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] target_site:{0}", e.TargetSite);
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] data_count:{0}", e.Data.Count);
            }
            catch (InvalidOperationException e) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.GloebitWebResponseCallback InvalidOperationException e:{0}", e.Message);
            }
            catch (ArgumentException e) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.GloebitWebResponseCallback ArgumentException e:{0}", e.Message);
            }

        }
        
        /// <summary>
        /// Handles asynchronous return from web request response stream BeginRead().
        /// Retrieves and stores buffered read, or closes stream and passes requestState to requestState.continuation().
        /// </summary>
        /// <param name="ar">State details compiled as this web request is processed.</param>
        private void GloebitReadCallBack(IAsyncResult ar)
        {
            // m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.GloebitReadCallback");
            
            // Get the RequestState object from AsyncResult.
            GloebitRequestState myRequestState = (GloebitRequestState)ar.AsyncState;
            Stream responseStream = myRequestState.responseStream;
            
            // Handle data read.
            int bytesRead = responseStream.EndRead( ar );
            if (bytesRead > 0)
            {
                // Decode and store the bytesRead in responseData
                Char[] charBuffer = new Char[GloebitRequestState.BUFFER_SIZE];
                int len = myRequestState.streamDecoder.GetChars(myRequestState.bufferRead, 0, bytesRead, charBuffer, 0);
                String str = new String(charBuffer, 0, len);
                myRequestState.responseData.Append(str);
                
                // Continue reading data until
                // responseStream.EndRead returns 0 for end of stream.
                // TODO: should we be doing anything with result???
                IAsyncResult result = responseStream.BeginRead(myRequestState.bufferRead, 0, GloebitRequestState.BUFFER_SIZE, GloebitReadCallBack, myRequestState);
            }
            else
            {
                // Done Reading
                
                // Close down the response stream.
                responseStream.Close();
                
                if (myRequestState.responseData.Length <= 0) {
                    // TODO: Is this necessarily an error if we don't have data???
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.GloebitReadCallback error: No Data");
                    // TODO: signal error
                }
                
                if (myRequestState.continuation != null) {
                    OSDMap responseDataMap = (OSDMap)OSDParser.DeserializeJson(myRequestState.responseData.ToString());
                    myRequestState.continuation(responseDataMap);
                }
            }
        }
        
        // TODO: These functions should probably be moved to the money module.
        
        /// <summary>
        /// Sends a message with url to user.
        /// </summary>
        /// <param name="client">IClientAPI of client we are sending the URL to</param>
        /// <param name="title">string title of message we are sending with the url</param>
        /// <param name="body">string body of message we are sending with the url</param>
        /// <param name="uri">full url we are sending to the client</param>
        public static void SendUrlToClient(IClientAPI client, string title, string body, Uri uri)
        {
            string imMessage = String.Format("{0}\n\n{1}", title, body);
            UUID fromID = UUID.Zero;
            string fromName = String.Empty;
            UUID toID = client.AgentId;
            bool isFromGroup = false;
            UUID imSessionID = toID;     // Don't know what this is used for.  Saw it hacked to agent id in friendship module
            bool isOffline = true;       // I believe when true, if user is logged out, saves message and delivers it next time the user logs in.
            bool addTimestamp = false;
            GridInstantMessage im = new GridInstantMessage(client.Scene, fromID, fromName, toID, (byte)InstantMessageDialog.GotoUrl, isFromGroup, imMessage, imSessionID, isOffline, Vector3.Zero, Encoding.UTF8.GetBytes(uri.ToString() + "\0"), addTimestamp);
            client.SendInstantMessage(im);
        }
        
        // TODO: This should become an interface function and moved to the Money Module
        /// <summary>
        /// Request a subscriptin authorization from a user.
        /// This specifically sends a message with a clickable URL to the client.
        /// </summary>
        /// <param name="client">IClientAPI of client we are sending the URL to</param>
        /// <param name="subAuthID">ID of the authorization request the user will be asked to approve - provided by Gloebit.</param>
        /// <param name="sub">Subscription which containes necessary details for message to user.</param>
        /// <param name="isDeclined">Bool is true if this sub auth has already been declined by the user which should present different messaging.</param>
        public void SendSubscriptionAuthorizationToClient(IClientAPI client, string subAuthID, Subscription sub, bool isDeclined)
        {
            // Build the URL -- consider making a helper to be done in the API once we move this to the GMM
            Uri request_uri = new Uri(m_url, String.Format("authorize-subscription/{0}/", subAuthID));
            
            if (client != null) {
                // TODO: adjust our wording
                string title = "GLOEBIT Subscription Authorization Request (scripted object auto-debit):";
                string body;
                if (!isDeclined) {
                    body = String.Format("To approve or decline the request to authorize this object:\n   {0}\n   {1}\n\nPlease visit this web page:", sub.ObjectName, sub.ObjectID);
                } else {
                    body = String.Format("You've already declined the request to authorize this object:\n   {0}\n   {1}\n\nIf you would like to review the request, or alter your response, please visit this web page:", sub.ObjectName, sub.ObjectID);
                }
                
                SendUrlToClient(client, title, body, request_uri);
            } else {
                // TODO: what should we do in this case?  Ideally, Gloebit has also emailed the user when this request was created.
                // Perhaps, when user is not logged in, add to queue and send when user next logs in.
            }
        }
        
        public enum TransactionStage : int
        {
            BEGIN           = 0,    // Not really a stage.  may not need this
            BUILD           = 100,   // Preparing the transaction locally for submission
            SUBMIT          = 200,   // Submitting the transaciton to Gloebit via the API Endpoints.
            AUTHENTICATE    = 300,   // Checking OAuth Token included in header
            VALIDATE        = 400,   // Validating the txn form submitted to Gloebit -- may need to add in somesubscription specific validations
            QUEUE           = 500,   // Queing the transaction for processing
            ENACT_GLOEBIT   = 600,   // perfoming Gloebit components of transaction
            ENACT_ASSET     = 650,   // performing local components of transaction
            CONSUME_GLOEBIT = 700,   // committing Gloebit components of transaction
            CONSUME_ASSET   = 750,   // committing local components of transaction
            CANCEL_GLOEBIT  = 800,   // canceling gloebit components of transaction
            CANCEL_ASSET    = 850,   // canceling local components of transaction
            COMPLETE        = 1000,  // Not really a stage.  May not need this.  Once local asset is consumed, we are complete.
        }
        
        public enum TransactionFailure : int
        {
            NONE                            = 0,
            SUBMISSION_FAILED               = 200,
            BUILD_WEB_REQUEST_FAILED        = 201,
            AUTHENTICATION_FAILED           = 300,
            VALIDATION_FAILED               = 400,
            FORM_GENERIC_ERROR              = 401,
            FORM_MISSING_SUBSCRIPTION_ID    = 411,
            PAYER_ACCOUNT_LOCKED            = 441,
            PAYEE_CANNOT_BE_IDENTIFIED      = 451,
            PAYEE_CANNOT_RECEIVE            = 452,
            SUBSCRIPTION_NOT_FOUND          = 461,
            SUBSCRIPTION_AUTH_NOT_FOUND     = 471,
            SUBSCRIPTION_AUTH_PENDING       = 472,
            SUBSCRIPTION_AUTH_DECLINED      = 473,
            QUEUEING_FAILED                 = 500,
            RACE_CONDITION                  = 501,
            ENACTING_GLOEBIT_FAILED         = 600,
            INSUFFICIENT_FUNDS              = 601,
            ENACTING_ASSET_FAILED           = 650
        }
    }
}
