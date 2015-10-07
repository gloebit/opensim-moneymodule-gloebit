/*
 * Copyright (c) 2015 Gloebit LLC
 *
 * Copyright (c) Contributors, http://opensimulator.org/
 * See opensim CONTRIBUTORS.TXT for a full list of copyright holders.
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
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Text;
using log4net;
using Nini.Config;
using Nwc.XmlRpc;
using Mono.Addins;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenMetaverse.StructuredData;     // TODO: turn transactionData into a dictionary of <string, object> and remove this.

[assembly: Addin("Gloebit", "0.1")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]

namespace Gloebit.GloebitMoneyModule
{
    enum GLBEnv {
        None = 0,
        Custom = 1,
        Sandbox = 2,
        Production = 3,
    }

    /// <summary>
    /// This is only the functionality required to make the functionality associated with money work
    /// (such as land transfers).  There is no money code here!  Use FORGE as an example for money code.
    /// Demo Economy/Money Module.  This is a purposely crippled module!
    ///  // To land transfer you need to add:
    /// -helperuri http://serveraddress:port/
    /// to the command line parameters you use to start up your client
    /// This commonly looks like -helperuri http://127.0.0.1:9000/
    ///
    /// </summary>

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "GloebitMoneyModule")]
    public class GloebitMoneyModule : IMoneyModule, ISharedRegionModule, GloebitAPI.IAsyncEndpointCallback, GloebitAPI.IAssetCallback
    {
        
        /// <summary>
        /// Class for sending questions to a user and receiving and processing the clicked resopnse.
        /// Create a derived class (see CreateSubscriptionAuthorizationDialog) to send a new type of message.
        /// All derived classes must implement all abstract methods and properties, as well as a
        /// constructor which calls the base Dialog constructor.
        /// To send a dialog to the user:
        /// --- 1. Call the constructor for the derived Dialog type with new.
        /// --- 2. Call the static method Dialog.Send() with the derived Dialog you just created.
        /// --- 3. Handle the user's response via the derived classes implementation of ProcessResponse()
        /// </summary>
        public abstract class Dialog
        {
            // Master map of Dialogs - Map of AgentIDs to map of channels to Dialog message info
            private static Dictionary<UUID, Dictionary<int, Dialog>> s_clientDialogMap = new Dictionary<UUID, Dictionary<int, Dialog>>();
            
            // Time of last purge used to purge old Dialogs for which the user didn't respond.  See PurgeOldDialogs()
            private static DateTime s_lastPurgedOldDialogs = DateTime.UtcNow;
            private static readonly Object s_purgeLock = new Object();       // Lock to enforce only a single purge per period
            
            // Counter used to create unique channels for each dialog message
            private const int c_MinChannel = -1700000000;           // channel limited to -2,147,483,648 -- reset when we get close
            private const int c_MaxChannel = -1600000001;              // Use negative channels only as they are harder for standard viewers to mimic.
            protected static int s_lastChannel = c_MaxChannel + 1;  // Use negative channels only as they are harder for standard viewers to mimic.
            
            // variables not used by dialog because we are sending the message, not an object from inworld
            protected static readonly UUID c_msgSourceObjectID = UUID.Zero;         // Message from us, not from inworld object, so Zero
            protected static readonly UUID c_msgSourceObjectOwnerID = UUID.Zero;    // Message from us, not from inworld object, so Zero
            protected static readonly UUID c_backgroundTextureID = UUID.Zero;       // Background; was never implemented on client, so Zero
            
            // variables consistent across all dialog messages from GloebitMoneyModule
            protected const string c_msgHeaderWordOne = "GLOEBIT";        // Word 1 of msg header displayed in Dialog Message - designed to be possessive name
            protected const string c_msgHeaderWordTwo = "MoneyModule";     // Word 2 of msg header displayed in Dialog Message - designed to be possessive name
            // Header: "{0} {1}'s".format(c_msgHeaderWordOne, c_msgHeaderWordTwo)
            
            // variables common to all Dialog messages
            // TODO: test cTime to make sure they are different.
            protected readonly DateTime cTime = DateTime.UtcNow;     // Time created - used for purging old Dialogs
            protected readonly int Channel = PickChannel();          // Channel response will be received on for this Dialog
            protected readonly IClientAPI Client;                    // Client to whom we're sending the Dialog
            protected readonly UUID AgentID;                         // AgentID of client to whom we're sending the Dialog
            
            // Properties that derived classes must implement - all are displayed in Dialog message
            protected abstract string MsgTitle { get; }             // Message Title -- submitted as the source ObjectName
            protected abstract string MsgBody { get; }              // Message Body -- submitted as the message from the Object.
            protected abstract string[] ButtonResponses { get; }    // Button Responses
            
            // Methods that derived classes must implement
            protected abstract void ProcessResponse(IClientAPI client, OSChatMessage chat);
            
            /// <summary>
            /// base Dialog constructor.
            /// Must be called by all derived class constructors
            /// Sets some universally required parameters which are specific to the Dialog instance.
            /// </summary>
            /// <param name="client">IClientAPI of agent to whom we are sending the Dialog</param>
            /// <param name="agentID">UUID of agent to whom we are sending the Dialog</param>
            protected Dialog(IClientAPI client, UUID agentID)
            {
                this.AgentID = agentID;
                this.Client = client;
            }
            
            /// <summary>
            /// Creates a channel for this Dialog.
            /// The channel is the chat channel that this dialog sends it's response through.
            /// Chat channels are limited to -2,147,483,648 to 2,147,483,647
            /// channel should always be negative (harder to mimic from standard viewers).
            /// channel should always be unique for other active dialogs for the same user otherwise the
            /// previous dialog will disappear.
            /// channel should also always be unique from other active dialogs because it is used
            /// as the unique identifier to alocate a response to a specific Dialog.
            /// Channel could be made random in the future
            /// </summary>
            private static int PickChannel()
            {
                int local_lc, myChannel;
                do {
                    local_lc = s_lastChannel;
                    myChannel = local_lc - 1;
                    
                    // channel limited to -2,147,483,648 -- reset when we get close
                    if (myChannel < c_MinChannel) {
                        myChannel = c_MaxChannel;
                    }
                } while (local_lc != Interlocked.CompareExchange(ref s_lastChannel, myChannel, local_lc));
                // while ensures that one and only one thread finieshes and modifies s_lastChannel
                // If s_lastChannel has changed since local_lc was set, this fails and the loop runs again.
                // If multiple threads are executing at the same time, at least one will always succeed.
                
                return myChannel;
            }
            
            /// <summary>
            /// Send instance of derived dialog to user.
            /// This is the public interface and only way a Dialog message should be sent.
            /// Create an instance of derived Dialog class using new to pass to this method.
            /// </summary>
            /// <param name="dialog">Instance of derived Dialog to track and send to user.</param>
            public static void Send(Dialog dialog)
            {
                dialog.Open();
                dialog.Deliver();
            }
            
            /// <summary>
            /// Adds dialog to our master map.
            /// If there are no other dialogs for this user, creates a dictionary of dialogs and registers a chat listener
            /// for this user.
            /// Always called before delivering the dialog to the user in order to prepare to handle the response.
            /// See Close() for cleanup
            /// </summary>
            private void Open()
            {
                lock (s_clientDialogMap) {
                    /***** Create Dialog Dict for agent and register chat listener if no open dialogs exist for this agent *****/
                    Dictionary<int, Dialog> channelDialogMap;
                    if (!s_clientDialogMap.TryGetValue(AgentID, out channelDialogMap )) {
                        s_clientDialogMap[AgentID] = channelDialogMap = new Dictionary<int, Dialog>();
                        Client.OnChatFromClient += OnChatFromClientAPI;
                    }
                
                    /***** Add Dialog to master map *****/
                    channelDialogMap[Channel] = this;
                }
            }
            
            /// <summary>
            /// Delivers the dialog message to the client.
            /// </summary>
            private void Deliver()
            {
                /***** Send Dialog message to agent *****/
                Client.SendDialog(objectname: MsgTitle,
                                  objectID: c_msgSourceObjectID, ownerID: c_msgSourceObjectOwnerID,
                                  ownerFirstName: c_msgHeaderWordOne, ownerLastName: c_msgHeaderWordTwo,
                                  msg: MsgBody, textureID: c_backgroundTextureID,
                                  ch: Channel, buttonlabels: ButtonResponses);
            }

            
            /// <summary>
            /// Catch chat from client and see if it is a response to a dialog message we've delivered.
            /// --- If not, consider purging old Dialogs.
            /// --- If it is on a channel for a Dialog for this user, validate that it's not an imposter.
            /// --- Call ProcessResponse on derived Dialog class
            /// Callback registerd in Dialog.Open()
            /// EVENT:
            ///     ChatFromClientEvent is triggered via ChatModule (or
            ///     substitutes thereof) when a chat message
            ///     from the client  comes in.
            /// </summary>
            /// <param name="sender">Sender of message</param>
            /// <param name="chat">message sent</param>
            protected static void OnChatFromClientAPI(Object sender, OSChatMessage chat)
            {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] OnChatFromClientAPI from:{0} chat:{1}", sender, chat);
                m_log.InfoFormat("[GLOEBITMONEYMODULE] OnChatFromClientAPI \n\tmessage:{0} \n\ttype: {1} \n\tchannel: {2} \n\tposition: {3} \n\tfrom: {4} \n\tto: {5} \n\tsender: {6} \n\tsenderObject: {7} \n\tsenderUUID: {8} \n\ttargetUUID: {9} \n\tscene: {10}", chat.Message, chat.Type, chat.Channel, chat.Position, chat.From, chat.To, chat.Sender, chat.SenderObject, chat.SenderUUID, chat.TargetUUID, chat.Scene);
                
                IClientAPI client = (IClientAPI) sender;
                
                /***** Verify that this is a message intended for us.  Otherwise, ignore or check to see if time to purge old dialogs *****/
                
                // Since we have to lock the map to look for a dialog with this channel, let's only proceed if the channel is within our range,
                // or we've reached our purge duration.
                if (chat.Channel < c_MinChannel || chat.Channel > c_MaxChannel) {
                    // Every so often, cleanup old dialog messages not yet deregistered.
                    if (s_lastPurgedOldDialogs.CompareTo(DateTime.UtcNow.AddHours(-6)) < 0) {
                        Dialog.PurgeOldDialogs();
                    }
                    // message is not for us, so exit
                    return;
                }
                
                Dictionary<int, Dialog> channelDialogDict;
                Dialog dialog = null;
                bool found = false;
                lock (s_clientDialogMap) {
                    if ( s_clientDialogMap.TryGetValue(client.AgentId, out channelDialogDict) ) {
                        found = channelDialogDict.TryGetValue(chat.Channel, out dialog);
                    }
                }
                if (!found) {
                    // message is not for us
                    return;
                }
                
                /***** Validate base Dialog response parameters *****/
                
                // Check defaults that should always be the same to ensure no one tried to impersonate our dialog response
                if (chat.SenderUUID != UUID.Zero || chat.TargetUUID != UUID.Zero || !String.IsNullOrEmpty(chat.From) || !String.IsNullOrEmpty(chat.To) || chat.Type != ChatTypeEnum.Region) {
                    m_log.WarnFormat("[GLOEBITMONEYMODULE] OnChatFromClientAPI Received message on Gloebit dialog channel:{0} which may be an attempted impersonation. SenderUUID:{1}, TargetUUID:{2}, From:{3} To:{4} Type: {5} Message:{6}", chat.Channel, chat.SenderUUID, chat.TargetUUID, chat.From, chat.To, chat.Type, chat.Message);
                    return;
                }
                
                // TODO: Should we check that chat.Sender/sender is IClientAPI as expected?
                // TODO: Should we check that Chat.Scene is scene we sent this to?
                
                /***** Process the response *****/
                
                m_log.InfoFormat("[GLOEBITMONEYMODULE] Dialog.OnChatFromClientAPI Processing Response: {0}", chat.Message);
                dialog.ProcessResponse(client, chat);

                /***** Handle Post Processing Cleanup of Dialog *****/
                
                dialog.Close();
                
            }
            
            /// <summary>
            /// Post processing cleanup of a Dialog.  Cleans everything that Open() set up.
            /// Removes dialog to our master map.
            /// If there are no other dialogs for this user, removes the dictionary of dialogs and deregisters the chat listener
            /// for this user.
            /// Always called after processing the response to a Dialog in order to clean up.
            /// Also called when a Dialog is purged without a response due to age.
            /// See Open() for setup that this cleans up.
            /// </summary>
            private void Close()
            {
                GloebitMoneyModule.m_log.InfoFormat("[GLOEBITMONEYMODULE] Dialog.Close AgentID:{0} Channel:{1}.", this.AgentID, this.Channel);
                
                bool foundChannelDialogMap = false;
                bool foundChannel = false;
                bool lastActiveDialog = false;
                
                /***** Remove Dialog from master map --- also deregister chat listener if no more active dialogs for this agent *****/
                
                lock (s_clientDialogMap) {
                    Dictionary<int, Dialog> channelDialogMap;
                    if (s_clientDialogMap.TryGetValue(this.AgentID, out channelDialogMap)) {
                        foundChannelDialogMap = true;
                        if (channelDialogMap.ContainsKey(this.Channel)) {
                            foundChannel = true;
                            
                            if (channelDialogMap.Count() == 1) {
                                // Delete channelDialogMap and Deregister chat listener as we're closing the only open dialog for this agent
                                lastActiveDialog = true;
                                this.Client.OnChatFromClient -= OnChatFromClientAPI;
                                s_clientDialogMap.Remove(this.AgentID);
                            } else {
                                // Remove this dialog from the map for this agent
                                channelDialogMap.Remove(this.Channel);
                            }
                        }
                    }
                }
                
                /***** Handle error/info messaging here so it is outside of the lock *****/
                if (!foundChannelDialogMap) {
                    GloebitMoneyModule.m_log.WarnFormat("[GLOEBITMONEYMODULE] Dialog.Close Called on dialog where agent is not in map -  AgentID:{0}.", this.AgentID);
                } else if (!foundChannel){
                    GloebitMoneyModule.m_log.WarnFormat("[GLOEBITMONEYMODULE] Dialog.Close Called on dialog where channel is not in map for agent -  AgentID:{0} Channel:{1}.", this.AgentID, this.Channel);
                } else {
                    GloebitMoneyModule.m_log.InfoFormat("[GLOEBITMONEYMODULE] Dialog.Close Removed dialog - AgentID:{0} Channel:{1}.", this.AgentID, this.Channel);
                    if (lastActiveDialog) {
                        GloebitMoneyModule.m_log.InfoFormat("[GLOEBITMONEYMODULE] Dialog.Close Removed agent dialog event listener - AgentID:{0}", this.AgentID);
                    }
                }
            }
            
            /// <summary>
            /// Called when user logs out to cleanup any active dialogs.
            /// If any dialogs are active, deletes dictionary and deregisters chat listener for this client
            /// </summary>
            /// <param name="client">Client which logged out</param>
            public static void DeregisterAgent(IClientAPI client)
            {
                UUID agentID = client.AgentId;
                GloebitMoneyModule.m_log.InfoFormat("[GLOEBITMONEYMODULE] Dialog.DeregisterAgent - AgentID:{0}.", agentID);
                bool foundChannelDialogMap = false;
                
                lock (s_clientDialogMap) {
                    if (s_clientDialogMap.ContainsKey(agentID)) {
                        foundChannelDialogMap = true;
                        client.OnChatFromClient -= OnChatFromClientAPI;
                        s_clientDialogMap.Remove(agentID);
                    }
                }
                if (!foundChannelDialogMap) {
                    GloebitMoneyModule.m_log.InfoFormat("[GLOEBITMONEYMODULE] Dialog.DeregisterAgent No listener - AgentID:{0}.", agentID);
                } else {
                    GloebitMoneyModule.m_log.InfoFormat("[GLOEBITMONEYMODULE] Dialog.DeregisterAgent Removed listener - AgentID:{0}.", agentID);
                }
            }
            
            /// <summary>
            /// Called when we receive a chat message from a client for whom we've registered a listener
            /// and m_lastTimeCleanedDialogs is greater than our purge duration (currently 6 hours).
            /// If any active Dialogs are older than ttl (currently 6 hours), calls Close() on those dialogs.
            /// Necessary because a user may not responsd or can ignore or block our messages without us knowing, and
            /// we do not want to add load to the OpenSim server by continuing to get chat events from that user.
            /// Assuming users log out reasonably frequently, this my be unnecessary.
            /// </summary>
            private static void PurgeOldDialogs()
            {
                // Let's avoid two purges running at the same time.
                if (Monitor.TryEnter(s_purgeLock)) {
                    try {
                        if (s_lastPurgedOldDialogs.CompareTo(DateTime.UtcNow.AddHours(-6)) < 0) {
                            // Time to purge.  Reset s_lastPurgedOldDialogs so no other thread will purge after the Monitor exists.
                            s_lastPurgedOldDialogs = DateTime.UtcNow;
                        } else {
                            // Not yet time.  Return
                            return;
                        }
                    } finally {
                        // Allow other threads access to this resource again.
                        Monitor.Exit(s_purgeLock);
                    }
                } else {
                    // another thread is making this check.  Return
                    return;
                }
                
                // If we've reached this point, then we have a single thread which has reset s_lastPurgedOldDialogs and is ready to purge.
                
                GloebitMoneyModule.m_log.InfoFormat("[GLOEBITMONEYMODULE] Dialog.PurgeOldDialogs.");
                
                List<Dialog> dialogsToPurge = new List<Dialog>();
                
                lock (s_clientDialogMap) {
                    foreach( KeyValuePair<UUID, Dictionary<int, Dialog>> kvp in s_clientDialogMap )
                    {
                        foreach (KeyValuePair<int, Dialog> idp in kvp.Value) {
                            if (idp.Value.cTime.CompareTo(DateTime.UtcNow.AddHours(-6)) < 0) {
                                dialogsToPurge.Add(idp.Value);
                            }
                        }
                    }
                }
                
                foreach( Dialog dialog in dialogsToPurge ) {
                    // If any of these have already been closed, we'll produce a WarnFormat log.
                    dialog.Close();
                }
            }
            
        };
        
        /// <summary>
        /// Class for asking a user to authorize or report a subscription (automated/unattended) payment
        /// triggered by a scripted object which attempted an automatic debit from its owner.
        /// Should be sent to a user whenever LLGiveMoney or LLTransferLindens causes a transaction request
        /// for a subscription (scripted object) for which the user hasn't already authorized from the Gloebit website.
        /// Upon response, we either send a fraud report or build a URL for the user to approve/decline a pending
        /// subscription authorization for this subscription (scripted object).
        /// To send this dialog to a user, use the following command
        /// --- Dialog.Send(new CreateSubscriptionAuthorizationDialog(<constructor params>))
        /// </summary>
        public class CreateSubscriptionAuthorizationDialog : Dialog
        {
            // Name of the agent to whom this dialog is being delivered
            public readonly string AgentName;    // Name of the agent we're sending the dialog to and requesting auths this subscription
            
            // Details of scripted object which caused this subscription creation
            public readonly UUID ObjectID;       // ID of object which attempted the auto debit.
            public readonly string ObjectName;   // name of object which attempted the auto debit.
            public readonly string ObjectDescription;
            
            // Details of attempted, failed transaction resulting in this create subscription authorization dialog
            public readonly UUID TransactionID;  // id of the auto debit transaciton which failed due to lack of authorization
            public readonly UUID PayeeID;        // ID of the agent receiving the proceeds
            public readonly string PayeeName;    // name of the agent receiving the proceeds
            public readonly int Amount;          // The amount of the auto-debit transaction
            public readonly UUID SubscriptionID; // The subscription id return by GloebitAPI.CreateSubscription
            
            // TODO: can these be static, or should we be passing in the m_api instead?
            public readonly GloebitAPI api;         // The GloebitAPI environment that is currently active
            public readonly Uri callbackBaseURI;    // The economyURL for the sim - used if we decide to create callbacks.
            
            
            // Create static variables here so we only need one string array
            private const string c_title = "Subscription Authorization Request (scripted object auto-debit)";
            private static readonly string[] c_buttons = new string[3] {"Authorize", "Ignore", "Report Fraud"};
            
            // Create variable we can format once in constructor to return for MsgBody
            private readonly string m_body;
            
            protected override string MsgTitle
            {
                get
                {
                    return c_title;
                }
            }
            protected override string MsgBody
            {
                get
                {
                    return m_body;
                }
            }
            protected override string[] ButtonResponses
            {
                get
                {
                    return c_buttons;
                }
            }
            
            /// <summary>
            /// Constructs a CreateSubscriptionAuthorizationDialog
            /// </summary>
            /// <param name="client">IClientAPI of agent that script attempted to auto-debit</param>
            /// <param name="agentID">UUID of agent that script attempted to auto-debit</param>
            /// <param name="agentName">String name of the OpenSim user who is being asked to authorize</param>
            /// <param name="objectID">UUID of object containing the script which attempted the auto-debit</param>
            /// <param name="objectDescription">Description of object containing the script which attempted the auto-debit</param>
            /// <param name="objectName">Name of object containing the script which attempted the auto-debit</param>
            /// <param name="transactionID">UUID of auto-debit transaction that failed due to lack of authorization</param>
            /// <param name="payeeID">UUID of the OpenSim user who is being paid by the object/script/subscription</param>
            /// <param name="payeeName">String name of the OpenSim user who is being paid by the object/script/subscription</param>
            /// <param name="amount">int amount of the failed transaction which triggered this authorization request</param>
            /// <param name="subscriptionID">UUID of subscription created/returnd by Gloebit and for which authorization is being requested</param>
            /// <param name="activeApi">GloebitAPI active for this GMM</param>
            /// <param name="appCallbackBaseURI">Base URI for any callbacks this request makes back into the app</param>
            public CreateSubscriptionAuthorizationDialog(IClientAPI client, UUID agentID, string agentName, UUID objectID, string objectName, string objectDescription, UUID transactionID, UUID payeeID, string payeeName, int amount, UUID subscriptionID, GloebitAPI activeApi, Uri appCallbackBaseURI) : base(client, agentID)
            {
                this.AgentName = agentName;
                
                this.ObjectID = objectID;
                this.ObjectName = objectName;
                this.ObjectDescription = objectDescription;
                
                this.TransactionID = transactionID;
                this.PayeeID = payeeID;
                this.PayeeName = payeeName;
                this.Amount = amount;
                this.SubscriptionID = subscriptionID;
                
                this.api = activeApi;
                this.callbackBaseURI = appCallbackBaseURI;
                
                this.m_body = String.Format("\nA payment was attempted by a scripted object you own.  To allow payments triggered by this object, you must authorize it from the Gloebit Website.\n\nObject:\n   {0}\n   {1}\nTo:\n   {2}\n   {3}\nAmount:\n   {4} Gloebits", ObjectName, ObjectID, PayeeName, PayeeID, Amount);
                
                //this.m_body = String.Format("\nAn auto-debit was attempted by an object which you have not yet authorized to auto-debit from the Gloebit Website.\n\nObject:\n   {0}\n   {1}\nTo:\n   {2}\n   {3}\nAmount:\n   {4} Gloebits", ObjectName, ObjectID, PayeeName, PayeeID, Amount);
                
                // TODO: what else do we need to track for handling auth or fraud reporting on response?
                
                // TODO: should we also save and double check all the region/grid/app info?
            }

            /// <summary>
            /// Processes the user response (click of button on dialog) to a CreateSubscriptionAuthorizationDialog.
            /// --- Ignore: does nothing.
            /// --- Report Fraud: sends fraud report to Gloebit
            /// --- Authorize: Creates pending authorization subscription for this user and the referenced subscription
            /// </summary>
            /// <param name="client">IClientAPI of sender of response</param>
            /// <param name="chat">response sent</param>
            protected override void ProcessResponse(IClientAPI client, OSChatMessage chat)
            {
                
                switch (chat.Message) {
                    case "Ignore":
                        // User actively ignored.  remove from our message listener
                        break;
                    case "Authorize":
                        // Create authorization
                        
                        string subscriptionIDStr = SubscriptionID.ToString();
                        string apiUrl = api.m_url.ToString();
                        
                        GloebitAPI.Subscription sub = GloebitAPI.Subscription.GetBySubscriptionID(subscriptionIDStr, apiUrl);
                        // TODO: Do we need to check if this is null?  Shouldn't happen.

                        // Get GloebitAPI.User for this agent
                        GloebitAPI.User user = GloebitAPI.User.Get(AgentID);
                        
                        // TODO: Shouldn't get here unless we have a token, but should we check again?
                        
                        // TODO: need to include transactionID, payeeID, payeeName and amount somehow
                        api.CreateSubscriptionAuthorization(sub, user, AgentName, callbackBaseURI, client);
                        
                        break;
                    case "Report Fraud":
                        // Report to Gloebit
                        // TODO: fire off fraud report to Gloebit
                        break;
                    default:
                        m_log.ErrorFormat("[GLOEBITMONEYMODULE] CreateSubscriptionAuthorizationDialog.ProcessResponse Received unexpected dialog response message:{0}", chat.Message);
                        break;
                }
            }

            
        };
        
        
        /// <summary>
        /// Class for asking a user to authorize or report a subscription (automated/unattended) payment
        /// triggered by a scripted object which attempted an automatic debit from its owner
        /// for which a pending SubscriptionAuthorizationa already exists.
        /// Should be sent to a user whenever LLGiveMoney or LLTransferLindens causes a transaction request
        /// for a subscription (scripted object) for which the user already has a pending authorization request on the Gloebit website.
        /// Upon response, we either send a fraud report or build a URL for the user to approve/decline the pending
        /// subscription authorization for this subscription (scripted object).
        /// To send this dialog to a user, use the following command
        /// --- Dialog.Send(new PendingSubscriptionAuthorizationDialog(<constructor params>))
        /// </summary>
        public class PendingSubscriptionAuthorizationDialog : Dialog
        {
            // Details of scripted object which caused this subscription creation
            public readonly UUID ObjectID;       // ID of object which attempted the auto debit.
            public readonly string ObjectName;   // name of object which attempted the auto debit.
            public readonly string ObjectDescription;
            
            // Details of attempted, failed transaction resulting in this create subscription authorization dialog
            public readonly UUID TransactionID;  // id of the auto debit transaciton which failed due to lack of authorization
            public readonly UUID PayeeID;        // ID of the agent receiving the proceeds
            public readonly string PayeeName;    // name of the agent receiving the proceeds
            public readonly int Amount;          // The amount of the auto-debit transaction
            public readonly UUID SubscriptionID; // The subscription id return by GloebitAPI.CreateSubscription
            
            // TODO: can these be static, or should we be passing in the m_api instead?
            public readonly GloebitAPI api;      // The GloebitAPI environment that is currently active
            public readonly Uri callbackBaseURI;      // The economyURL for the sim - used if we decide to create callbacks.
            
            // Name of the agent to whom this dialog is being delivered
            public readonly string AgentName;    // Name of the agent we're sending the dialog to and requesting auths this subscription
            
            // pending subscription authorization information
            public readonly UUID SubscriptionAuthorizationID;   // The subscription authorization id returned by the failed transaction.
            
            // Create static variables here so we only need one string array
            private const string c_title = "Pending Subscription Authorization Request (scripted object auto-debit)";
            private static readonly string[] c_buttons = new string[3] {"Respond", "Ignore", "Report Fraud"};
            
            // Create variable we can format once in constructor to return for MsgBody
            private readonly string m_body;
            
            protected override string MsgTitle
            {
                get
                {
                    return c_title;
                }
            }
            protected override string MsgBody
            {
                get
                {
                    return m_body;
                }
            }
            protected override string[] ButtonResponses
            {
                get
                {
                    return c_buttons;
                }
            }
            
            
            /// <summary>
            /// Constructs a PendingSubscriptionAuthorizationDialog
            /// </summary>
            /// <param name="client">IClientAPI of agent that script attempted to auto-debit</param>
            /// <param name="agentID">UUID of agent that script attempted to auto-debit</param>
            /// <param name="agentName">String name of the OpenSim user who is being asked to authorize</param>
            /// <param name="objectID">UUID of object containing the script which attempted the auto-debit</param>
            /// <param name="objectDescription">Description of object containing the script which attempted the auto-debit</param>
            /// <param name="objectName">Name of object containing the script which attempted the auto-debit</param>
            /// <param name="transactionID">UUID of auto-debit transaction that failed due to lack of authorization</param>
            /// <param name="payeeID">UUID of the OpenSim user who is being paid by the object/script/subscription</param>
            /// <param name="payeeName">String name of the OpenSim user who is being paid by the object/script/subscription</param>
            /// <param name="amount">int amount of the failed transaction which triggered this authorization request</param>
            /// <param name="subscriptionID">UUID of subscription created/returnd by Gloebit and for which authorization is being requested</param>
            /// <param name="subscriptionAuthorizationID">UUID of the pending subscription authorization returned by Gloebit with the failed transaction</param>
            /// <param name="activeApi">GloebitAPI active for this GMM</param>
            /// <param name="appCallbackBaseURI">Base URI for any callbacks this request makes back into the app</param>
            public PendingSubscriptionAuthorizationDialog(IClientAPI client, UUID agentID, string agentName, UUID objectID, string objectName, string objectDescription, UUID transactionID, UUID payeeID, string payeeName, int amount, UUID subscriptionID, UUID subscriptionAuthorizationID, GloebitAPI activeApi, Uri appCallbackBaseURI) : base(client, agentID)
            {
                this.AgentName = agentName;
                
                this.ObjectID = objectID;
                this.ObjectName = objectName;
                this.ObjectDescription = objectDescription;
                
                this.TransactionID = transactionID;
                this.PayeeID = payeeID;
                this.PayeeName = payeeName;
                this.Amount = amount;
                this.SubscriptionID = subscriptionID;
                this.SubscriptionAuthorizationID = subscriptionAuthorizationID;
                
                this.api = activeApi;
                this.callbackBaseURI = appCallbackBaseURI;
                
                this.m_body = String.Format("\nA payment was attempted by a scripted object you own.  To allow payments triggered by this object, you must authorize it from the Gloebit Website.  A pending authorization request for this object exists to which you have not yet responded.\n\nObject:\n   {0}\n   {1}\nTo:\n   {2}\n   {3}\nAmount:\n   {4} Gloebits", ObjectName, ObjectID, PayeeName, PayeeID, Amount);
                
                // TODO: what else do we need to track for handling auth or fraud reporting on response?
                
                // TODO: should we also save and double check all the region/grid/app info?
            }
            
            /// <summary>
            /// Processes the user response (click of button on dialog) to a PendingSubscriptionAuthorizationDialog.
            /// --- Ignore: does nothing.
            /// --- Report Fraud: sends fraud report to Gloebit
            /// --- Authorize: Delivers authorization link to user
            /// </summary>
            /// <param name="client">IClientAPI of sender of response</param>
            /// <param name="chat">response sent</param>
            protected override void ProcessResponse(IClientAPI client, OSChatMessage chat)
            {
                
                switch (chat.Message) {
                    case "Ignore":
                        // User actively ignored.  remove from our message listener
                        break;
                    case "Respond":
                        // Resend authorization link
                        
                        string subscriptionIDStr = SubscriptionID.ToString();
                        string apiUrl = api.m_url.ToString();
                        GloebitAPI.Subscription sub = GloebitAPI.Subscription.GetBySubscriptionID(subscriptionIDStr, apiUrl);
                        // TODO: Do we need to check if this is null?  Shouldn't happen.
                        
                        // Send Authorize URL
                        api.SendSubscriptionAuthorizationToClient(client, SubscriptionAuthorizationID.ToString(), sub);
                        
                        break;
                    case "Report Fraud":
                        // Report to Gloebit
                        // TODO: fire off fraud report to Gloebit
                        break;
                    default:
                        m_log.ErrorFormat("[GLOEBITMONEYMODULE] PendingSubscriptionAuthorizationDialog.ProcessResponse Received unexpected dialog response message:{0}", chat.Message);
                        break;
                }
            }
            
            
        };
        
        
        
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private const string SANDBOX_URL = "https://sandbox.gloebit.com/";
        private const string PRODUCTION_URL = "https://www.gloebit.com/";

        /// <summary>
        /// Where Stipends come from and Fees go to.
        /// </summary>
        // private UUID EconomyBaseAccount = UUID.Zero;

        private float EnergyEfficiency = 0f;

        private bool m_enabled = true;
        private bool m_sellEnabled = false;
        private GLBEnv m_environment = GLBEnv.None;
        private string m_keyAlias;
        private string m_key;
        private string m_secret;
        private string m_apiUrl;
        private Uri m_overrideBaseURI;
        private string m_gridnick = "unknown_grid";
        private string m_gridname = "unknown_grid_name";
        private Uri m_economyURL;

        private IConfigSource m_gConfig;

        /// <summary>
        /// Scenes by Region Handle
        /// </summary>
        private Dictionary<ulong, Scene> m_scenel = new Dictionary<ulong, Scene>();

        private int ObjectCount = 0;
        private int PriceEnergyUnit = 0;
        private int PriceGroupCreate = 0;
        private int PriceObjectClaim = 0;
        private float PriceObjectRent = 0f;
        private float PriceObjectScaleFactor = 0f;
        private int PriceParcelClaim = 0;
        private float PriceParcelClaimFactor = 0f;
        private int PriceParcelRent = 0;
        private int PricePublicObjectDecay = 0;
        private int PricePublicObjectDelete = 0;
        private int PriceRentLight = 0;
        private int PriceUpload = 0;
        private int TeleportMinPrice = 0;

        private float TeleportPriceExponent = 0f;


        private GloebitAPI m_api;

        /// <summary>
        /// Called on startup so the module can be configured.
        /// </summary>
        /// <param name="config">Configuration source.</param>
        public void Initialise(IConfigSource config)
        {
            m_log.Info ("[GLOEBITMONEYMODULE] Initialising.");
            m_gConfig = config;

            string[] sections = {"Startup", "Gloebit", "Economy", "GridInfoService"};
            foreach (string section in sections) {
                IConfig sec_config = m_gConfig.Configs[section];
            
                if (null == sec_config) {
                    m_log.WarnFormat("[GLOEBITMONEYMODULE] Config section {0} is missing. Skipping.", section);
                    continue;
                }
                ReadConfigAndPopulate(sec_config, section);
            }

            m_log.InfoFormat("[GLOEBITMONEYMODULE] Initialised. Gloebit enabled: {0}, GLBEnvironment: {1}, GLBApiUrl: {2} GLBKeyAlias {3}, GLBKey: {4}, GLBSecret {5}",
                m_enabled, m_environment, m_apiUrl, m_keyAlias, m_key, (m_secret == null ? "null" : "configured"));

            // TODO: I've added GLBEnv.Custom for testing.  Remove before we ship
            if(m_environment != GLBEnv.Sandbox && m_environment != GLBEnv.Production && m_environment != GLBEnv.Custom) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] Unsupported environment selected: {0}, disabling GloebitMoneyModule", m_environment);
                m_enabled = false;
            }

            if(m_enabled) {
                //string key = (m_keyAlias != null && m_keyAlias != "") ? m_keyAlias : m_key;
                m_api = new GloebitAPI(m_key, m_keyAlias, m_secret, new Uri(m_apiUrl), this, this);
                GloebitUserData.Initialise(m_gConfig.Configs["DatabaseService"]);
                GloebitTransactionData.Initialise(m_gConfig.Configs["DatabaseService"]);
                GloebitSubscriptionData.Initialise(m_gConfig.Configs["DatabaseService"]);
            }
        }

        /// <summary>
        /// Parse Standard MoneyModule Configuration
        /// </summary>
        /// <param name="config"></param>
        /// <param name="section"></param>
        private void ReadConfigAndPopulate(IConfig config, string section)
        {
            if (section == "Startup") {
                m_enabled = (config.GetString("economymodule", "Gloebit") == "Gloebit");
                if(m_enabled) {
                    m_log.Info ("[GLOEBITMONEYMODULE] selected as economymodule.");
                }
            }

            if (section == "Gloebit") {
                bool enabled = config.GetBoolean("Enabled", false);
                m_enabled = m_enabled && enabled;
                if (!enabled) {
                    m_log.Info ("[GLOEBITMONEYMODULE] Not enabled. (to enable set \"Enabled = true\" in [Gloebit])");
                    return;
                }
                string envString = config.GetString("GLBEnvironment", "sandbox");
                switch(envString) {
                    case "sandbox":
                        m_environment = GLBEnv.Sandbox;
                        m_apiUrl = SANDBOX_URL;
                        break;
                    case "production":
                        m_environment = GLBEnv.Production;
                        m_apiUrl = PRODUCTION_URL;
                        break;
                    case "custom":
                        m_environment = GLBEnv.Custom;
                        m_apiUrl = config.GetString("GLBApiUrl", SANDBOX_URL);
                        string overrideBaseURIStr = config.GetString("GLBCallbackBaseURI", null);
                        if(overrideBaseURIStr != null) {
                            m_overrideBaseURI = new Uri(overrideBaseURIStr);
                        }
                        m_log.Warn("[GLOEBITMONEYMODULE] GLBEnvironment \"custom\" unsupported, things will probably fail later");
                        break;
                    default:
                        m_environment = GLBEnv.None;
                        m_apiUrl = null;
                        m_log.WarnFormat("[GLOEBITMONEYMODULE] GLBEnvironment \"{0}\" unrecognized, setting to None", envString); 
                        break;
                }
                m_keyAlias = config.GetString("GLBKeyAlias", null);
                m_key = config.GetString("GLBKey", null);
                m_secret = config.GetString("GLBSecret", null);
            }

            if (section == "Economy") {
                PriceEnergyUnit = config.GetInt("PriceEnergyUnit", 100);
                PriceObjectClaim = config.GetInt("PriceObjectClaim", 10);
                PricePublicObjectDecay = config.GetInt("PricePublicObjectDecay", 4);
                PricePublicObjectDelete = config.GetInt("PricePublicObjectDelete", 4);
                PriceParcelClaim = config.GetInt("PriceParcelClaim", 1);
                PriceParcelClaimFactor = config.GetFloat("PriceParcelClaimFactor", 1f);
                PriceUpload = config.GetInt("PriceUpload", 0);
                PriceRentLight = config.GetInt("PriceRentLight", 5);
                TeleportMinPrice = config.GetInt("TeleportMinPrice", 2);
                TeleportPriceExponent = config.GetFloat("TeleportPriceExponent", 2f);
                EnergyEfficiency = config.GetFloat("EnergyEfficiency", 1);
                PriceObjectRent = config.GetFloat("PriceObjectRent", 1);
                PriceObjectScaleFactor = config.GetFloat("PriceObjectScaleFactor", 10);
                PriceParcelRent = config.GetInt("PriceParcelRent", 1);
                PriceGroupCreate = config.GetInt("PriceGroupCreate", -1);
                m_sellEnabled = config.GetBoolean("SellEnabled", false);
            }

            if (section == "GridInfoService") {
                m_gridnick = config.GetString("gridnick", m_gridnick);
                m_gridname = config.GetString("gridname", m_gridname);
                m_economyURL = new Uri(config.GetString("economy"));

                // TODO(brad) - figure out how to install a global economy url handler
                // in robust mode.  do we need to make a separate addon for Robust.exe?
                if(m_economyURL == null) {
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] GridInfoService.economy setting MUST be configured!");
                }
            }
        }

        public void AddRegion(Scene scene)
        {
            if (m_enabled)
            {
                m_log.Info("[GLOEBITMONEYMODULE] region added");
                scene.RegisterModuleInterface<IMoneyModule>(this);
                IHttpServer httpServer = MainServer.Instance;

                lock (m_scenel)
                {
                    if (m_scenel.Count == 0)
                    {
                        // XMLRPCHandler = scene;

                        // To use the following you need to add:
                        // -helperuri <ADDRESS TO HERE OR grid MONEY SERVER>
                        // to the command line parameters you use to start up your client
                        // This commonly looks like -helperuri http://127.0.0.1:9000/

                       
                        // Local Server..  enables functionality only.
                        httpServer.AddXmlRPCHandler("getCurrencyQuote", quote_func);
                        httpServer.AddXmlRPCHandler("buyCurrency", buy_func);
                        httpServer.AddXmlRPCHandler("preflightBuyLandPrep", preflightBuyLandPrep_func);
                        httpServer.AddXmlRPCHandler("buyLandPrep", landBuy_func);

                        // Register callback for 2nd stage of OAuth2 Authorization_code_grant
                        httpServer.AddHTTPHandler("/gloebit/auth_complete", authComplete_func);
                        
                        // Register callback for asset enact, consume, & cancel holds transaction parts
                        httpServer.AddHTTPHandler("/gloebit/transaction", transactionState_func);
                       
                        // Used by the redirect-to parameter to GloebitAPI.Purchase.  Called when a user has finished purchasing gloebits
                        httpServer.AddHTTPHandler("/gloebit/buy_complete", buyComplete_func);
                    }

                    if (m_scenel.ContainsKey(scene.RegionInfo.RegionHandle))
                    {
                        m_scenel[scene.RegionInfo.RegionHandle] = scene;
                    }
                    else
                    {
                        m_scenel.Add(scene.RegionInfo.RegionHandle, scene);
                    }
                }

                scene.EventManager.OnNewClient += OnNewClient;
                scene.EventManager.OnMoneyTransfer += OnMoneyTransfer;
                scene.EventManager.OnClientClosed += ClientClosed;
                scene.EventManager.OnAvatarEnteringNewParcel += AvatarEnteringParcel;
                scene.EventManager.OnMakeChildAgent += MakeChildAgent;
                scene.EventManager.OnValidateLandBuy += ValidateLandBuy;
                scene.EventManager.OnLandBuy += processLandBuy;
                
            }
        }

        public void RemoveRegion(Scene scene)
        {
            lock (m_scenel)
            {
                m_scenel.Remove(scene.RegionInfo.RegionHandle);
            }
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
            m_enabled = false;
        }

        public Type ReplaceableInterface {
            get { return null; }
        }

        public string Name {
            get { return "GloebitMoneyModule"; }
        }


        #region IMoneyModule Members

        public bool ObjectGiveMoney(UUID objectID, UUID fromID, UUID toID, int amount)
        {
            string description = String.Format("Object {0} pays {1}", resolveObjectName(objectID), resolveAgentName(toID));
            m_log.InfoFormat("[GLOEBITMONEYMODULE] ******************ObjectGiveMoney {0}", description);
            
            SceneObjectPart part = null;
            string regionname = "";
            string regionID = "";
            
            // TODO: is there a better way to get the scene and part?
            // Are the object and payee always in the same scene?
            // Is the payee even necessarily online?
            Scene s = LocateSceneClientIn(toID);
            if (s != null) {
                part = s.GetSceneObjectPart(objectID);
                regionname = s.RegionInfo.RegionName;
                regionID = s.RegionInfo.RegionID.ToString();
            }
            
            // Check subscription table.  If not exists, send create call to Gloebit
            m_log.InfoFormat("[GLOEBITMONEYMODULE] ObjectGiveMoney - looking for local subscription");
            GloebitAPI.Subscription sub = GloebitAPI.Subscription.Get(objectID, m_key, m_apiUrl);
            if (sub == null) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] ObjectGiveMoney - creating local subscription fo {0}", part.Name);
                // Create local sub
                sub = GloebitAPI.Subscription.Init(objectID, m_key, m_apiUrl, part.Name, part.Description);
            }
            if (sub.SubscriptionID == UUID.Zero) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] ObjectGiveMoney - SID is ZERO -- calling GloebitAPI Create Subscription");
                
                // Message to user that we are creating the subscription.
                alertUsersSubscriptionTransactionFailedForSubscriptionCreation(fromID, toID, amount, sub);
                
                // call api to have Gloebit create
                m_api.CreateSubscription(sub, m_economyURL);
                
                // return false so this t
                return false;
            }
            
            // Check that user has authed Gloebit and token is on file.
            GloebitAPI.User payerUser = GloebitAPI.User.Get(fromID);
            if (payerUser != null && String.IsNullOrEmpty(payerUser.GloebitToken)) {
                // send message asking to auth Gloebit.
                alertUsersSubscriptionTransactionFailedForGloebitAuthorization(fromID, toID, amount, sub);
                return false;
            }
            
            // Checks done.  Ready to build and submit transaction.
            
            OSDMap descMap = buildBaseTransactionDescMap(regionname, regionID, "ObjectGiveMoney", part);
            
            // TODO: we are assuming this is the fromID in messaging.  Which is right?  Fix.
            IClientAPI activeClient = LocateClientObject(toID);
            ////string actionStr = String.Format("User Gifted Funds From Object: {0}\nOwned By: {1}", part.Name, resolveAgentName(fromID));

            
            GloebitAPI.Transaction txn = buildTransaction(transactionType: TransactionType.OBJECT_PAYS_USER,
                                                          payerID: fromID, payeeID: toID, amount: amount, subscriptionID: sub.SubscriptionID,
                                                          partID: objectID, partName: part.Name, partDescription: part.Description,
                                                          categoryID: UUID.Zero, localID: 0, saleType: 0);
            // TODO: should we store "transaction description" with the Transaction?
            
            bool give_result = submitTransaction(txn, description, descMap, activeClient);

            // TODO - move this to a proper execute callback
            BalanceUpdate(fromID, toID, give_result, description);

            return give_result;
        }

        public int GetBalance(UUID agentID)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GetBalance for agent {0}", agentID);
            return 0;
        }

        // Please do not refactor these to be just one method
        // Existing implementations need the distinction
        //
        public bool UploadCovered(UUID agentID, int amount)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] UploadCovered for agent {0}", agentID);
            return true;
        }
        public bool AmountCovered(UUID agentID, int amount)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] AmountCovered for agent {0}", agentID);
            return true;
        }

        // Please do not refactor these to be just one method
        // Existing implementations need the distinction
        //
        public void ApplyCharge(UUID agentID, int amount, MoneyTransactionType type, string extraData)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] ApplyCharge for agent {0} with extraData {1}", agentID, extraData);
        }

        public void ApplyCharge(UUID agentID, int amount, MoneyTransactionType type)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] ApplyCharge for agent {0}", agentID);
        }

        public void ApplyUploadCharge(UUID agentID, int amount, string text)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] ApplyUploadCharge for agent {0}", agentID);
        }

        // property to store fee for uploading assets
        // NOTE: fees are not applied to meshes right now because functionality to compute prim equivalent has not been written
        public int UploadCharge
        {
            get { return 0; }
        }

        // property to store fee for creating a group
        public int GroupCreationCharge
        {
            get { return 0; }
        }

//#pragma warning disable 0067
        public event ObjectPaid OnObjectPaid;
//#pragma warning restore 0067

        #endregion // IMoneyModule members


        /// <summary>
        /// New Client Event Handler
        /// </summary>
        /// <param name="client"></param>
        private void OnNewClient(IClientAPI client)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] OnNewClient for {0}", client.AgentId);

            // Subscribe to Money messages
            client.OnEconomyDataRequest += EconomyDataRequestHandler;
            client.OnMoneyBalanceRequest += SendMoneyBalance;
            client.OnRequestPayPrice += requestPayPrice;
            client.OnObjectBuy += ObjectBuy;
            client.OnLogout += ClientLoggedOut;

            GloebitAPI.User user = GloebitAPI.User.Get(client.AgentId);
            if(user != null && !String.IsNullOrEmpty(user.GloebitToken)) {
                m_api.GetBalance(user);
            } else {
                m_api.Authorize(client, BaseURI);
            }
        }
        
        /// <summary>
        /// Build a GloebitAPI.Transaction for a specific TransactionType.  This Transaction will be:
        /// --- persistently stored
        /// --- used for submitting to Gloebit via the TransactU2U endpoint via submitTransaction(),
        /// --- used for processing transact enact/consume/cancel callbacks to handle any other OpenSim components of the transaction(such as object delivery),
        /// --- used for tracking/reporting/analysis
        /// </summary>
        /// <param name="transactionType">enum from OpenSim defining the type of transaction (buy object, pay object, pay user, object pays user, etc).  This will not affect how Gloebit process the monetary component of a transaction, but is useful for easily varying how OpenSim should handle processing once funds are transfered.</param>
        /// <param name="payerID">OpenSim UUID of agent sending gloebits.</param>
        /// <param name="payeeID">OpenSim UUID of agent receiving gloebits</param>
        /// <param name="amount">Amount of gloebits being transferred.</param>
        /// <param name="subscriptionID">UUID of subscription for automated transactions (Object pays user).  Otherwise UUID.Zero.</param>
        /// <param name="partID">UUID of the object, when transaciton involves an object.  UUID.Zero otherwise.</param>
        /// <param name="partName">string name of the object, when transaciton involves an object.  null otherwise.</param>
        /// <param name="partDescription">string description of the object, when transaciton involves an object.  String.Empty otherwise.</param>
        /// <param name="categoryID">UUID of folder in object used when transactionType is ObjectBuy and saleType is copy.  UUID.Zero otherwise.  Required by IBuySellModule.</param>
        /// <param name="localID">uint region specific id of object used when transactionType is ObjectBuy.  0 otherwise.  Required by IBuySellModule.</param>
        /// <param name="saleType">int differentiating between orginal, copy or contents for ObjectBuy.  Required by IBuySellModule to process delivery.</param>
        /// <returns>GloebitAPI.Transaction created. if successful.</returns>
        private GloebitAPI.Transaction buildTransaction(TransactionType transactionType, UUID payerID, UUID payeeID, int amount, UUID subscriptionID, UUID partID, string partName, string partDescription, UUID categoryID, uint localID, int saleType)
        {
            // Create a transaction ID
            UUID transactionID = UUID.Random();
            
            // Get user names
            string payerName = resolveAgentName(payerID);
            string payeeName = resolveAgentName(payeeID);
            
            // set up defaults
            bool isSubscriptionDebit = false;
            string transactionTypeString = String.Empty;
            //subscriptionID = UUID.Zero;
            
            switch (transactionType) {
                case TransactionType.USER_BUYS_OBJECT:
                    // 5000 - ObjectBuy
                    transactionTypeString = "User Buys Object";
                    // This is the only type which requires categoryID, localID, and saleType
                    break;
                case TransactionType.USER_PAYS_USER:
                    // 5001 - OnMoneyTransfer - Pay User
                    transactionTypeString = "User Pays User";
                    // This is the only type which doesn't include a partID, partName or partDescription since no object is involved.
                    break;
                case TransactionType.USER_PAYS_OBJECT:
                    // 5008 - OnMoneyTransfer - Pay Object
                    transactionTypeString = "User Pays Object";
                    break;
                case TransactionType.OBJECT_PAYS_USER:
                    // 5009 - ObjectGiveMoney
                    transactionTypeString = "Object Pays User";
                    isSubscriptionDebit = true;
                    // TODO: should I get the subscription ID here instead of passing it in?
                    // TODO: what to do if subscriptionID is zero?
                    break;
                default:
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] buildTransaction failed --- unknown transaction type: {0}", transactionType);
                    // TODO: should we throw an exception?  return null?  just continue?
                    break;
            }
            
            GloebitAPI.Transaction txn = GloebitAPI.Transaction.Init(transactionID, payerID, payerName, payeeID, payeeName, amount, (int)transactionType, transactionTypeString, isSubscriptionDebit, subscriptionID, partID, partName, partDescription, categoryID, localID, saleType);
            return txn;
        }
        
        
        /// <summary>
        /// Submits a GloebitAPI.Transaction to gloebit for processing and provides any necessary feedback to user/platform.
        /// --- Must call buildTransaction() to create argument 1.
        /// --- Must call buildBaseTransactionDescMap() to create argument 2.
        /// </summary>
        /// <param name="txn">GloebitAPI.Transaction created from buildTransaction().  Contains vital transaction details.</param>
        /// <param name="description">Description of transaction for transaction history reporting.</param>
        /// <param name="descMap">Map of platform, location & transaction descriptors for tracking/querying and transaciton history details.  For more details, see buildTransactionDescMap helper function.</param>
        /// <param name="activeClient">Used solely for sending transaction status messages to OpenSim user who caused the transaction.</param>
        /// <param name="actionStr">String describing the type of transaction and who/what it is with.  Used solely for sending transaction status messages to OpenSim user who caused the transaction.</param>
        /// <returns>
        /// true if async transactU2U web request was built and submitted successfully; false if failed to submit request.
        /// If true:
        /// --- IAsyncEndpointCallback transactU2UCompleted should eventually be called with additional details on state of request.
        /// --- IAssetCallback processAsset[Enact|Consume|Cancel]Hold may eventually be called dependent upon processing.
        /// </returns>
        private bool submitTransaction(GloebitAPI.Transaction txn, string description, OSDMap descMap, IClientAPI activeClient)
        {
            // TODO: remove actionStr from argument comments above.
            m_log.InfoFormat("[GLOEBITMONEYMODULE] submitTransaction Txn: {0}, from {1} to {2}, for amount {3}, transactionType: {4}, description: {5}", txn.TransactionID, txn.PayerID, txn.PayeeID, txn.Amount, txn.TransactionType, description);
            
            // TODO: move details into args for func.
            /*string amountStr = String.Format("Amount: {0} gloebits", txn.Amount);
            string descStr = String.Format("Description: {0}", description);
            string idStr = String.Format("Transaction ID: {0}", txn.TransactionID);*/
            alertUsersTransactionBegun(txn, activeClient, null, description);
            /*
            if (activeClient != null) {
                string amountStr = String.Format("Amount: {0} gloebits", txn.Amount);
                string descStr = String.Format("Description: {0}", description);
                string idStr = String.Format("Transaction ID: {0}", txn.TransactionID);
                activeClient.SendAlertMessage(String.Format("Gloebit: Submitting transaction request.\n{0}\n{1}\n{2}\n{3}", actionStr, amountStr, descStr, idStr));
            }
            */
            
            // TODO: Should we wrap TransactU2U or request.BeginGetResponse in Try/Catch?
            // TODO: Should we return IAsyncResult in addition to bool on success?  May not be necessary since we've created an asyncCallback interface,
            //       but could make it easier for app to force synchronicity if desired.
            bool result = m_api.TransactU2U(GloebitAPI.User.Get(txn.PayerID), txn.PayerName, GloebitAPI.User.Get(txn.PayeeID), txn.PayeeName, resolveAgentEmail(txn.PayeeID), txn.Amount, description, txn, txn.TransactionID, descMap, BaseURI);
            
            if (!result) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] submitTransaction failed to create HttpWebRequest in GloebitAPI.TransactU2U");
                /*if (activeClient != null) {
                    activeClient.SendAlertMessage(String.Format("Gloebit: Transaction Failed.\nRegion Failed to properly create and send request to Gloebit.  Please try again.\nTransaction ID: {0}", txn.TransactionID));
                }*/
                alertUsersTransactionFailed(txn, GloebitAPI.TransactionStage.SUBMIT, String.Empty);
            } else {
                /*if (activeClient != null) {
                    activeClient.SendAlertMessage(String.Format("Gloebit: Transaction Successfully submitted to Gloebit Service.\nTransaction ID: {0}", txn.TransactionID));
                }*/
                alertUsersTransactionStageCompleted(txn, GloebitAPI.TransactionStage.SUBMIT, String.Empty);
            }
            
            // TODO: Should we be returning true before Transact completes successfully now that this is async???
            return result;
        }
        

        /// <summary>
        /// Sends the the stored money balance to the client
        /// </summary>
        /// <param name="client"></param>
        /// <param name="agentID"></param>
        /// <param name="SessionID"></param>
        /// <param name="TransactionID"></param>
        private void SendMoneyBalance(IClientAPI client, UUID agentID, UUID SessionID, UUID TransactionID)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] SendMoneyBalance request from {0} about {1} for transaction {2}", client.AgentId, agentID, TransactionID);

            if (client.AgentId == agentID && client.SessionId == SessionID)
            {
                int returnfunds = 0;
                double realBal = 0.0;

                try
                {
                    realBal = GetFundsForAgentID(agentID);
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] SendMoneyBalance Failure, Exception:{0}",e.Message);
                    client.SendAlertMessage(e.Message + " ");
                }
                
                // Get balance rounded down (may not be int for merchants)
                returnfunds = (int)realBal;
                client.SendMoneyBalance(TransactionID, true, new byte[0], returnfunds, 0, UUID.Zero, false, UUID.Zero, false, 0, String.Empty);
            }
            else
            {
                m_log.WarnFormat("[GLOEBITMONEYMODULE] SendMoneyBalance - Unable to send money balance");
                client.SendAlertMessage("Unable to send your money balance to you!");
            }
        }

        private SceneObjectPart findPrim(UUID objectID)
        {
            lock (m_scenel)
            {
                foreach (Scene s in m_scenel.Values)
                {
                    SceneObjectPart part = s.GetSceneObjectPart(objectID);
                    if (part != null)
                    {
                        return part;
                    }
                }
            }
            return null;
        }

        private string resolveObjectName(UUID objectID)
        {
            SceneObjectPart part = findPrim(objectID);
            if (part != null)
            {
                return part.Name;
            }
            return String.Empty;
        }

        private string resolveAgentName(UUID agentID)
        {
            // try avatar username surname
            Scene scene = GetAnyScene();
            UserAccount account = scene.UserAccountService.GetUserAccount(scene.RegionInfo.ScopeID, agentID);
            if (account != null)
            {
                string avatarname = account.FirstName + " " + account.LastName;
                return avatarname;
            }
            else
            {
                m_log.ErrorFormat(
                    "[GLOEBITMONEYMODULE]: Could not resolve user {0}", 
                    agentID);
            }
            
            return String.Empty;
        }
        
        // Possible that this is automatic as firstname=FN.LN and lastname=@home_uri
        // If not set through account service, than may change resolveAgentName to use umModule.GetUserName(UUID) which should work.
        // Or, alternatively, for better tracking of unique avatars across all OpenSim Hypergrid, we could try to discover
        // the home_uri when user is on home grid, and turn name into same format as a foreign user.
        // TODO: consider adding user's homeURL to tracked data on Gloebit --- might help spot grids with user accounts we should blacklist.
/*
        private string resolveAgentNameAtHome(UUID agentID)
        {
            // try avatar username surname
            Scene scene = GetAnyScene();
            UserAccount account = scene.UserAccountService.GetUserAccount(scene.RegionInfo.ScopeID, agentID);
            IUserManagement umModule = scene.RequestModuleInterface<IUserManagement>();
            
            m_log.InfoFormat("[GLOEBITMONEYMODULE]: resolveAgentNameAtHome\n GetUserName:{0} \nGetUserHomeURL:{1} \nGetUserUUI:{2}",
                              umModule.GetUserName(agentID), umModule.GetUserHomeURL(agentID), umModule.GetUserUUI(agentID));

            
            if (account != null && umModule != null)
            {
                string avatarname = account.FirstName + " " + account.LastName + " @" + umModule.GetUserHomeURL(agentID);
                return avatarname;
            }
            else
            {
                m_log.ErrorFormat(
                                  "[GLOEBITMONEYMODULE]: Could not resolve user {0}",
                                  agentID);
            }
            return String.Empty;
        }
*/
        private string resolveAgentEmail(UUID agentID)
        {
            // try avatar username surname
            Scene scene = GetAnyScene();
            UserAccount account = scene.UserAccountService.GetUserAccount(scene.RegionInfo.ScopeID, agentID);
            if (account != null)
            {
                return account.Email;
            }
            else
            {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE]: Could not resolve user {0}", agentID);
            }

            return String.Empty;
        }

        private void BalanceUpdate(UUID senderID, UUID receiverID, bool transactionresult, string description)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] BalanceUpdate from {0} to {1}", senderID, receiverID);
            IClientAPI sender = LocateClientObject(senderID);
            IClientAPI receiver = LocateClientObject(receiverID);

            if (senderID != receiverID)
            {
                if (sender != null)
                {
                    int senderReturnFunds = (int)GetFundsForAgentID (senderID);
                    sender.SendMoneyBalance(UUID.Random(), transactionresult, Utils.StringToBytes(description), senderReturnFunds, 0, UUID.Zero, false, UUID.Zero, false, 0, String.Empty);
                }

                if (receiver != null)
                {
                    int receiverReturnFunds = (int)GetFundsForAgentID (receiverID);
                    receiver.SendMoneyBalance(UUID.Random(), transactionresult, Utils.StringToBytes(description), receiverReturnFunds, 0, UUID.Zero, false, UUID.Zero, false, 0, String.Empty);
                }
            }
        }

        #region Standalone box enablers only

        private XmlRpcResponse quote_func(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            Hashtable requestData = (Hashtable) request.Params[0];

            string agentIdStr = requestData["agentId"] as string;
            UUID agentId = UUID.Parse(agentIdStr);
            UUID sessionId = UUID.Parse(requestData["secureSessionId"] as string);
            int amount = (int) requestData["currencyBuy"];

            m_log.InfoFormat("[GLOEBITMONEYMODULE] quote_func agentId: {0} sessionId: {1} currencyBuy: {2}", agentId, sessionId, amount);
            // foreach(DictionaryEntry e in requestData) { m_log.InfoFormat("{0}: {1}", e.Key, e.Value); }

            XmlRpcResponse returnval = new XmlRpcResponse();
            Hashtable quoteResponse = new Hashtable();
            Hashtable currencyResponse = new Hashtable();

            currencyResponse.Add("estimatedCost", amount / 2);
            currencyResponse.Add("currencyBuy", amount);

            quoteResponse.Add("success", true);
            quoteResponse.Add("currency", currencyResponse);

            // TODO - generate a unique confirmation token
            quoteResponse.Add("confirm", "asdfad9fj39ma9fj");

            GloebitAPI.User u = GloebitAPI.User.Get(agentId);
            if (String.IsNullOrEmpty(u.GloebitToken)) {
                IClientAPI user = LocateClientObject(agentId);
                m_api.Authorize(user, m_economyURL);
            }

            returnval.Value = quoteResponse;
            return returnval;
        }

        private XmlRpcResponse buy_func(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            Hashtable requestData = (Hashtable) request.Params[0];
            UUID agentId = UUID.Parse(requestData["agentId"] as string);
            string confirm = requestData["confirm"] as string;
            int currencyBuy = (int) requestData["currencyBuy"];
            int estimatedCost = (int) requestData["estimatedCost"];
            string secureSessionId = requestData["secureSessionId"] as string;

            // currencyBuy:viewerMinorVersion:secureSessionId:viewerBuildVersion:estimatedCost:confirm:agentId:viewerPatchVersion:viewerMajorVersion:viewerChannel:language
 
            m_log.InfoFormat("[GLOEBITMONEYMODULE] buy_func params {0}", String.Join(":", requestData.Keys.Cast<String>()));
            m_log.InfoFormat("[GLOEBITMONEYMODULE] buy_func agentId {0} confirm {1} currencyBuy {2} estimatedCost {3} secureSessionId {4}",
                agentId, confirm, currencyBuy, estimatedCost, secureSessionId);

            GloebitAPI.User u = GloebitAPI.User.Get(agentId);
            Uri url = m_api.BuildPurchaseURI(m_economyURL, u);
            string message = String.Format("Unfortunately we cannot yet sell Gloebits directly in the viewer.  Please visit {0} to buy Gloebits.", url);

            XmlRpcResponse returnval = new XmlRpcResponse();
            Hashtable returnresp = new Hashtable();
            returnresp.Add("success", false);
            returnresp.Add("errorMessage", message);
            returnresp.Add("errorUrl", url);
            returnval.Value = returnresp;
            return returnval;
        }

        private XmlRpcResponse preflightBuyLandPrep_func(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] preflightBuyLandPrep_func");
            XmlRpcResponse ret = new XmlRpcResponse();
            Hashtable retparam = new Hashtable();
            Hashtable membershiplevels = new Hashtable();
            ArrayList levels = new ArrayList();
            Hashtable level = new Hashtable();
            level.Add("id", "00000000-0000-0000-0000-000000000000");
            level.Add("description", "some level");
            levels.Add(level);
            //membershiplevels.Add("levels",levels);

            Hashtable landuse = new Hashtable();
            landuse.Add("upgrade", false);
            landuse.Add("action", "http://invaliddomaininvalid.com/");

            Hashtable currency = new Hashtable();
            currency.Add("estimatedCost", 0);

            Hashtable membership = new Hashtable();
            membershiplevels.Add("upgrade", false);
            membershiplevels.Add("action", "http://invaliddomaininvalid.com/");
            membershiplevels.Add("levels", membershiplevels);

            retparam.Add("success", true);
            retparam.Add("currency", currency);
            retparam.Add("membership", membership);
            retparam.Add("landuse", landuse);
            retparam.Add("confirm", "asdfajsdkfjasdkfjalsdfjasdf");

            ret.Value = retparam;

            return ret;
        }

        private XmlRpcResponse landBuy_func(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] landBuy_func");
            XmlRpcResponse ret = new XmlRpcResponse();
            Hashtable retparam = new Hashtable();
            // Hashtable requestData = (Hashtable) request.Params[0];

            // UUID agentId = UUID.Zero;
            // int amount = 0;
           
            retparam.Add("success", true);
            ret.Value = retparam;

            return ret;
        }
        
        /*********************************************************/
        /*** GloebitAPI Required HTTP Callback Entrance Points ***/
        /*********************************************************/

        /// <summary>
        /// Registered to the redirectURI from GloebitAPI.Authorize.  Called when a user approves authorization.
        /// Enacts the GloebitAPI.AccessToken endpoint to exchange the auth_code for the token.
        /// </summary>
        /// <param name="requestData">response data from GloebitAPI.Authorize</param>
        private Hashtable authComplete_func(Hashtable requestData) {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] authComplete_func");
            foreach(DictionaryEntry e in requestData) { m_log.InfoFormat("{0}: {1}", e.Key, e.Value); }

            string agentId = requestData["agentId"] as string;
            string code = requestData["code"] as string;

            m_api.ExchangeAccessToken(LocateClientObject(UUID.Parse(agentId)), code, BaseURI);

            m_log.InfoFormat("[GLOEBITMONEYMODULE] authComplete_func started ExchangeAccessToken");

            // TODO: call SendMoneyBalance(IClientAPI client, UUID agentID, UUID SessionID, UUID TransactionID) to update user balance.

            // TODO: How do we wait until complete to send this response and update balance?

            // TODO: call SendMoneyBalance(IClientAPI client, UUID agentID, UUID SessionID, UUID TransactionID) to update user balance.
            Hashtable response = new Hashtable();
            response["int_response_code"] = 200;
            response["str_response_string"] = "<html><head><title>Gloebit authorized</title></head><body><h2>Gloebit authorized</h2>Thank you for authorizing Gloebit.  You may now close this window.</body></html>";
            response["content_type"] = "text/html";
            return response;
        }
        
        /// <summary>
        /// Registered to the enactHoldURI, consumeHoldURI and cancelHoldURI from GloebitAPI.Transaction.
        /// Called by the Gloebit transaction processor.
        /// Enacts, cancels, or consumes the GloebitAPI.Asset.
        /// Response of true certifies that the Asset transaction part has been processed as requested.
        /// Response of false alerts transaction processor that asset failed to process as requested.
        /// Additional data can be returned about failures, specifically whether or not to retry.
        /// </summary>
        /// <param name="requestData">GloebitAPI.Asset enactHoldURI, consumeHoldURI or cancelHoldURI query arguments tying this callback to a specific Asset.</param>
        /// <returns>Web respsponse including JSON array of one or two elements.  First element is bool representing success state of call.  If first element is false, the second element is a string providing the reason for failure.  If the second element is "pending", then the transaction processor will retry.  All other reasons are considered permanent failure.</returns>
        private Hashtable transactionState_func(Hashtable requestData) {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] transactionState_func **************** Got Callback");
            foreach(DictionaryEntry e in requestData) { m_log.InfoFormat("{0}: {1}", e.Key, e.Value); }
            
            // TODO: check that these exist in requestData.  If not, signal error and send response with false.
            string transactionIDstr = requestData["id"] as string;
            string stateRequested = requestData["state"] as string;
            string returnMsg = "";
            
            bool success = GloebitAPI.Transaction.ProcessStateRequest(transactionIDstr, stateRequested, out returnMsg);
            
            //JsonValue[] result;
            //JsonValue[0] = JsonValue.CreateBooleanValue(success);
            //JsonValue[1] = JsonValue.CreateStringValue("blah");
            // JsonValue jv = JsonValue.Parse("[true, \"blah\"]")
            //JsonArray ja = new JsonArray();
            //ja.Add(JsonValue.CreateBooleanValue(success));
            //ja.Add(JsonValue.CreateStringValue("blah"));
            
            OSDArray paramArray = new OSDArray();
            paramArray.Add(success);
            if (!success) {
                paramArray.Add(returnMsg);
            }
            
            // TODO: build proper response with json
            Hashtable response = new Hashtable();
            response["int_response_code"] = 200;
            //response["str_response_string"] = ja.ToString();
            response["str_response_string"] = OSDParser.SerializeJsonString(paramArray);
            response["content_type"] = "application/json";
            m_log.InfoFormat("[GLOEBITMONEYMODULE].transactionState_func response:{0}", OSDParser.SerializeJsonString(paramArray));
            return response;
        }

        /// <summary>
        /// Used by the redirect-to parameter to GloebitAPI.Purchase.  Called when a user has finished purchasing gloebits
        /// Sends a balance update to the user
        /// </summary>
        /// <param name="requestData">response data from GloebitAPI.Authorize</param>
        private Hashtable buyComplete_func(Hashtable requestData) {
            UUID agentId = UUID.Parse(requestData["agentId"] as string);
            GloebitAPI.User u = GloebitAPI.User.Get(agentId);
            IClientAPI client = LocateClientObject(agentId);

            double balance = m_api.GetBalance(u);
            client.SendMoneyBalance(UUID.Zero, true, new byte[0], (int)balance, 0, UUID.Zero, false, UUID.Zero, false, 0, String.Empty);

            Hashtable response = new Hashtable();
            response["int_response_code"] = 200;
            response["str_response_string"] = "<html><head><title>Purchase Complete</title></head><body><h2>Purchase Complete</h2>Thank you for purchasing Gloebits.  You may now close this window.</body></html>";
            response["content_type"] = "text/html";
            return response;
        }
        
        /******************************************/
        /**** IAsyncEndpointCallback Interface ****/
        /******************************************/
        
        public void transactU2UCompleted(OSDMap responseDataMap, GloebitAPI.User sender, GloebitAPI.User recipient, GloebitAPI.Transaction txn) {
            
            bool success = (bool)responseDataMap["success"];
            string reason = responseDataMap["reason"];
            string status = "";
            if (responseDataMap.ContainsKey("status")) {
                status = responseDataMap["status"];
            }
            string tID = "";
            if (responseDataMap.ContainsKey("id")) {
                tID = responseDataMap["id"];
            }
            // TODO: verify that tID = txn.TransactionID --- should never be otherwise.
            
            // TODO: check if needed after messaging removed
            UUID buyerID = UUID.Parse(sender.PrincipalID);
            UUID sellerID = UUID.Parse(recipient.PrincipalID);
            UUID transactionID = UUID.Parse(tID);
            
            // remove these after messaging moved out of func.
            IClientAPI buyerClient = LocateClientObject(buyerID);
            IClientAPI sellerClient = LocateClientObject(sellerID);
            
            if (success) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE].transactU2UCompleted with SUCCESS reason:{0} id:{1}", reason, tID);
                if (reason == "success") {                                  /* successfully queued, early enacted all non-asset transaction parts */
                    // TODO: Once txn can't be null, turn this into an asset check.
                    if (txn == null) {
                        // TODO: examine the early enact without asset-callbacks code path and see if we need to handle other reasons not handled here.
                        // TODO: consider moving this alert to be called from the GAPI.
                        alertUsersTransactionSucceeded(txn);
                    } else {
                        ////buyerClient.SendAlertMessage(String.Format("Gloebit: Transaction successfully queued and gloebits transfered.\nTransaction ID: {0}", tID));
                        alertUsersTransactionStageCompleted(txn, GloebitAPI.TransactionStage.QUEUE, String.Empty);
                    }
                } else if (reason == "resubmitted") {                       /* transaction had already been created.  resubmitted to queue */
                    m_log.InfoFormat("[GLOEBITMONEYMODULE].transactU2UCompleted resubmitted transaction  id:{0}", tID);
                    ////buyerClient.SendAlertMessage("Gloebit: Transaction resubmitted to queue.");
                    alertUsersTransactionStageCompleted(txn, GloebitAPI.TransactionStage.QUEUE, "Transaction resubmitted to queue.");
                } else {                                                    /* Unhandled success reason */
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted unhandled response reason:{0}  id:{1}", reason, tID);
                    alertUsersTransactionStageCompleted(txn, GloebitAPI.TransactionStage.QUEUE, String.Empty);
                }
                
                // If no asset, consider complete and update balances; else update in consume callback.
                double balance = responseDataMap["balance"].AsReal();
                int intBal = (int)balance;
                // TODO: consider moving this to alert completed func.
                // TODO: Once txn can't be null, turn this into an asset check.
                if (txn == null) {
                    buyerClient.SendMoneyBalance(transactionID, true, new byte[0], intBal, 0, buyerID, false, sellerID, false, 0, String.Empty);
                    if (sellerClient != null) {
                        sellerClient.SendMoneyBalance(transactionID, true, new byte[0], intBal, 0, buyerID, false, sellerID, false, 0, String.Empty);
                    }
                }
                
            } else if (status == "queued") {                                /* successfully queued.  an early enact failed */
                m_log.InfoFormat("[GLOEBITMONEYMODULE].transactU2UCompleted transaction successfully queued for processing.  id:{0}", tID);
                // TODO: Once txn can't be null, turn this into an asset check.
                if (txn != null) {
                    ////buyerClient.SendAlertMessage("Gloebit: Transaction successfully queued for processing.");
                    alertUsersTransactionStageCompleted(txn, GloebitAPI.TransactionStage.QUEUE, String.Empty);
                }
                // TODO: possible we should only handle queued errors here if asset is null
                if (reason == "insufficient balance") {                     /* permanent failure - actionable by buyer */
                    m_log.InfoFormat("[GLOEBITMONEYMODULE].transactU2UCompleted transaction failed.  Buyer has insufficent funds.  id:{0}", tID);
                    /*if (buyerClient != null) {
                        buyerClient.SendAgentAlertMessage("Gloebit: Transaction failed.  Insufficient funds.  Go to https://www.gloebit.com/purchase to get more gloebits.", false);
                    }*/
                    alertUsersTransactionFailed(txn, GloebitAPI.TransactionStage.ENACT_GLOEBIT, "Insufficient funds.  Go to https://www.gloebit.com/purchase to get more gloebits.");
                } else if (reason == "pending") {                           /* queue will retry enacts */
                    // may not be possible.  May only be "pending" if includes a charge part which these will not.
                } else {                                                    /* perm failure - assumes tp will get same response form part.enact */
                    // Shouldn't ever get here.
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted transaction failed during processing.  reason:{0} id:{1}", reason, tID);
                    /*if (buyerClient != null) {
                        buyerClient.SendAgentAlertMessage("Gloebit: Transaction failed during processing.  Please retry.  Contact Regoin/Grid owner if failure persists.", false);
                    }*/
                    alertUsersTransactionFailed(txn, GloebitAPI.TransactionStage.ENACT_GLOEBIT, "Failure during processing.  Please retry.  Contact Regoin/Grid owner if failure persists.");
                }
            } else {                                                        /* failure prior to queing.  Something requires fixing */
                string subscriptionIDStr = String.Empty;
                string appSubscriptionIDStr = String.Empty;
                string subscriptionAuthIDStr = String.Empty;
                UUID subscriptionAuthID = UUID.Zero;
                if (responseDataMap.ContainsKey("subscription-id")) {
                    subscriptionIDStr = responseDataMap["subscription-id"];
                }
                if (responseDataMap.ContainsKey("app-subscription-id")) {
                    appSubscriptionIDStr = responseDataMap["app-subscription-id"];
                }
                if (responseDataMap.ContainsKey("subscription-authorization-id")) {
                    subscriptionAuthIDStr = responseDataMap["subscription-authorization-id"];
                    subscriptionAuthID = UUID.Parse(subscriptionAuthIDStr);
                }
                
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted with FAILURE reason:{0} status:{1} id:{2}", reason, status, tID);
                
                
                if (status == "queuing-failed") {                           /* failed to queue.  net or processor error */
                    /*if (buyerClient != null) {
                        buyerClient.SendAgentAlertMessage("Gloebit: Transaction Failed. Queuing error.  Please try again.  If problem persists, contact Gloebit.", false);
                    }*/
                    alertUsersTransactionFailed(txn, GloebitAPI.TransactionStage.QUEUE, "Queuing error.  Please try again.  If problem persists, contact Gloebit.");
                } else if (status == "failed") {                            /* race condition - already queued */
                    // nothing to tell user.  buyer doesn't need to know it was double submitted
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted race condition.  You double submitted transaction:{0}", tID);
                } else if (status == "cannot-spend") {                      /* Buyer's gloebit account is locked and not allowed to spend gloebits */
                    /*if (buyerClient != null) {
                        buyerClient.SendAgentAlertMessage("Gloebit: Transaction Failed.  Your Gloebit account is locked.  Please contact Gloebit to resolve any account status issues.", false);
                    }*/
                    alertUsersTransactionFailed(txn, GloebitAPI.TransactionStage.VALIDATE, "Your Gloebit account is locked.  Please contact Gloebit to resolve any account status issues.");
                } else if (status == "cannot-receive") {                    /* Seller's gloebit account can not receive gloebits */
                    // TODO: should we try to message seller if online?
                    // TODO: Is it a privacy issue to alert buyer here?
                    // TODO: research if/when account is in this state.  Only by admin?  All accounts until merchants?
                    /*if (buyerClient != null) {
                        buyerClient.SendAgentAlertMessage("Gloebit: Transaction Failed.  Seller's Gloebit account is unable to receive gloebits.  Please alert seller to this issue if possible and have seller contact Gloebit.", false);
                    }*/
                    alertUsersTransactionFailed(txn, GloebitAPI.TransactionStage.VALIDATE, "Seller's Gloebit account is unable to receive gloebits.  Please alert seller to this issue if possible and have seller contact Gloebit.");
                } else if (status == "unknown-merchant") {                  /* can not identify merchant from params supplied by app */
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted Gloebit could not identify merchant from params.  transactionID:{0} merchantID:{1}", tID, sender.PrincipalID);
                    /*if (buyerClient != null) {
                        buyerClient.SendAgentAlertMessage("Gloebit: Transaction Failed.  Gloebit can not identify seller from OpenSim account.  Please alert seller to this issue if possible and have seller contact Gloebit.", false);
                    }*/
                    alertUsersTransactionFailed(txn, GloebitAPI.TransactionStage.VALIDATE, "Gloebit can not identify seller from OpenSim account.  Please alert seller to this issue if possible and have seller contact Gloebit.");
                } else if (reason == "Transaction with automated-transaction=True is missing subscription-id") {
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted Subscription-id missing from transaction marked as unattended/automated transaction.  transactionID:{0}", tID);
                    // TODO: Do we need to register a subscription, or is this a case we should never end up in?
                    alertUsersTransactionFailed(txn, GloebitAPI.TransactionStage.VALIDATE, "Missing subscription-id from transaction marked as subscription payment.  Please retry.  If problem persists, contact region/grid owner.");
                } else if (status == "unknown-subscription") {
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted Gloebit can not identify subscription from identifier(s).  transactionID:{0}, subscription-id:{1}, app-subscription-id:{2}", tID, subscriptionIDStr, appSubscriptionIDStr);
                    // TODO: We should wipe this subscription from the DB and re-create it.
                    alertUsersTransactionFailed(txn, GloebitAPI.TransactionStage.VALIDATE, "Gloebit can not identify subscription from transaction marked as subscription payment.  Please retry.  If problem persists, contact region/grid owner.");
                } else if (status == "unknown-subscription-authorization") {
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted Gloebit No subscription authorization in place.  transactionID:{0}, subscription-id:{1}, app-subscription-id:{2} BuyerID:{3} BuyerName:{4}", tID, subscriptionIDStr, appSubscriptionIDStr, buyerID, resolveAgentName(buyerID));
                    // TODO: Should we store auths so we know if we need to create it or just to ask user to auth it again?
                    // We have a valid subscription, but no subscription auth for this user-id-on-app+token(gloebit_uid) combo
                    // Ask user if they would like to authorize
                    // Don't call CreateSubscriptionAuthorization unless they do.  If this is fraud, the user will not want to see a pending auth.
                    
                    // TODO: should we use SubscriptionIDStr, validate that they match, or get rid of that?
                    
                    alertUsersTransactionFailed(txn, GloebitAPI.TransactionStage.VALIDATE, "Payer has not authorized payments for this subscription.  You will be presented with an additional message instructing you how to approve or deny authorization for future automated transactions for this subscription.");
                    
                    if (buyerClient != null) {
                        Dialog.Send(new CreateSubscriptionAuthorizationDialog(buyerClient, buyerID, resolveAgentName(buyerID), txn.PartID, txn.PartName, txn.PartDescription, transactionID, sellerID, resolveAgentName(sellerID), txn.Amount, txn.SubscriptionID, m_api, m_economyURL));
                    } else {
                        // TODO: does the message eventually make it if the user is offline?  Is there a way to send a Dialog to a user the next time they log in?
                        // Should we just create the subscription_auth in this case?
                    }
                    
                    
                } else if (status == "subscription-authorization-pending") {
                    // User has been asked and chose to auth already.
                    // Subscription-authorization has already been created.
                    // User has not yet responded to that request, so send a dialog again to ask for auth and allow reporting of fraud.
                    
                    m_log.InfoFormat("[GLOEBITMONEYMODULE] transactU2UCompleted - status:{0} \n subID:{1} appSubID:{2} apiUrl:{3} ", status, subscriptionIDStr, appSubscriptionIDStr, m_apiUrl);
                    
                    alertUsersTransactionFailed(txn, GloebitAPI.TransactionStage.VALIDATE, "Payer has a pending authorization for this subscription.  You will be presented with an additional message instructing you how to approve or deny authorization for future automated transactions for this subscription.");
                    
                    // Send request to user again
                    if (buyerClient != null) {
                        Dialog.Send(new PendingSubscriptionAuthorizationDialog(buyerClient, buyerID, resolveAgentName(buyerID), txn.PartID, txn.PartName, txn.PartDescription, transactionID, sellerID, resolveAgentName(sellerID), txn.Amount, txn.SubscriptionID, subscriptionAuthID, m_api, m_economyURL));
                    } else {
                        // TODO: does the message eventually make it if the user is offline?  Is there a way to send a Dialog to a user the next time they log in?
                        // Should we just create the subscription_auth in this case?
                    }


                } else if (status == "subscription-authorization-declined") {
                    m_log.InfoFormat("[GLOEBITMONEYMODULE] transactU2UCompleted - FAILURE -- user declined subscription auth.");
                    // TODO: should we do something here? -- different type of dialog perhaps
                    // Send dialog asking user to auth or report --- needs different message.
                    
                    alertUsersTransactionFailed(txn, GloebitAPI.TransactionStage.VALIDATE, "Payer has declined authorization for this subscription.  There is not currently a method for resetting this authorization request.  If you would like such functionality, please contact Gloebit to request it.");
                } else {                                                    /* App issue --- Something needs fixing by app */
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted Transaction failed.  App needs to fix something.");
                    /*if (buyerClient != null) {
                        buyerClient.SendAgentAlertMessage("Gloebit: Transaction Failed.  Application provided malformed transaction to Gloebit.  Please retry.  Contact Regoin/Grid owner if failure persists.", false);
                    }*/
                    alertUsersTransactionFailed(txn, GloebitAPI.TransactionStage.VALIDATE, "Application provided malformed transaction to Gloebit.  Please retry.  Contact Regoin/Grid owner if failure persists.");
                }
            }
            return;
        }
        
        public void createSubscriptionCompleted(OSDMap responseDataMap, GloebitAPI.Subscription subscription) {
            
            bool success = (bool)responseDataMap["success"];
            string reason = responseDataMap["reason"];
            string status = responseDataMap["status"];
            
            //string tID = "";
            //if (responseDataMap.ContainsKey("id")) {
            //    tID = responseDataMap["id"];
            //}
            
            //UUID buyerID = UUID.Parse(sender.PrincipalID);
            //UUID sellerID = UUID.Parse(recipient.PrincipalID);
            //UUID transactionID = UUID.Parse(tID);
            //IClientAPI buyerClient = LocateClientObject(buyerID);
            //IClientAPI sellerClient = LocateClientObject(sellerID);
            
            if (success) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE].createSubscriptionCompleted with SUCCESS reason:{0} status:{1}", reason, status);
                // TODO: Do we need to message any client?
                
                // TODO: Do we need to take any action? -- should we restart a stalled transaction or ask the user to auth this subscription?
                
            } else if (status == "retry") {                                /* failure could be temporary -- retry. */
                m_log.InfoFormat("[GLOEBITMONEYMODULE].createSubscriptionCompleted with FAILURE but suggested retry.  reason:{0}", reason);
                
                // TODO: Should we retry?  How do we prevent infinite loop?
                
            } else if (status == "failed") {                                /* failure permanent -- requires fixing something. */
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].createSubscriptionCompleted with FAILURE permanently.  reason:{0}", reason);
                
                // TODO: Any action required?
                
            } else {                                                        /* failure - unexpected status */
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].createSubscriptionCompleted with FAILURE - unhandled status:{0} reason:{1}", status, reason);
            }
            return;
        }
        
        public void createSubscriptionAuthorizationCompleted(OSDMap responseDataMap, GloebitAPI.Subscription sub, GloebitAPI.User sender, IClientAPI client) {
            m_log.InfoFormat("[GLOEBITMONEYMODULE].createSubscriptionAuthorizationCompleted");
            
            bool success = (bool)responseDataMap["success"];
            string reason = responseDataMap["reason"];
            string status = responseDataMap["status"];
            
            //string tID = "";
            //if (responseDataMap.ContainsKey("id")) {
            //    tID = responseDataMap["id"];
            //}
            
            UUID senderID = UUID.Parse(sender.PrincipalID);
            //UUID sellerID = UUID.Parse(recipient.PrincipalID);
            //UUID transactionID = UUID.Parse(tID);
            IClientAPI senderClient = LocateClientObject(senderID);
            //IClientAPI sellerClient = LocateClientObject(sellerID);
            
            // TODO: we need to carry the transactionID through and retrieve toID, toName, amount
            UUID toID = UUID.Zero;
            UUID transactionID = UUID.Zero;
            string toName = "testing name";
            int amount = 47;
            
            if (success) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE].createSubscriptionAuthorizationCompleted with SUCCESS reason:{0} status:{1}", reason, status);
                switch (status) {
                    case "success":
                    case "created":
                    case "duplicate":
                        // grab subscription_authorization_id
                        string subAuthID = responseDataMap["id"];
                        
                        // Send Authorize URL
                        m_api.SendSubscriptionAuthorizationToClient(client, subAuthID, sub);
                        
                        break;
                    case "duplicate-and-already-approved-by-user":
                        // TODO: if we have a transaction pending, trigger it
                        break;
                    case "duplicate-and-previously-declined-by-user":
                        // TODO: determine what we'll do here.
                        // Is this a success case or failure case?
                        // Do we make a call to reset to pending?
                        // Do we send a dialog to the user --- maybe that makes more sense. !!! I think a new dialog message is the winner.
                        break;
                    default:
                        break;
                }
            } else if (status == "retry") {                                /* failure could be temporary -- retry. */
                m_log.InfoFormat("[GLOEBITMONEYMODULE].createSubscriptionAuthorizationCompleted with FAILURE but suggested retry.  reason:{0}", reason);
                
                // TODO: Should we retry?  How do we prevent infinite loop?
                
            } else if (status == "failed") {                                /* failure permanent -- requires fixing something. */
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].createSubscriptionAuthorizationCompleted with FAILURE permanently.  reason:{0}", reason);
                
                // TODO: Any action required?
                // TODO: if we move "duplicate-and-previously-declined-by-user" to here, then we should handle it here and we need another endpoint to reset status of this subscription auth to pending
                
            } else {                                                        /* failure - unexpected status */
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].createSubscriptionAuthorizationCompleted with FAILURE - unhandled status:{0} reason:{1}", status, reason);
            }
            return;
        }
        
        
        /***************************************/
        /**** IAssetCallback Interface *********/
        /***************************************/
        
        private bool deliverObject(GloebitAPI.Transaction txn, IClientAPI buyerClient, out string returnMsg) {
            // TODO: this could fail if user logs off right after submission.  Is this what we want?
            // TODO: This basically always fails when you crash opensim and recover during a transaction.  Is this what we want?
            if (buyerClient == null) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].deliverObject FAILED to locate buyer agent");
                returnMsg = "Can't locate buyer.";
                return false;
            }
            
            // Retrieve BuySellModule used for dilivering this asset
            Scene s = LocateSceneClientIn(buyerClient.AgentId);    // TODO: should we be locating the scene the part is in instead of the agent in case the agent moved?
            IBuySellModule module = s.RequestModuleInterface<IBuySellModule>();
            if (module == null) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].deliverObject FAILED to access to IBuySellModule");
                ////buyerClient.SendAgentAlertMessage("Gloebit: OpenSim Asset delivery failed.  Could not access region IBuySellModule.", false);
                returnMsg = "Can't access IBuySellModule.";
                return false;
            }
            
            // Rebuild delivery params from Asset and attempt delivery of object
            bool success = module.BuyObject(buyerClient, txn.CategoryID, txn.LocalID, (byte)txn.SaleType, txn.Amount);
            if (!success) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].deliverObject FAILED to deliver asset");
                ////buyerClient.SendAgentAlertMessage("Gloebit: OpenSim Asset delivery failed.  IBuySellModule.BuyObject failed.  Please retry your purchase.", false);
                // TODO: is this message good to go?
                returnMsg = "IBuySellModule.BuyObject failed delivery attempt.";
            } else {
                m_log.InfoFormat("[GLOEBITMONEYMODULE].deliverObject SUCCESS - delivered asset");
                ////buyerClient.SendAlertMessage("Gloebit: OpenSim Asset delivered to inventory successfully.");
                returnMsg = "object delivery succeeded";
            }
            return success;
        }
        
        public bool processAssetEnactHold(GloebitAPI.Transaction txn, out string returnMsg) {
            
            // If we've gotten this call, then the Gloebit components have enacted successfully
            // all funds have been transferred.
            alertUsersTransactionStageCompleted(txn, GloebitAPI.TransactionStage.ENACT_GLOEBIT, String.Empty);
            
            // Retrieve Payer & Payee agents
            // TODO: remove these once messaging moved to alert funcs
            IClientAPI payerClient = LocateClientObject(txn.PayerID);
            IClientAPI payeeClient = LocateClientObject(txn.PayeeID);
            IClientAPI activeClient = payerClient;
            
            
            switch ((TransactionType)txn.TransactionType) {
                case TransactionType.USER_BUYS_OBJECT:
                    // 5000 - ObjectBuy
                    // Need to deliver the object/contents purchased.
                    bool delivered = deliverObject(txn, payerClient, out returnMsg);
                    if (!delivered) {
                        returnMsg = String.Format("Asset enact failed: {0}", returnMsg);
                        // Local Asset Enact failed - inform user
                        alertUsersTransactionFailed(txn, GloebitAPI.TransactionStage.ENACT_ASSET, returnMsg);
                        return false;
                    }
                    break;
                case TransactionType.USER_PAYS_USER:
                    // 5001 - OnMoneyTransfer - Pay User
                    // nothing to enact
                    break;
                case TransactionType.USER_PAYS_OBJECT:
                    // 5008 - OnMoneyTransfer - Pay Object
                    // need to alert the object that it has been paid.
                    ObjectPaid handleObjectPaid = OnObjectPaid;
                    if(handleObjectPaid != null) {
                        //handleObjectPaid(e.receiver, e.sender, e.amount);
                        handleObjectPaid(txn.PartID, txn.PayerID, txn.Amount);
                    } else {
                        // This really shouldn't happen, as it would mean that the OpenSim region is not properly set up
                        // However, we won't fail here as expectation is unclear
                        m_log.ErrorFormat("[GLOEBITMONEYMODULE].processAssetEnactHold - IMoneyModule OnObjectPaid event not properly subscribed.  Object payment may have failed.");
                    }
                    break;
                case TransactionType.OBJECT_PAYS_USER:
                    // 5009 - ObjectGiveMoney
                    // need to alert payee, not payer.
                    activeClient = payeeClient;
                    break;
                default:
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] processAssetEnactHold called on unknown transaction type: {0}", txn.TransactionType);
                    // TODO: should we throw an exception?  return null?  just continue?
                    // take no action.
                    break;
            }
            /*
            if (activeClient != null) {
                activeClient.SendAlertMessage(String.Format("Gloebit: Funds transferred successfully.\nTransaction ID: {0}", txn.TransactionID));
            }*/
            
            // Local Asset Enact completed
            alertUsersTransactionStageCompleted(txn, GloebitAPI.TransactionStage.ENACT_ASSET, String.Empty);
            
            returnMsg = "Asset enact succeeded";
            return true;
        }


        public bool processAssetConsumeHold(GloebitAPI.Transaction txn, out string returnMsg) {
            
            m_log.InfoFormat("[GLOEBITMONEYMODULE].processAssetConsumeHold SUCCESS - transaction complete");
            
            // If we've gotten this call, then the Gloebit components have enacted successfully
            // all transferred funds have been commited.
            alertUsersTransactionStageCompleted(txn, GloebitAPI.TransactionStage.CONSUME_GLOEBIT, String.Empty);
            
            // TODO: remove these once we've moved messaging.
            // Retrieve Payer & Payee agents
            IClientAPI payerClient = LocateClientObject(txn.PayerID);
            IClientAPI payeeClient = LocateClientObject(txn.PayeeID);
            IClientAPI activeClient = payerClient;
            
            switch ((TransactionType)txn.TransactionType) {
                case TransactionType.USER_BUYS_OBJECT:
                    // 5000 - ObjectBuy
                    // nothing to finalize
                    break;
                case TransactionType.USER_PAYS_USER:
                    // 5001 - OnMoneyTransfer - Pay User
                    // nothing to finalize
                    break;
                case TransactionType.USER_PAYS_OBJECT:
                    // 5008 - OnMoneyTransfer - Pay Object
                    // nothing to finalize
                    break;
                case TransactionType.OBJECT_PAYS_USER:
                    // 5009 - ObjectGiveMoney
                    // need to alert payee, not payer.
                    // TODO: really need to think about who we're informing here
                    activeClient = payeeClient;
                    break;
                default:
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] processAssetConsumeHold called on unknown transaction type: {0}", txn.TransactionType);
                    // TODO: should we throw an exception?  return null?  just continue?
                    // take no action.
                    break;
            }
            
            // TODO: really need to think about who we're informing for OBJECT_PAYS_USER
            
            // Local Asset Consume completed
            alertUsersTransactionStageCompleted(txn, GloebitAPI.TransactionStage.CONSUME_ASSET, String.Empty);
            
            if (payerClient == null) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE].processAssetConsumeHold FAILED to locate payer agent");
            } else {
                if (txn.PayerEndingBalance >= 0) {
                    payerClient.SendMoneyBalance(txn.TransactionID, true, new byte[0], txn.PayerEndingBalance, txn.TransactionType, txn.PayerID, false, txn.PayeeID, false, txn.Amount, String.Empty);
                } else {
                    // TODO: make gloebit get balance request for user asynchronously.
                }
            }
            if (payeeClient != null) {
                // TODO: Need to send a reqeust to get sender's balance from Gloebit asynchronously since this is not returned here.
                //sellerClient.SendMoneyBalance(transactionID, true, new byte[0], balance, asset.SaleType, buyerID, false, sellerID, false, asset.SalePrice, String.Empty);
            }
            /*
            if (activeClient != null) {
                activeClient.SendAgentAlertMessage(String.Format("Gloebit: Transaction complete.\nTransaction ID: {0}", txn.TransactionID), false);
            }*/
            // TODO: consider moving this alert to be called from the GAPI after we mark this txn consumed.
            alertUsersTransactionSucceeded(txn);
            
            returnMsg = "Asset consume succeeded";
            return true;
        }
        
        // This is only called if the the local asset had previously been successfully enacted before the transaction failed.
        // This really shouldn't happen since the local asset is the final transaction coponent and the transaction
        // should not be able to fail once it enacts successfully.
        public bool processAssetCancelHold(GloebitAPI.Transaction txn, out string returnMsg) {
            m_log.InfoFormat("[GLOEBITMONEYMODULE].processAssetCancelHold SUCCESS - transaction rolled back");
            
            // TODO: should probably move this out of here to GAPI to be reported when cancel is received regardless
            // of whter enact has already occurred; or set this function up to be called always on first cancel,
            // and check txn to see if undoing of enact is necessary.
            alertUsersTransactionStageCompleted(txn, GloebitAPI.TransactionStage.CANCEL_GLOEBIT, String.Empty);
            
            // Retrieve Payer & Payee agents
            ////IClientAPI payerClient = LocateClientObject(txn.PayerID);
            ////IClientAPI payeeClient = LocateClientObject(txn.PayeeID);
            ////IClientAPI activeClient = payerClient;
            
            // nothing to cancel - either enact of asset failed or was never called if we're here.
            switch ((TransactionType)txn.TransactionType) {
                case TransactionType.USER_BUYS_OBJECT:
                    // 5000 - ObjectBuy
                    // nothing to finalize
                    break;
                case TransactionType.USER_PAYS_USER:
                    // 5001 - OnMoneyTransfer - Pay User
                    // nothing to finalize
                    break;
                case TransactionType.USER_PAYS_OBJECT:
                    // 5008 - OnMoneyTransfer - Pay Object
                    // nothing to finalize
                    break;
                case TransactionType.OBJECT_PAYS_USER:
                    // 5009 - ObjectGiveMoney
                    // need to alert payee, not payer.
                    // TODO: really need to think about who we're informing here
                    ////activeClient = payeeClient;
                    break;
                default:
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] processAssetCancelHold called on unknown transaction type: {0}", txn.TransactionType);
                    // TODO: should we throw an exception?  return null?  just continue?
                    // take no action.
                    break;
            }
            
            /*if (activeClient == null) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].processAssetCancelHold FAILED to locate active agent");
            } else {
                ////activeClient.SendAgentAlertMessage(String.Format("Gloebit: Transaction canceled and rolled back.\nTrasaction ID: {0}", txn.TransactionID), false);
            }*/
            alertUsersTransactionStageCompleted(txn, GloebitAPI.TransactionStage.CANCEL_ASSET, String.Empty);
            returnMsg = "Asset cancel succeeded";
            return true;
        }

        #endregion

        #region local Fund Management

        /// <summary>
        /// Retrieves the gloebit balance of the gloebit account linked to the OpenSim agent defined by the agentID.
        /// </summary>
        /// <param name="AgentID">OpenSim AgentID for the user whose balance is being requested</param>
        /// <returns>Gloebit balance for the gloebit account linked to this OpenSim agent.</returns>
        private double GetFundsForAgentID(UUID agentID)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GetFundsForAgentID AgentID:{0}", agentID);
            GloebitAPI.User user = GloebitAPI.User.Get(agentID);
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GetFundsForAgentID User:{0}", user);
            double returnfunds = m_api.GetBalance(user);
            
            return returnfunds;
        }

        #endregion

        #region Utility Helpers

        /// <summary>
        /// Locates a IClientAPI for the client specified
        /// </summary>
        /// <param name="AgentID"></param>
        /// <returns></returns>
        private IClientAPI LocateClientObject(UUID AgentID)
        {
            ScenePresence tPresence = null;
            IClientAPI rclient = null;

            lock (m_scenel)
            {
                foreach (Scene _scene in m_scenel.Values)
                {
                    tPresence = _scene.GetScenePresence(AgentID);
                    if (tPresence != null)
                    {
                        if (!tPresence.IsChildAgent)
                        {
                            rclient = tPresence.ControllingClient;
                        }
                    }
                    if (rclient != null)
                    {
                        return rclient;
                    }
                }
            }
            return null;
        }

        private Scene LocateSceneClientIn(UUID AgentId)
        {
            lock (m_scenel)
            {
                foreach (Scene _scene in m_scenel.Values)
                {
                    ScenePresence tPresence = _scene.GetScenePresence(AgentId);
                    if (tPresence != null)
                    {
                        if (!tPresence.IsChildAgent)
                        {
                            return _scene;
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Utility function Gets an arbitrary scene in the instance.  For when which scene exactly you're doing something with doesn't matter
        /// </summary>
        /// <returns></returns>
        private Scene GetAnyScene()
        {
            lock (m_scenel)
            {
                foreach (Scene rs in m_scenel.Values)
                    return rs;
            }
            return null;
        }

        /// <summary>
        /// Utility function to get a Scene by RegionID in a module
        /// </summary>
        /// <param name="RegionID"></param>
        /// <returns></returns>
        private Scene GetSceneByUUID(UUID RegionID)
        {
            lock (m_scenel)
            {
                foreach (Scene rs in m_scenel.Values)
                {
                    if (rs.RegionInfo.originRegionID == RegionID)
                    {
                        return rs;
                    }
                }
            }
            return null;
        }

        #endregion

        #region event Handlers

        private void requestPayPrice(IClientAPI client, UUID objectID)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] requestPayPrice");
            Scene scene = LocateSceneClientIn(client.AgentId);
            if (scene == null)
                return;

            SceneObjectPart task = scene.GetSceneObjectPart(objectID);
            if (task == null)
                return;
            SceneObjectGroup group = task.ParentGroup;
            SceneObjectPart root = group.RootPart;

            client.SendPayPrice(objectID, root.PayPrice);
        }

        /// <summary>
        /// When the client closes the connection we remove their accounting
        /// info from memory to free up resources.
        /// </summary>
        /// <param name="AgentID">UUID of agent</param>
        /// <param name="scene">Scene the agent was connected to.</param>
        /// <see cref="OpenSim.Region.Framework.Scenes.EventManager.ClientClosed"/>
        private void ClientClosed(UUID AgentID, Scene scene)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] ClientClosed {0}", AgentID);
        }

        /// <summary>
        /// Call this when the client disconnects.
        /// </summary>
        /// <param name="client"></param>
        private void ClientClosed(IClientAPI client)
        {
            ClientClosed(client.AgentId, null);
        }

        /// <summary>
        /// Event called Economy Data Request handler.
        /// </summary>
        /// <param name="agentId"></param>
        private void EconomyDataRequestHandler(IClientAPI user)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] EconomyDataRequestHandler {0}", user.AgentId);
            Scene s = (Scene)user.Scene;

            user.SendEconomyData(EnergyEfficiency, s.RegionInfo.ObjectCapacity, ObjectCount, PriceEnergyUnit, PriceGroupCreate,
                                 PriceObjectClaim, PriceObjectRent, PriceObjectScaleFactor, PriceParcelClaim, PriceParcelClaimFactor,
                                 PriceParcelRent, PricePublicObjectDecay, PricePublicObjectDelete, PriceRentLight, PriceUpload,
                                 TeleportMinPrice, TeleportPriceExponent);
        }

        private void ValidateLandBuy(Object osender, EventManager.LandBuyArgs e)
        {
            
            
            lock (e)
            {
                e.economyValidated = true;
            }
       
            
        }

        private void processLandBuy(Object osender, EventManager.LandBuyArgs e)
        {
            
        }

        /// <summary>
        /// THis method gets called when someone pays someone else as a gift.
        /// </summary>
        /// <param name="osender"></param>
        /// <param name="e"></param>
        private void OnMoneyTransfer(Object osender, EventManager.MoneyTransferArgs e)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] OnMoneyTransfer sender {0} receiver {1} amount {2} transactiontype {3} description '{4}'", e.sender, e.receiver, e.amount, e.transactiontype, e.description);
            
            Scene s = (Scene) osender;
            string regionname = s.RegionInfo.RegionName;
            string regionID = s.RegionInfo.RegionID.ToString();
            
            // Decalare variables to be assigned in switch below
            IClientAPI activeClient = null;
            UUID fromID = UUID.Zero;
            UUID toID = UUID.Zero;
            UUID partID = UUID.Zero;
            string partName = null;
            string partDescription = String.Empty;
            ////string actionStr = String.Empty;
            OSDMap descMap = null;
            SceneObjectPart part = null;
            string description;
            
            // TODO: figure out how to get agent locations and add them to descMaps below
            
            /****** Fill in fields dependent upon transaction type ******/
            switch((TransactionType)e.transactiontype) {
                case TransactionType.USER_PAYS_USER:
                    // 5001 - OnMoneyTransfer - Pay User
                    
                    fromID = e.sender;
                    toID = e.receiver;
                    descMap = buildBaseTransactionDescMap(regionname, regionID, "PayUser");
                    if (String.IsNullOrEmpty(e.description)) {
                        description = "PayUser: <no description provided>";
                    } else {
                        description = String.Format("PayUser: {0}", e.description);
                    }
                    
                    activeClient = LocateClientObject(fromID);
                    ////actionStr = String.Format("Paying User: {0}", resolveAgentName(toID));
                    
                    break;
                case TransactionType.USER_PAYS_OBJECT:
                    // 5008 - OnMoneyTransfer - Pay Object
                    
                    partID = e.receiver;
                    part = s.GetSceneObjectPart(partID);
                    // TODO: Do we need to verify that part is not null?  can it ever by here?
                    partName = part.Name;
                    partDescription = part.Description;
                    fromID = e.sender;
                    toID = part.OwnerID;
                    descMap = buildBaseTransactionDescMap(regionname, regionID, "PayObject", part);
                    description = e.description;
                    
                    activeClient = LocateClientObject(fromID);
                    ////actionStr = String.Format("Paying Object: {0}\nOwned By: {1}", partName, resolveAgentName(toID));
                    
                    break;
                case TransactionType.OBJECT_PAYS_USER:
                    // 5009 - ObjectGiveMoney
                    m_log.ErrorFormat("******* OBJECT_PAYS_USER received in OnMoneyTransfer - Unimplemented transactiontype: {0}", e.transactiontype);
                    
                    // TransactionType 5009 is handled by ObjectGiveMoney and should never be trigger a call to OnMoneyTransfer
                    /*
                    partID = e.sender;
                    part = s.GetSceneObjectPart(partID);
                    partName = part.Name;
                    partDescription = part.Description;
                    fromID = part.OwnerID;
                    toID = e.receiver;
                    descMap = buildBaseTransactionDescMap(regionname, regionID, "ObjectPaysUser", part);
                    description = e.description;
                    activeClient = LocateClientObject(toID);
                    actionStr = String.Format("User Gifted Funds From Object: {0}\nOwned By: {1}", partName, resolveAgentName(fromID));
                    */
                    return;
                    break;
                default:
                    m_log.ErrorFormat("UNKNOWN Unimplemented transactiontype received in OnMoneyTransfer: {0}", e.transactiontype);
                    return;
                    break;
            }
            
            /******** Set up necessary parts for gloebit transact-u2u **********/
            
            GloebitAPI.Transaction txn = buildTransaction(transactionType: (TransactionType)e.transactiontype,
                                                          payerID: fromID, payeeID: toID, amount: e.amount, subscriptionID: UUID.Zero,
                                                          partID: partID, partName: partName, partDescription: partDescription,
                                                          categoryID: UUID.Zero, localID: 0, saleType: 0);
            // TODO: should we store "transaction description" with the Transaction?
            
            bool transaction_result = submitTransaction(txn, description, descMap, activeClient);
            
            // TODO - do we need to send any error message to the user if things failed above?`
        }

        /// <summary>
        /// Event Handler for when a root agent becomes a child agent
        /// </summary>
        /// <param name="avatar"></param>
        private void MakeChildAgent(ScenePresence avatar)
        {
            
        }

        /// <summary>
        /// Event Handler for when the client logs out.
        /// </summary>
        /// <param name="AgentId"></param>
        private void ClientLoggedOut(IClientAPI client)
        {
            // Deregister OnChatFromClient if we have one.
            Dialog.DeregisterAgent(client);
            
            m_log.InfoFormat("[GLOEBITMONEYMODULE] ClientLoggedOut {0}", client.AgentId);
        }

        /// <summary>
        /// Event Handler for when an Avatar enters one of the parcels in the simulator.
        /// </summary>
        /// <param name="avatar"></param>
        /// <param name="localLandID"></param>
        /// <param name="regionID"></param>
        private void AvatarEnteringParcel(ScenePresence avatar, int localLandID, UUID regionID)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] AvatarEnteringParcel {0}", avatar.Name);
            
            //m_log.Info("[FRIEND]: " + avatar.Name + " status:" + (!avatar.IsChildAgent).ToString());
        }

        #endregion

        private void ObjectBuy(IClientAPI remoteClient, UUID agentID,
                UUID sessionID, UUID groupID, UUID categoryID,
                uint localID, byte saleType, int salePrice)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] ObjectBuy client:{0}, agentID: {1}", remoteClient.AgentId, agentID);
            
            if (!m_sellEnabled)
            {
                ////remoteClient.SendBlueBoxMessage(UUID.Zero, "", "Buying is not enabled");
                alertUsersTransactionPreparationFailure(TransactionType.USER_BUYS_OBJECT, remoteClient, null, "Buying is not enabled in economy settings.");
                return;
            }

            Scene s = LocateSceneClientIn(remoteClient.AgentId);

            // Implmenting base sale data checking here so the default OpenSimulator implementation isn't useless 
            // combined with other implementations.  We're actually validating that the client is sending the data
            // that it should.   In theory, the client should already know what to send here because it'll see it when it
            // gets the object data.   If the data sent by the client doesn't match the object, the viewer probably has an 
            // old idea of what the object properties are.   Viewer developer Hazim informed us that the base module 
            // didn't check the client sent data against the object do any.   Since the base modules are the 
            // 'crowning glory' examples of good practice..

            // Validate that the object exists in the scene the user is in
            SceneObjectPart part = s.GetSceneObjectPart(localID);
            if (part == null)
            {
                remoteClient.SendAgentAlertMessage("Unable to buy now. The object was not found.", false);
                return;
            }
            
            // Validate that the client sent the price that the object is being sold for 
            if (part.SalePrice != salePrice)
            {
                ////remoteClient.SendAgentAlertMessage("Cannot buy at this price. Buy Failed. If you continue to get this relog.", false);
                alertUsersTransactionPreparationFailure(TransactionType.USER_BUYS_OBJECT, remoteClient, null, "Cannot buy at this price.  Price may have changed.  If you continue to get this error, relog.");
                return;
            }

            // Validate that the client sent the proper sale type the object has set 
            if (part.ObjectSaleType != saleType)
            {
                ////remoteClient.SendAgentAlertMessage("Cannot buy this way. Buy Failed. If you continue to get this relog.", false);
                alertUsersTransactionPreparationFailure(TransactionType.USER_BUYS_OBJECT, remoteClient, null, "Cannot buy this way.  Sale type may have changed.  If you continue to get this error, relog.");
                return;
            }

            // Check that the IBuySellModule is accesible before submitting the transaction to Gloebit
            IBuySellModule module = s.RequestModuleInterface<IBuySellModule>();
            if (module == null) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] ObjectBuy FAILED to access to IBuySellModule");
                ////remoteClient.SendAlertMessage("Transaction Failed.  Unable to access IBuySellModule");
                alertUsersTransactionPreparationFailure(TransactionType.USER_BUYS_OBJECT, remoteClient, null, "Unable to access IBuySellModule necessary for transferring inventory.  If this error continues to occur, please report it to the region or grid owner.");
                return;
            }

            string agentName = resolveAgentName(agentID);
            string regionname = s.RegionInfo.RegionName;
            string regionID = s.RegionInfo.RegionID.ToString();

            // string description = String.Format("{0} bought object {1}({2}) on {3}({4})@{5}", agentName, part.Name, part.UUID, regionname, regionID, m_gridnick);
            string description = String.Format("{0} object purchased on {1}, {2}", part.Name, regionname, m_gridnick);

            OSDMap descMap = buildBaseTransactionDescMap(regionname, regionID.ToString(), "ObjectBuy", part);
            
            ////string objectStr;
            switch (saleType) {
                case 1: // Sell as original (in-place sale)
                    ////objectStr = String.Format("Purchase Original: {0}\nFrom: {1}", part.Name, resolveAgentName(part.OwnerID));
                    break;
                case 2: // Sell a copy
                    ////objectStr = String.Format("Purchase Copy: {0}\nFrom: {1}", part.Name, resolveAgentName(part.OwnerID));
                    break;
                case 3: // Sell contents
                    ////objectStr = String.Format("Purchase Contents: {0}\nFrom: {1}", part.Name, resolveAgentName(part.OwnerID));
                    break;
                default:
                    // Should not get here unless an object purchase is submitted with a bad or new (but unimplemented here) saleType.
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] ObjectBuy Unrecognized saleType:{0} --- expected 1,2 or 3 for original, copy, or contents", saleType);
                    ////remoteClient.SendAlertMessage(String.Format("Can not complete transaction due to unrecognized saleType of {0}.  Please report this error to the region or grid owner.", saleType));
                    alertUsersTransactionPreparationFailure(TransactionType.USER_BUYS_OBJECT, remoteClient, null, String.Format("Unrecognized saleType of {0}.  If this error continues to occur, please report it to the region or grid owner.", saleType));
                    return;
            }
            
            GloebitAPI.Transaction txn = buildTransaction(transactionType: TransactionType.USER_BUYS_OBJECT,
                                                          payerID: agentID, payeeID: part.OwnerID, amount: salePrice, subscriptionID: UUID.Zero,
                                                          partID: part.UUID, partName: part.Name, partDescription: part.Description,
                                                          categoryID: categoryID, localID: localID, saleType: saleType);
            // TODO: should we store "transaction description" with the Transaction?
            
            bool transaction_result = submitTransaction(txn, description, descMap, remoteClient);
            
            m_log.InfoFormat("[GLOEBITMONEYMODULE] ObjectBuy Transaction queued {0}", txn.TransactionID.ToString());
        }
        
        
        /// <summary>
        /// Helper function to build the minimal transaction description sent to the Gloebit transactU2U endpoint.
        /// Used for tracking as well as information provided in transaction histories.
        /// If transaction includes an object, use the version which takes a fourth paramater as a SceneObjectPart.
        /// </summary>
        /// <param name="regionname">Name of the OpenSim region where this transaction is taking place.</param>
        /// <param name="regionID">OpenSim UUID of the region where this transaction is taking place.</param>
        /// <param name="txnType">String describing the type of transaction.  eg. ObjectBuy, PayObject, PayUser, etc.</param>
        /// <returns>OSDMap to be sent with the transaction request parameters.  Map contains six dictionary entries, each including an OSDArray.</returns>
        private OSDMap buildBaseTransactionDescMap(string regionname, string regionID, string txnType)
        {
            // Create descMap
            OSDMap descMap = new OSDMap();
            
            // Create arrays in descMap
            descMap["platform-names"] = new OSDArray();
            descMap["platform-values"] = new OSDArray();
            descMap["location-names"] = new OSDArray();
            descMap["location-values"] = new OSDArray();
            descMap["transaction-names"] = new OSDArray();
            descMap["transaction-values"] = new OSDArray();
            
            // Add base platform details
            addDescMapEntry(descMap, "platform", "platform", "OpenSim");
            addDescMapEntry(descMap, "platform", "version", OpenSim.VersionInfo.Version);
            addDescMapEntry(descMap, "platform", "version-number", OpenSim.VersionInfo.VersionNumber);
            // TODO: Should we add hosting-provider or more?
            
            // Add base location details
            addDescMapEntry(descMap, "location", "grid-name", m_gridname);
            addDescMapEntry(descMap, "location", "grid-nick", m_gridnick);
            addDescMapEntry(descMap, "location", "region-name", regionname);
            addDescMapEntry(descMap, "location", "region-id", regionID);
            
            // Add base transaction details
            addDescMapEntry(descMap, "transaction", "transaction-type", txnType);
            
            return descMap;
        }
        
        /// <summary>
        /// Helper function to build the minimal transaction description sent to the Gloebit transactU2U endpoint.
        /// Used for tracking as well as information provided in transaction histories.
        /// If transaction does not include an object, use the version which takes three paramaters instead.
        /// </summary>
        /// <param name="regionname">Name of the OpenSim region where this transaction is taking place.</param>
        /// <param name="regionID">OpenSim UUID of the region where this transaction is taking place.</param>
        /// <param name="txnType">String describing the type of transaction.  eg. ObjectBuy, PayObject, PayUser, etc.</param>
        /// <param name="part">Object (as SceneObjectPart) which is involved in this transaction (being sold, being paid, paying user, etc.).</param>
        /// <returns>OSDMap to be sent with the transaction request parameters.  Map contains six dictionary entries, each including an OSDArray.</returns>
        private OSDMap buildBaseTransactionDescMap(string regionname, string regionID, string txnType, SceneObjectPart part)
        {
            // Build universal base descMap
            OSDMap descMap = buildBaseTransactionDescMap(regionname, regionID, txnType);
            
            // Add base descMap details for transaciton involving an object/part
            if (descMap != null && part != null) {
                addDescMapEntry(descMap, "location", "object-group-position", part.GroupPosition.ToString());
                addDescMapEntry(descMap, "location", "object-absolute-position", part.AbsolutePosition.ToString());
                addDescMapEntry(descMap, "transaction", "object-name", part.Name);
                addDescMapEntry(descMap, "transaction", "object-description", part.Description);
                addDescMapEntry(descMap, "transaction", "object-id", part.UUID.ToString());
                addDescMapEntry(descMap, "transaction", "creator-name", resolveAgentName(part.CreatorID));
                addDescMapEntry(descMap, "transaction", "creator-id", part.CreatorID.ToString());
            }
            return descMap;
        }
        
        /// <summary>
        /// Helper function to add an entryName/entryValue pair to one of the three entryGroup array pairs for a descMap.
        /// Used by buildBaseTransactionDescMap, and to add additional entries to a descMap created by buildBaseTransactionDescMap.
        /// PRECONDITION: The descMap passed to this function must have been created and returned by buildBaseTransactionDescMap.
        /// Any entryName/Value pairs added to a descMap passed to the transactU2U endpoint will be sent to Gloebit, tracked with the transaction, and will appear in the transaction history for all users who are a party to the transaction.
        /// </summary>
        /// <param name="descMap">descMap created by buildBaseTransactionDescMap.</param>
        /// <param name="entryGroup">String group to which to add entryName/Value pair.  Must be one of {"platform", "location", "transactino"}.  Specifies group to which these details are most applicable.</param>
        /// <param name="entryName">String providing the name for entry to be added.  This is the name users will see in their transaction history for this entry.</param>
        /// <param name="entryValue">String providing the value for entry to be added.  This is the value users will see in their transaction history for this entry.</param>
        private void addDescMapEntry(OSDMap descMap, string entryGroup, string entryName, string entryValue)
        {
            
            /****** ERROR CHECKING *******/
            if (descMap == null) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] addDescMapEntry: Attempted to add an entry to a NULL descMap.  entryGroup:{0} entryName:{1} entryValue:{2}", entryGroup, entryName, entryValue);
                return;
            }
            if (entryGroup == null || entryName == null || entryValue == null) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] addDescMapEntry: Attempted to add an entry to a descMap where one of the entry strings is NULL.  entryGroup:{0} entryName:{1} entryValue:{2}", entryGroup, entryName, entryValue);
                return;
            }
            if (entryGroup == String.Empty || entryName == String.Empty) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] addDescMapEntry: Attempted to add an entry to a descMap where entryGroup or entryName is the empty string.  entryGroup:{0} entryName:{1} entryValue:{2}", entryGroup, entryName, entryValue);
                return;
            }
            
            List<string> permittedGroups = new List<string> {"platform", "location", "transaction"};
            if (!permittedGroups.Contains(entryGroup)) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] addDescMapEntry: Attempted to add a transaction description parameter in an entryGroup that is not be tracked by Gloebit.  entryGroup:{0} permittedGroups:{1} entryName:{2} entryValue:{3}", entryGroup, permittedGroups, entryName, entryValue);
                return;
            }
            
            /******* ADD ENTRY TO PROPER ARRAYS ******/
            switch (entryGroup) {
                case "platform":
                    ((OSDArray)descMap["platform-names"]).Add(entryName);
                    ((OSDArray)descMap["platform-values"]).Add(entryValue);
                    break;
                case "location":
                    ((OSDArray)descMap["location-names"]).Add(entryName);
                    ((OSDArray)descMap["location-values"]).Add(entryValue);
                    break;
                case "transaction":
                    ((OSDArray)descMap["transaction-names"]).Add(entryName);
                    ((OSDArray)descMap["transaction-values"]).Add(entryValue);
                    break;
                default:
                    // SHOULD NEVER GET HERE
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] addDescMapEntry: Attempted to add a transaction description parameter in an entryGroup that is not be tracked by Gloebit and made it to defualt of switch statement.  entryGroup:{0} permittedGroups:{1} entryName:{2} entryValue:{3}", entryGroup, permittedGroups, entryName, entryValue);
                    break;
            }
            return;
        }

        private Uri BaseURI {
            get {
                if(m_overrideBaseURI != null) {
                    return m_overrideBaseURI;
                } else {
                    return new Uri(GetAnyScene().RegionInfo.ServerURI);
                }
             }
        }
        
        /******************************************/
        /********* User Messaging Section *********/
        /******************************************/
        
        //**** Notes on OpenSim client messages ***/
        // AlertMessage: top right.  fades away. also appears in nearby chat (only to intended user). character limit of about 254
        // BlueBoxMessage: top right. fades away.  slightly darker than AlertMessage in Firestorm.  Also appears in nearby chat (only to intended user). no character limit
        // AgentAlertMessage w False: top right.  has "OK" button.  Fades away but stays in messages. character limit about 253
        // AgentAlertMessage w True: center. has "OK" button.  Does not fade away.  Requires clicking ok before interacting with anything else. character limit about 250
        
        private void sendMessageToClient(IClientAPI client, string message)
        {
            //payerClient.SendBlueBoxMessage(UUID.Zero, "What is this?", String.Format("BlueBoxMessage: {0}", message));
            //payerClient.SendAgentAlertMessage(String.Format("AgentAlertMessage: {0}", message), false);
            //payerClient.SendAgentAlertMessage(String.Format("AgentAlertMessage True: {0}", message), true);
            //payerClient.SendAlertMessage(String.Format("AlertMessage: {0}", message));
            
            //string imMessage = String.Format("{0}\n\n{1}", "Gloebit:", message);
            string imMessage = message;
            UUID fromID = UUID.Zero;
            string fromName = String.Empty;
            UUID toID = client.AgentId;
            bool isFromGroup = false;
            UUID imSessionID = toID;     // Don't know what this is used for.  Saw it hacked to agent id in friendship module
            bool isOffline = true;          // Don't know what this is for.  Should probably try both.
            bool addTimestamp = false;
            
            if (client != null) {
                // TODO: add alternate MessageFromAgent which includes an ok button and doesn't show up in chat, rather goes to notifications
                GridInstantMessage im = new GridInstantMessage(client.Scene, fromID, fromName, toID, (byte)InstantMessageDialog.MessageBox, isFromGroup, imMessage, imSessionID, isOffline, Vector3.Zero, new byte[0], addTimestamp);
                client.SendInstantMessage(im);
            } else {
                // TODO: do we want to send an email or do anything else?
                // TODO: do we need to hold the client through entire flows to ensure it will not be null if logged out which should be able to send
                // the message the next time the user logs in?
            }
        }
        
        private void sendTxnStatusToClient(GloebitAPI.Transaction txn, IClientAPI client, string message)
        {
            int shortenedID = (int)(txn.TransactionID.GetULong() % 10000);
            string sid = txn.TransactionID.ToString().Substring(0,4);
            
            string msg = String.Format("Gloebit Transaction ({0})({2}):\n{1}\n", shortenedID, message, sid);
            
            sendMessageToClient(client, msg);
        }
        
        
        private void alertUsersSubscriptionTransactionFailedForSubscriptionCreation(UUID payerID, UUID payeeID, int amount, GloebitAPI.Subscription sub)
        {
            IClientAPI payerClient = LocateClientObject(payerID);
            IClientAPI payeeClient = LocateClientObject(payeeID);
            
            // Need to alert payer whether online or not as action is required.
            if (payerClient != null) {
                payerClient.SendAlertMessage(String.Format("Gloebit: Scripted object attempted payment from you, but failed because no subscription exists for this recurring, automated payment.  Creating subscription now.  Once created, the next time this script attempts to debit your account, you will be asked to authorize that subscription for future auto-debits from your account.\n\nFailed Transaction Details:\n   Object Name: {0}\n   Object Description: {1}\n   Payment To: {2}\n   Amount: {3}", sub.ObjectName, sub.Description, resolveAgentName(payeeID), amount));
            } else {
                // TODO: send an email
            }
            
            // TODO: is this message bad if fraudster?
            // Should alert payee if online as might be expecting feedback
            if (payeeClient != null) {
                payeeClient.SendAlertMessage(String.Format("Gloebit: Scripted object attempted payment to you, but failed because no subscription exists for this recurring, automated payment.  Creating subscription now.  If you triggered this transaction with an action, you can retry in a minute.\n\nFailed Transaction Details:\n   Object Name: {0}\n   Object Description: {1}\n   Owner: {2}\n   Amount: {3}", sub.ObjectName, sub.Description, resolveAgentName(payerID), amount));
            }
        }
        
        private void alertUsersSubscriptionTransactionFailedForGloebitAuthorization(UUID payerID, UUID payeeID, int amount, GloebitAPI.Subscription sub)
        {
            IClientAPI payerClient = LocateClientObject(payerID);
            IClientAPI payeeClient = LocateClientObject(payeeID);
            
            // Need to alert payer whether online or not as action is required.
            if (payerClient != null) {
                payerClient.SendAlertMessage(String.Format("Gloebit: Scripted object attempted payment from you, but failed because you have not authorized this application from Gloebit.  Once you authorize this application, the next time this script attempts to debit your account, you will be asked to authorize that subscription for future auto-debits from your account.\n\nFailed Transaction Details:\n   Object Name: {0}\n   Object Description: {1}\n   Payment To: {2}\n   Amount: {3}", sub.ObjectName, sub.Description, resolveAgentName(payeeID), amount));
                m_api.Authorize(payerClient, BaseURI);
            } else {
                // TODO: send an email
            }
            
            // TODO: is this message bad if fraudster?
            // Should alert payee if online as might be expecting feedback
            if (payeeClient != null) {
                payeeClient.SendAlertMessage(String.Format("Gloebit: Scripted object attempted payment to you, but failed because the object owner has not yet authorized this subscription to make recurring, automated payments.  Requesting authorization now.\n\nFailed Transaction Details:\n   Object Name: {0}\n   Object Description: {1}\n   Owner: {2}\n   Amount: {3}", sub.ObjectName, sub.Description, resolveAgentName(payerID), amount));
            }
        }
        
        /// <summary>
        /// Called when application preparation of a transaction fails before submission to Gloebit is attempted.
        /// Use to inform users or log issues
        /// At a minimum, this should inform the user who triggered the transaction of failure so they have feedback.
        /// This is separated from alertUsersTransactionBegun because there may not be a transaction yet and therefore
        /// different arguments are needed.
        /// </summary>
        /// <param name="typeID">TransactionType that was being prepared.</param>
        /// <param name="payerClient">IClientAPI of payer or null.</param>
        /// <param name="payeeClient">IClientAPI of payee or null.</param>
        /// <param name="message">String containing additional details to be appended to the alert message.</param>
        private void alertUsersTransactionPreparationFailure(TransactionType typeID, IClientAPI payerClient, IClientAPI payeeClient, string message)
        {
            switch (typeID) {
                case TransactionType.USER_BUYS_OBJECT:
                    // Alert payer only; payee will be null
                    sendMessageToClient(payerClient, String.Format("Object buy precheck failure: {0}", message));
                    // TODO: should we format the message here to be "main reason: message"
                    break;
                case TransactionType.OBJECT_PAYS_USER:
                    // Alert payer and payee
                    // Currently handled in specific SUB functions above
                    // TODO: integrate here
                case TransactionType.USER_PAYS_USER:
                    // Alert payer only
                    // never happens currently
                case TransactionType.USER_PAYS_OBJECT:
                    // Alert payer only
                    // never happens currently
                default:
                    // Alert payer and payee
                    // TODO: log unimplemented type
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] alertUsersTransactionPreparationFailure: Unimplemented failure TransactionType [{0}] with message [{1}].", typeID, message);
                    break;
            }
        }
        
        /// <summary>
        /// Called just prior to the application submitting a transaction to Gloebit.
        /// This function should be used to provide immediate feedback to a user that their request/interaction was received.
        /// It is assumed that this is almost instantaneous and should be the source of immediate feedback that the user's action
        /// has resulted in a transaction.  If something added to the application's preparation is likely to delay this, then
        /// the application may wish to lower the priority of this message in favor of messaging the start of preparation.
        /// Once this is called, an alert for 1 or more stage status will be received and a transaction completion alert.
        /// </summary>
        /// <param name="txn">Transaction that failed.</param>
        /// <param name="payerClient">IClientAPI of payer or null.</param>
        /// <param name="payeeClient">IClientAPI of payee or null.</param>
        /// <param name="description">String containing txn description since this is not in the Transaction class yet.</param>
        private void alertUsersTransactionBegun(GloebitAPI.Transaction txn, IClientAPI payerClient, IClientAPI payeeClient, string description)
        {
            // TODO: examine comments in switch (copied)
            
            // TODO: consider using Txn.TransactionTypeString
            String actionStr;
            switch ((TransactionType)txn.TransactionType) {
                case TransactionType.USER_BUYS_OBJECT:
                    // Alert payer only; payee will be null
                    switch (txn.SaleType) {
                        case 1: // Sell as original (in-place sale)
                            actionStr = String.Format("Purchase Original: {0}\nFrom: {1}", txn.PartName, txn.PayeeName);
                            break;
                        case 2: // Sell a copy
                            actionStr = String.Format("Purchase Copy: {0}\nFrom: {1}", txn.PartName, txn.PayeeName);
                            break;
                        case 3: // Sell contents
                            actionStr = String.Format("Purchase Contents: {0}\nFrom: {1}", txn.PartName, txn.PayeeName);
                            break;
                        default:
                            // Should not get here as this should fail before transaction is built.
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] Transaction Begun With Unrecognized saleType:{0} --- expected 1,2 or 3 for original, copy, or contents", txn.SaleType);
                            // TODO: Assert this.
                            //assert(txn.TransactionType >= 1 && txn.TransactionType <= 3);
                            return;
                    }
                    break;
                case TransactionType.OBJECT_PAYS_USER:
                    // Alert payer and payee
                    // This looks like a message for payee, but is sent to payer
                    actionStr = String.Format("User Paid Funds From Object: {0}\nOwned By: {1}", txn.PartName, txn.PayerName);
                    break;
                case TransactionType.USER_PAYS_USER:
                    // Alert payer only
                    actionStr = String.Format("Paying User: {0}", txn.PayeeName);
                    break;
                case TransactionType.USER_PAYS_OBJECT:
                    // Alert payer only
                    actionStr = String.Format("Paying Object: {0}\nOwned By: {1}", txn.PartName, txn.PayeeName);
                    break;
                default:
                    // Alert payer and payee
                    // TODO: log unimplemented type
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] alertUsersTransactionPreparationFailure: Unimplemented TransactionBegun TransactionType [{0}] with description [{1}].", txn.TransactionType, description);
                    actionStr = "";
                    break;
            }
            
            // TODO: make these configurable to be turned on or off.
            string amountStr = String.Format("Amount: {0} gloebits", txn.Amount);
            string descStr = String.Format("Description: {0}", description);
            string idStr = String.Format("Transaction ID: {0}", txn.TransactionID);
            string txnDetails = String.Format("Details:\n   {0}\n   {1}\n   {2}", amountStr, descStr, idStr);
            /*
             if (activeClient != null) {
             string amountStr = String.Format("Amount: {0} gloebits", txn.Amount);
             string descStr = String.Format("Description: {0}", description);
             string idStr = String.Format("Transaction ID: {0}", txn.TransactionID);
             activeClient.SendAlertMessage(String.Format("Gloebit: Submitting transaction request.\n{0}\n{1}\n{2}\n{3}", actionStr, amountStr, descStr, idStr));
             }
             */
            
            // TODO: determine if we ever need to alert Payee or if payer will ever be null and Payee set.
            // Alert payer only; payee will be null
            //int shortenedID = (int)(txn.TransactionID.GetULong() % 10000);
            //sendMessageToClient(payerClient, String.Format("Submitting transaction request ({0}):\n{1}\n{2}\n", shortenedID, actionStr, txnDetails));
            sendTxnStatusToClient(txn, payerClient, String.Format("Submitting transaction request...\n   {0}\n{1}", actionStr, txnDetails));
        }
        
        /// <summary>
        /// Called when various stages of the transaction succeed.
        /// These will never be the final message received, but in failure cases, may provide information that will not be
        /// contained in the final message, so it is recommended that failure messages make it to at least the user who
        /// triggered the transaction.
        /// If desired, the completion of the enaction (asset enacted/delivered) can be used to short circuit to a final success message
        /// more quickly as the transaction should always eventually succeed after this.
        /// Stages:
        /// --- Submitted: TransactU2U call succeeded
        /// --- Queued: TransactU2U async callback succeeded
        ///             This will also trigger final success or failure if the transactoin did not include an asset with callback urls.
        /// --- Enacted:
        /// ------ Funds transferred: AssetEnact started (all Gloebit components of transaction enacted successfully)
        /// ------ Asset delivered: AssetEnact completing (local components of transaction enacted successfully)
        /// --- Canceled:
        /// ------ Probably shouldn't ever get called.  Worth logging.
        /// </summary>
        /// <param name="txn">Transaction that failed.</param>
        /// <param name="stage">TransactionStage which the transaction successfully completed to drive this alert.</param>
        /// <param name="message">String containing additional details to be appended to the alert message.</param>
        private void alertUsersTransactionStageCompleted(GloebitAPI.Transaction txn, GloebitAPI.TransactionStage stage, string message)
        {
            // TODO: determine when we want to alert payer vs payee and if messages are specific to TransactionType
            IClientAPI payerClient = LocateClientObject(txn.PayerID);
            
            // TODO: incorporate message arg into message below.  possibly rename to additional details or extra info.
            
            string status = String.Empty;
            
            switch (stage) {
                case GloebitAPI.TransactionStage.SUBMIT:
                    ////sendMessageToClient(payerClient, String.Format("Transaction successfully submitted to Gloebit service ({0})\n.", shortenedID));
                    status = "Successfully submitted to Gloebit service.";
                    break;
                case GloebitAPI.TransactionStage.QUEUE:
                    // a) queued and gloebits transfered.
                    // b) resubmitted
                    // c) queued, but early enact failure
                    ////sendMessageToClient(payerClient, String.Format("Transaction successfully received by Gloebit and queued for processing ({0}).\n", shortenedID));
                    status = "Successfully received by Gloebit and queued for processing.";
                    break;
                case GloebitAPI.TransactionStage.ENACT_GLOEBIT:
                    ////activeClient.SendAlertMessage(String.Format("Gloebit: Funds transferred successfully.\nTransaction ID: {0}", txn.TransactionID));
                    ////sendMessageToClient(payerClient, String.Format("Gloebit components successfully enacted.  Funds transferred successfully.  ({0}).\n", shortenedID));
                    status = "Successfully transferred gloebits.";
                    break;
                case GloebitAPI.TransactionStage.ENACT_ASSET:
                    switch ((TransactionType)txn.TransactionType) {
                        case TransactionType.USER_BUYS_OBJECT:
                            // 5000 - ObjectBuy
                            // delivered the object/contents purchased.
                            switch (txn.SaleType) {
                                case 1: // Sell as original (in-place sale)
                                    ////sendMessageToClient(payerClient, String.Format("Local components successfully enacted.  Object delivered.  ({0}).\n", shortenedID));
                                    status = "Successfully delivered object.";
                                    break;
                                case 2: // Sell a copy
                                    ////sendMessageToClient(payerClient, String.Format("Local components successfully enacted.  Copy of object delivered to inventory.  ({0}).\n", shortenedID));
                                    status = "Successfully delivered copy of object to inventory.";
                                    break;
                                case 3: // Sell contents
                                    ////sendMessageToClient(payerClient, String.Format("Local components successfully enacted.  Object contents delivered to inventory.  ({0}).\n", shortenedID));
                                    status = "Successfully delivered object contents to inventory.";
                                    break;
                                default:
                                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] alertUsersTransactionStageCompleted called on unknown sale type: {0}", txn.SaleType);
                                    ////sendMessageToClient(payerClient, String.Format("Local components successfully enacted.  ({0}).\n", shortenedID));
                                    status = "Successfully enacted local components of transaction.";
                                    break;
                            }
                            break;
                        case TransactionType.USER_PAYS_USER:
                            // 5001 - OnMoneyTransfer - Pay User
                            // nothing local enacted
                            ////sendMessageToClient(payerClient, String.Format("Local components successfully enacted.  ({0}).", shortenedID));
                            status = "Successfully enacted local components of transaction.";
                            break;
                        case TransactionType.USER_PAYS_OBJECT:
                            // 5008 - OnMoneyTransfer - Pay Object
                            // alerted the object that it has been paid.
                            ////sendMessageToClient(payerClient, String.Format("Local components successfully enacted.  Object notified of payment.  ({0}).", shortenedID));
                            status = "Successfully notified object of payment.";
                            break;
                        case TransactionType.OBJECT_PAYS_USER:
                            // 5009 - ObjectGiveMoney
                            // TODO: who to alert payee, or payer.
                            ////sendMessageToClient(payerClient, String.Format("Local components successfully enacted.  ({0}).", shortenedID));
                            status = "Successfully enacted local components of transaction.";
                            break;
                        default:
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] alertUsersTransactionStageCompleted called on unknown transaction type: {0}", txn.TransactionType);
                            // TODO: should we throw an exception?  return null?  just continue?
                            // take no action.
                            ////sendMessageToClient(payerClient, String.Format("Local components successfully enacted.  ({0}).", shortenedID));
                            status = "Successfully enacted local components of transaction.";
                            break;
                    }
                    break;
                case GloebitAPI.TransactionStage.CONSUME_GLOEBIT:
                    ////sendMessageToClient(payerClient, String.Format("Gloebit components successfully consumed.  ({0}).", shortenedID));
                    status = "Successfully finalized transfer of gloebits.";
                    break;
                case GloebitAPI.TransactionStage.CONSUME_ASSET:
                    ////sendMessageToClient(payerClient, String.Format("Local components successfully consumed.  ({0}).", shortenedID));
                    status = "Successfully finalized local components of transaction.";
                    break;
                case GloebitAPI.TransactionStage.CANCEL_GLOEBIT:
                    ////sendMessageToClient(payerClient, String.Format("Gloebit components successfully canceled and rolled back.  ({0}).", shortenedID));
                    status = "Successfully canceled and rolled back transfer of gloebits.";
                    break;
                case GloebitAPI.TransactionStage.CANCEL_ASSET:
                    ////"Gloebit: Transaction canceled and rolled back.\nTrasaction ID: {0}", txn.TransactionID)
                    ////sendMessageToClient(payerClient, String.Format("Local components successfully canceled and rolled back.  ({0}).", shortenedID));
                    status = "Successfully canceled and rolled back local components of transaction.";
                    break;
                default:
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] alertUsersTransactionStageCompleted called on unhandled transaction stage : {0}", stage);
                    // TODO: should we throw an exception?  return null?  just continue?
                    // take no action.
                    ////sendMessageToClient(payerClient, String.Format("unhandled transaction stage completed for transaction.  ({0}).", shortenedID));
                    status = "Successfully completed undefined transaction stage";
                    break;
            }
            if (!String.IsNullOrEmpty(message)) {
                status = String.Format("{0}\n{1}", status, message);
            }
            sendTxnStatusToClient(txn, payerClient, status);
        }
        
        /// <summary>
        /// Called when transaction completes with failure.
        /// At a minimum, this should always be messaged to the user who triggered the transaction.
        /// </summary>
        /// <param name="txn">Transaction that failed.</param>
        /// <param name="stage">TransactionStage which the transaction successfully completed to drive this alert.</param>
        /// <param name="message">String containing additional details to be appended to the alert message.</param>
        private void alertUsersTransactionFailed(GloebitAPI.Transaction txn, GloebitAPI.TransactionStage stage, string message)
        {
            // TODO: determine when we want to alert payer vs payee and if messages are specific to TransactionType
            IClientAPI payerClient = LocateClientObject(txn.PayerID);
            
            // TODO: create a shortID in txn class
            int shortenedID = (int)(txn.TransactionID.GetULong() % 10000);
            
            string error = String.Empty;
            string instruction = String.Empty;
            
            // TODO: incorporate message arg into message below.  possibly rename to additional details or extra info.
            
            switch (stage) {
                case GloebitAPI.TransactionStage.SUBMIT:
                    ////String.Format("Gloebit: Transaction Failed.\nRegion Failed to properly create and send request to Gloebit.  Please try again.\nTransaction ID: {0}", txn.TransactionID)
                    ////sendMessageToClient(payerClient, String.Format("Transaction Failed ({0}).\nRegion failed to properly create and send request to Gloebit.  Please try again.  If problem persists, please contact Region or Grid Owner.", shortenedID));
                    error = "Region failed to propery create and send request to Gloebit.";
                    instruction = "Please try again.  If problem persists, please contact Region or Grid Owner.";
                    break;
                case GloebitAPI.TransactionStage.VALIDATE:
                    //// Validate Form
                    //// Validate Payer
                    //// Validate Payee
                    //// Validate Subscription
                    //// Validate Subscription Authorization
                    
                    ////"Your Gloebit account is locked.  Please contact Gloebit to resolve any account status issues." - BUYER ONLY
                    ////"Seller's Gloebit account is unable to receive gloebits.  Please alert seller to this issue if possible and have seller contact Gloebit."  Buyer/Seller???
                    ////"Gloebit can not identify seller from OpenSim account.  Please alert seller to this issue if possible and have seller contact Gloebit." Buyer/Seller???
                    ////"Missing subscription-id from transaction marked as subscription payment.  Please retry.  If problem persists, contact region/grid owner."
                    ////"Gloebit can not identify subscription from transaction marked as subscription payment.  Please retry.  If problem persists, contact region/grid owner."
                    ////"Payer has not authorized payments for this subscription.  You will be presented with an additional message instructing you how to approve or deny authorization for future automated transactions for this subscription.");
                    ////"Payer has a pending authorization for this subscription.  You will be presented with an additional message instructing you how to approve or deny authorization for future automated transactions for this subscription."
                    ////"Payer has declined authorization for this subscription.  There is not currently a method for resetting this authorization request.  If you would like such functionality, please contact Gloebit to request it."
                    ////"Application provided malformed transaction to Gloebit.  Please retry.  Contact Regoin/Grid owner if failure persists."
                    sendMessageToClient(payerClient, String.Format("Transaction Failed ({0}).\nValidation Error.", shortenedID));
                    error = "Validation error.";
                    // instruction is dependent upon message -
                    break;
                case GloebitAPI.TransactionStage.QUEUE:
                    ////"Queuing error.  Please try again.  If problem persists, contact Gloebit."
                    ////sendMessageToClient(payerClient, String.Format("Transaction Failed ({0}).\nQueuing error.  Please try again.  If problem persists, contact Gloebit.", shortenedID));
                    error = "Queuing Error.";
                    instruction = "Please try again.  If problem persists, contact Gloebit.";
                    break;
                case GloebitAPI.TransactionStage.ENACT_GLOEBIT:
                    ////"Gloebit: Transaction failed.  Insufficient funds.  Go to https://www.gloebit.com/purchase to get more gloebits." buyer only (early enact)
                    ////"Failure during processing.  Please retry.  Contact Regoin/Grid owner if failure persists." (early enact)
                    ////sendMessageToClient(payerClient, String.Format("Transaction Failed ({0}).\nEnacting of Gloebit components failed.", shortenedID));
                    error = "Transfer of gloebits failed.";
                    // TODO: consider adding instruction for where to go to purchase more Gloebits.
                    break;
                case GloebitAPI.TransactionStage.ENACT_ASSET:
                    switch ((TransactionType)txn.TransactionType) {
                        case TransactionType.USER_BUYS_OBJECT:
                            // 5000 - ObjectBuy
                            // delivered the object/contents purchased.
                            ////"Can't locate buyer."
                            ////"Can't access IBuySellModule."
                            ////"IBuySellModule.BuyObject failed delivery attempt."
                            switch (txn.SaleType) {
                                case 1: // Sell as original (in-place sale)
                                    ////sendMessageToClient(payerClient, String.Format("Transaction Failed ({0}).\nDelivery of object failed.", shortenedID));
                                    error = "Delivery of object failed.";
                                    break;
                                case 2: // Sell a copy
                                    ////sendMessageToClient(payerClient, String.Format("Transaction Failed ({0}).\nDelivery of object copy failed.", shortenedID));
                                    error = "Delivery of object copy failed.";
                                    break;
                                case 3: // Sell contents
                                    ////sendMessageToClient(payerClient, String.Format("Transaction Failed ({0}).\nDelivery of object contents failed.", shortenedID));
                                    error = "Delivery of object contents failed.";
                                    break;
                                default:
                                    ////sendMessageToClient(payerClient, String.Format("Transaction Failed ({0}).\nEnacting of local components failed.", shortenedID));
                                    error = "Enacting of local transaction components failed.";
                                    break;
                            }
                            break;
                        case TransactionType.USER_PAYS_USER:
                            // 5001 - OnMoneyTransfer - Pay User
                            // nothing local enacted
                            ////sendMessageToClient(payerClient, String.Format("Transaction Failed ({0}).\nEnacting of local components failed.", shortenedID));
                            error = "Enacting of local transaction components failed.";
                            break;
                        case TransactionType.USER_PAYS_OBJECT:
                            // 5008 - OnMoneyTransfer - Pay Object
                            // alerted the object that it has been paid.
                            ////sendMessageToClient(payerClient, String.Format("Transaction Failed ({0}).\nEnacting of local components failed.  Object not notified of payment.", shortenedID));
                            error = "Object payment notification failed.";
                            break;
                        case TransactionType.OBJECT_PAYS_USER:
                            // 5009 - ObjectGiveMoney
                            // TODO: who to alert payee, or payer.
                            ////sendMessageToClient(payerClient, String.Format("Transaction Failed ({0}).\nEnacting of local components failed.", shortenedID));
                            error = "Enacting of local transaction components failed.";
                            break;
                        default:
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] alertUsersTransactionStageCompleted called on unknown transaction type: {0}", txn.TransactionType);
                            // TODO: should we throw an exception?  return null?  just continue?
                            // take no action.
                            ////sendMessageToClient(payerClient, String.Format("Transaction Failed ({0}).\nEnacting of local components failed.", shortenedID));
                            error = "Enacting of local transaction components failed.";
                            break;
                    }
                    break;
                default:
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] alertUsersTransactionFailed called on unhandled transaction stage : {0}", stage);
                    // TODO: should we throw an exception?  return null?  just continue?
                    // take no action.
                    ////sendMessageToClient(payerClient, String.Format("Transaction Failed ({0}).\nUnhandled transaction failure.", shortenedID));
                    error = "Unhandled transaction failure.";
                    break;
            }
            string status = error;
            if (!String.IsNullOrEmpty(message)) {
                status = String.Format("{0}\n{1}", status, message);
            }
            if (!String.IsNullOrEmpty(instruction)) {
                status = String.Format("{0}\n{1}", status, instruction);
            }
            sendTxnStatusToClient(txn, payerClient, status);
            
        }
        
        /// <summary>
        /// Called when a transaction has successfully completed so that necessary notification can be triggered.
        /// At a minimum, this should notify the user who triggered the transaction.
        /// </summary>
        /// <param name="txn">Transaction that succeeded.</param>
        private void alertUsersTransactionSucceeded(GloebitAPI.Transaction txn)
        {
            // TODO: determine when we want to alert payer vs payee and if messages are specific to TransactionType
            IClientAPI payerClient = LocateClientObject(txn.PayerID);
            
            ////sendMessageToClient(payerClient, String.Format("Transaction successfully completed ({0})", shortenedID));
            sendTxnStatusToClient(txn, payerClient, "Transaction SUCCEEDED.");
            
            // TODO: design system for including txn details if set to display them.
            
            // TODO: Move balance updates to here.
        }

        public enum TransactionType : int
        {
            USER_BUYS_OBJECT    = 5000,             // comes through ObjectBuy
            USER_PAYS_USER      = 5001,             // comes through OnMoneyTransfer
            REFUND              = 5005,             // not yet implemented
            USER_PAYS_OBJECT    = 5008,             // comes through OnMoneyTransfer
            OBJECT_PAYS_USER    = 5009,             // script auto debit owner - comes thorugh ObjectGiveMoney
            // USER_BUYS_LAND = 5013,
        }
        
    }
}
