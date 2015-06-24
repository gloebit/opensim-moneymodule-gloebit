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
using OpenMetaverse.StructuredData;     // TODO: turn assetData into a dictionary of <string, object> and remove this.

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
                GloebitAssetData.Initialise(m_gConfig.Configs["DatabaseService"]);
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
                        httpServer.AddHTTPHandler("/gloebit/asset", assetState_func);
                       
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
            m_log.InfoFormat("[GLOEBITMONEYMODULE] ObjectGiveMoney {0}", description);
            
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
            
            
            OSDMap descMap = buildBaseTransactionDescMap(regionname, regionID, "ObjectGiveMoney", part);

            bool give_result = doMoneyTransfer(fromID, toID, amount, 2, description, descMap);

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
            CheckExistAndRefreshFunds(client.AgentId);

            // Subscribe to Money messages
            client.OnEconomyDataRequest += EconomyDataRequestHandler;
            client.OnMoneyBalanceRequest += SendMoneyBalance;
            client.OnRequestPayPrice += requestPayPrice;
            client.OnObjectBuy += ObjectBuy;
            client.OnLogout += ClientLoggedOut;
        }

        /// <summary>
        /// Transfer money
        /// </summary>
        /// <param name="Sender"></param>
        /// <param name="Receiver"></param>
        /// <param name="amount"></param>
        /// <returns></returns>
        private bool doMoneyTransfer(UUID Sender, UUID Receiver, int amount, int transactiontype, string description, OSDMap descMap)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] doMoneyTransfer from {0} to {1}, for amount {2}, transactiontype: {3}, description: {4}",
                Sender, Receiver, amount, transactiontype, description);
            bool result = true;

            ////m_api.Transact(GloebitAPI.User.Get(Sender), resolveAgentName(Sender), amount, description);
            m_api.TransactU2U(GloebitAPI.User.Get(Sender), resolveAgentName(Sender), GloebitAPI.User.Get(Receiver), resolveAgentName(Receiver), resolveAgentEmail(Receiver), amount, description, null, UUID.Zero, descMap, m_economyURL);

            // TODO: Should we be returning true before Transact completes successfully now that this is async???
            // TODO: use transactiontype
            return result;
        }
        
        // TODO: May want to merge these separate doMoneyTransfer functions into one.
        
        /// <summary>
        /// Transfer money from one OpenSim agent to another.  Utilize asset to receive transact enact/consume/cancel callbacks, deliver
        /// any OpenSim assets being purchased, and handle any other OpenSim components of the transaction.
        /// </summary>
        /// <param name="Sender">OpenSim UUID of agent sending gloebits.</param>
        /// <param name="Receiver">OpenSim UUID of agent receiving gloebits</param>
        /// <param name="amount">Amount of gloebits being transferred.</param>
        /// <param name="transactiontype">int from OpenSim describing type of transaction (buy original, buy copy, buy contents, pay object, pay user, gift from object to user, etc)</param>
        /// <param name="description">Description of transaction for transaction history reporting.</param>
        /// <param name="asset">Object which will handle reception of enact/consume/cancel callbacks and delivery of any OpenSim assets or handling of any other OpenSim components of the transaction.</param>
        /// <param name="transactionID">Unique ID for transaciton provided by OpenSim.  This will be provided back in any callbacks allows for Idempotence.</param>
        /// <param name="descMap">Map of platform, location & transaction descriptors for tracking/querying and transaciton history details.  For more details, see buildTransactionDescMap helper function.</param>
        /// <param name="remoteClient">Used solely for sending transaction status messages to OpenSim user requesting transaction.</param>
        /// <returns>true if async transactU2U web request was built and submitted successfully; false if failed to submit request;  If true, IAsyncEndpointCallback transactU2UCompleted should eventually be called with additional details on state of request.</returns>
        private bool doMoneyTransferWithAsset(UUID Sender, UUID Receiver, int amount, int transactiontype, string description, GloebitAPI.Asset asset, UUID transactionID, OSDMap descMap, IClientAPI remoteClient)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] doMoneyTransfer with asset from {0} to {1}, for amount {2}, transactiontype: {3}, description: {4}",
                             Sender, Receiver, amount, transactiontype, description);
            
            // TODO: Should we wrap TransactU2U or request.BeginGetResponse in Try/Catch?
            // TODO: Should we return IAsyncResult in addition to bool on success?  May not be necessary since we've created an asyncCallback interface,
            //       but could make it easier for app to force synchronicity if desired.
            bool result = m_api.TransactU2U(GloebitAPI.User.Get(Sender), resolveAgentName(Sender), GloebitAPI.User.Get(Receiver), resolveAgentName(Receiver), resolveAgentEmail(Receiver), amount, description, asset, transactionID, descMap, m_economyURL);
            
            if (!result) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] doMoneyTransferWithAsset failed to create HttpWebRequest in GloebitAPI.TransactU2U");
                remoteClient.SendAlertMessage("Transaction Failed.  Region Failed to properly create and send request to Gloebit.  Please try again.");
            } else {
                remoteClient.SendAlertMessage("Gloebit: Transaction Successfully submitted to Gloebit Service.");
            }
            
            // TODO: Should we be returning true before Transact completes successfully now that this is async???
            // TODO: use transactiontype
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

            // GloebitAPI.User user = m_api.ExchangeAccessToken(LocateClientObject(UUID.Parse(agentId)), code);

            // string token = m_api.ExchangeAccessToken(LocateClientObject(UUID.Parse(agentId)), code);
            m_api.ExchangeAccessToken(LocateClientObject(UUID.Parse(agentId)), code, m_economyURL);

            // TODO: stop logging token
            //m_log.InfoFormat("[GLOEBITMONEYMODULE] authComplete_func got token: {0}", token);
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
        /// Registered to the enactHoldURI, consumeHoldURI and cancelHoldURI from GloebitAPI.Asset.
        /// Called by the Gloebit transaction processor.
        /// Enacts, cancels, or consumes the GloebitAPI.Asset.
        /// Response of true certifies that the Asset transaction part has been processed as requested.
        /// Response of false alerts transaction processor that asset failed to process as requested.
        /// Additional data can be returned about failures, specifically whether or not to retry.
        /// </summary>
        /// <param name="requestData">GloebitAPI.Asset enactHoldURI, consumeHoldURI or cancelHoldURI query arguments tying this callback to a specific Asset.</param>
        /// <returns>Web respsponse including JSON array of one or two elements.  First element is bool representing success state of call.  If first element is false, the second element is a string providing the reason for failure.  If the second element is "pending", then the transaction processor will retry.  All other reasons are considered permanent failure.</returns>
        private Hashtable assetState_func(Hashtable requestData) {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] assetState_func **************** Got Callback");
            foreach(DictionaryEntry e in requestData) { m_log.InfoFormat("{0}: {1}", e.Key, e.Value); }
            
            // TODO: check that these exist in requestData.  If not, signal error and send response with false.
            string transactionIDstr = requestData["id"] as string;
            string stateRequested = requestData["state"] as string;
            string returnMsg = "";
            
            bool success = GloebitAPI.Asset.ProcessStateRequest(transactionIDstr, stateRequested, out returnMsg);
            // bool success = m_api.Asset.ProcessStateRequest(transactionIDstr, stateRequested);
            
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
            m_log.InfoFormat("[GLOEBITMONEYMODULE].assetState_func response:{0}", OSDParser.SerializeJsonString(paramArray));
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
        
        public void transactU2UCompleted(OSDMap responseDataMap, GloebitAPI.User sender, GloebitAPI.User recipient, GloebitAPI.Asset asset) {
            
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
            
            UUID buyerID = UUID.Parse(sender.PrincipalID);
            UUID sellerID = UUID.Parse(recipient.PrincipalID);
            UUID transactionID = UUID.Parse(tID);
            IClientAPI buyerClient = LocateClientObject(buyerID);
            IClientAPI sellerClient = LocateClientObject(sellerID);
            
            if (success) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE].transactU2UCompleted with SUCCESS reason:{0} id:{1}", reason, tID);
                if (reason == "success") {                                  /* successfully queued, early enacted all non-asset transaction parts */
                    if (buyerClient != null) {
                        if (asset == null) {
                            buyerClient.SendAgentAlertMessage("Gloebit: Transaction successfully completed.", false);
                        } else {
                            buyerClient.SendAlertMessage("Gloebit: Transaction successfully queued and gloebits transfered.");
                        }
                    }
                } else if (reason == "resubmitted") {                       /* transaction had already been created.  resubmitted to queue */
                    m_log.InfoFormat("[GLOEBITMONEYMODULE].transactU2UCompleted resubmitted transaction  id:{0}", tID);
                    if (buyerClient != null) {
                        buyerClient.SendAlertMessage("Gloebit: Transaction resubmitted to queue.");
                    }
                } else {                                                    /* Unhandled success reason */
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted unhandled response reason:{0}  id:{1}", reason, tID);
                }
                
                // If no asset, consider complete and update balances; else update in consume callback.
                double balance = responseDataMap["balance"].AsReal();
                int intBal = (int)balance;
                if (asset == null) {
                    buyerClient.SendMoneyBalance(transactionID, true, new byte[0], intBal, 0, buyerID, false, sellerID, false, 0, String.Empty);
                    if (sellerClient != null) {
                        sellerClient.SendMoneyBalance(transactionID, true, new byte[0], intBal, 0, buyerID, false, sellerID, false, 0, String.Empty);
                    }
                }
                
            } else if (status == "queued") {                                /* successfully queued.  an early enact failed */
                m_log.InfoFormat("[GLOEBITMONEYMODULE].transactU2UCompleted transaction successfully queued for processing.  id:{0}", tID);
                if (buyerClient != null) {
                    if (asset != null) {
                        buyerClient.SendAlertMessage("Gloebit: Transaction successfully queued for processing.");
                    }
                }
                // TODO: possible we should only handle queued errors here if asset is null
                if (reason == "insufficient balance") {                     /* permanent failure - actionable by buyer */
                    m_log.InfoFormat("[GLOEBITMONEYMODULE].transactU2UCompleted transaction failed.  Buyer has insufficent funds.  id:{0}", tID);
                    if (buyerClient != null) {
                        buyerClient.SendAgentAlertMessage("Gloebit: Transaction failed.  Insufficient funds.  Go to https://www.gloebit.com/purchase to get more gloebits.", false);
                    }
                } else if (reason == "pending") {                           /* queue will retry enacts */
                    // may not be possible.  May only be "pending" if includes a charge part which these will not.
                } else {                                                    /* perm failure - assumes tp will get same response form part.enact */
                    // Shouldn't ever get here.
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted transaction failed during processing.  reason:{0} id:{1}", reason, tID);
                    if (buyerClient != null) {
                        buyerClient.SendAgentAlertMessage("Gloebit: Transaction failed during processing.  Please retry.  Contact Regoin/Grid owner if failure persists.", false);
                    }
                }
            } else {                                                        /* failure prior to queing.  Something requires fixing */
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted with FAILURE reason:{0} status:{1} id:{2}", reason, status, tID);
                if (status == "queuing-failed") {                           /* failed to queue.  net or processor error */
                    if (buyerClient != null) {
                        buyerClient.SendAgentAlertMessage("Gloebit: Transaction Failed. Queuing error.  Please try again.  If problem persists, contact Gloebit.", false);
                    }
                } else if (status == "failed") {                            /* race condition - already queued */
                    // nothing to tell user.  buyer doesn't need to know it was double submitted
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted race condition.  You double submitted transaction:{0}", tID);
                } else if (status == "cannot-spend") {                      /* Buyer's gloebit account is locked and not allowed to spend gloebits */
                    if (buyerClient != null) {
                        buyerClient.SendAgentAlertMessage("Gloebit: Transaction Failed.  Your Gloebit account is locked.  Please contact Gloebit to resolve any account status issues.", false);
                    }
                } else if (status == "cannot-receive") {                    /* Seller's gloebit account can not receive gloebits */
                    // TODO: should we try to message seller if online?
                    // TODO: Is it a privacy issue to alert buyer here?
                    // TODO: research if/when account is in this state.  Only by admin?  All accounts until merchants?
                    if (buyerClient != null) {
                        buyerClient.SendAgentAlertMessage("Gloebit: Transaction Failed.  Seller's Gloebit account is unable to receive gloebits.  Please alert seller to this issue if possible and have seller contact Gloebit.", false);
                    }
                } else if (status == "unknown-merchant") {                  /* can not identify merchant from params supplied by app */
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted Gloebit could not identify merchant from params.  transactionID:{0} merchantID:{1}", tID, sender.PrincipalID);
                    if (buyerClient != null) {
                        buyerClient.SendAgentAlertMessage("Gloebit: Transaction Failed.  Gloebit can not identify seller from OpenSim account.  Please alert seller to this issue if possible and have seller contact Gloebit.", false);
                    }
                } else {                                                    /* App issue --- Something needs fixing by app */
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted Transaction failed.  App needs to fix something.");
                    if (buyerClient != null) {
                        buyerClient.SendAgentAlertMessage("Gloebit: Transaction Failed.  Application provided malformed transaction to Gloebit.  Please retry.  Contact Regoin/Grid owner if failure persists.", false);
                    }
                }
            }
            return;
        }
        
        
        /***************************************/
        /**** IAssetCallback Interface *********/
        /***************************************/
        
        public bool processAssetEnactHold(GloebitAPI.Asset asset, out string returnMsg) {
            
            // Retrieve buyer agent
            IClientAPI buyerClient = LocateClientObject(asset.BuyerID);
            
            if (asset.GhostAsset) {
                // no object to deliver.  enact just for informational purposes.
                if (buyerClient != null) {
                    buyerClient.SendAlertMessage("Gloebit: Funds transferred successfully.");
                }
                returnMsg = "Asset enact succeeded";
                return true;
            }
            
            // TODO: this could fail if user logs off right after submission.  Is this what we want?
            // TODO: This basically always fails when you crash opensim and recover during a transaction.  Is this what we want?
            if (buyerClient == null) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].processAssetEnactHold FAILED to locate buyer agent");
                returnMsg = "Can't locate buyer";
                return false;
            }
            
            // Retrieve BuySellModule used for dilivering this asset
            Scene s = LocateSceneClientIn(buyerClient.AgentId);    // TODO: should we be locating the scene the part is in instead of the agent in case the agent moved?
            IBuySellModule module = s.RequestModuleInterface<IBuySellModule>();
            if (module == null) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].processAssetEnactHold FAILED to access to IBuySellModule");
                buyerClient.SendAgentAlertMessage("Gloebit: OpenSim Asset delivery failed.  Could not access region IBuySellModule.", false);
                returnMsg = "Can't access IBuySellModule";
                return false;
            }
            
            // Rebuild delivery params from Asset
            //UUID categoryID = asset.AssetData["categoryID"];
            //uint localID = asset.AssetData["localID"];
            //byte saleType = (byte) (int)asset.AssetData["saleType"];
            //int salePrice = asset.AssetData["salePrice"];
            
            // attempt delivery of object
            bool success = module.BuyObject(buyerClient, asset.CategoryID, asset.LocalID, (byte)asset.SaleType, asset.SalePrice);
            if (!success) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].processAssetEnactHold FAILED to deliver asset");
                buyerClient.SendAgentAlertMessage("Gloebit: OpenSim Asset delivery failed.  IBuySellModule.BuyObject failed.  Please retry your purchase.", false);
                returnMsg = "Asset enact failed";
            } else {
                m_log.InfoFormat("[GLOEBITMONEYMODULE].processAssetEnactHold SUCCESS - delivered asset");
                buyerClient.SendAlertMessage("Gloebit: OpenSim Asset delivered to inventory successfully.");
                returnMsg = "Asset enact succeeded";
            }
            return success;
        }
        
        public bool processAssetConsumeHold(GloebitAPI.Asset asset, out string returnMsg) {
            
            m_log.InfoFormat("[GLOEBITMONEYMODULE].processAssetConsumeHold SUCCESS - transaction complete");
            
            // Retrieve buyer & seller agents
            UUID buyerID = asset.BuyerID;
            UUID sellerID = asset.SellerID;
            UUID transactionID = asset.TransactionID;
            IClientAPI buyerClient = LocateClientObject(buyerID);
            IClientAPI sellerClient = LocateClientObject(sellerID);
            
            if (buyerClient == null) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].processAssetConsumeHold FAILED to locate buyer agent");
            } else {
                if (asset.BuyerEndingBalance >= 0) {
                    buyerClient.SendMoneyBalance(transactionID, true, new byte[0], asset.BuyerEndingBalance, asset.SaleType, buyerID, false, sellerID, false, asset.SalePrice, String.Empty);
                } else {
                    // TODO: make gloebit get balance request for user asynchronously.
                }
                buyerClient.SendAgentAlertMessage("Gloebit: Transaction complete.", false);
            }

            if (sellerClient != null) {
                // TODO: Need to send a reqeust to get sender's balance from Gloebit asynchronously since this is not returned here.
                //sellerClient.SendMoneyBalance(transactionID, true, new byte[0], balance, asset.SaleType, buyerID, false, sellerID, false, asset.SalePrice, String.Empty);
            }
            
            returnMsg = "Asset consume succeeded";
            return true;
        }
        
        public bool processAssetCancelHold(GloebitAPI.Asset asset, out string returnMsg) {
            m_log.InfoFormat("[GLOEBITMONEYMODULE].processAssetCancelHold SUCCESS - transaction rolled back");
            
            // Retrieve buyer agent
            IClientAPI remoteClient = LocateClientObject(asset.BuyerID);
            if (remoteClient == null) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].processAssetCancelHold FAILED to locate buyer agent");
            } else {
                remoteClient.SendAgentAlertMessage("Gloebit: Transaction canceled and rolled back.", false);
            }
            returnMsg = "Asset cancel succeeded";
            return true;
        }

        #endregion

        #region local Fund Management

        /// <summary>
        /// Ensures that the agent accounting data is set up in this instance.
        /// </summary>
        /// <param name="agentID"></param>
        private void CheckExistAndRefreshFunds(UUID agentID)
        {
            GloebitAPI.User user = GloebitAPI.User.Get(agentID);
            if(user != null) {
                m_api.GetBalance(user);
            }
        }

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
            // TODO: figure out how to get agent locations and add them to descMaps below
            
            OSDMap descMap = null;
            SceneObjectPart part = null;

            bool transaction_result = false;
            switch(e.transactiontype) {
                case 5001:
                    // Pay User Gift
                    descMap = buildBaseTransactionDescMap(regionname, regionID, "PayUser");
                    transaction_result = doMoneyTransfer(e.sender, e.receiver, e.amount, e.transactiontype, e.description, descMap);
                    break;
                case 5008:
                    // Pay Object
                    part = s.GetSceneObjectPart(e.receiver);
                    // TODO: Do we need to verify that part is not null?  can it ever by here?
                    UUID receiverOwner = part.OwnerID;
    
                    descMap = buildBaseTransactionDescMap(regionname, regionID, "PayObject", part);
                    transaction_result = doMoneyTransfer(e.sender, receiverOwner, e.amount, e.transactiontype, e.description, descMap);
                    
                    ObjectPaid handleObjectPaid = OnObjectPaid;
                    if(transaction_result && handleObjectPaid != null) {
                        // TODO - move this to a proper execute callback.
                        handleObjectPaid(e.receiver, e.sender, e.amount);
                    }
                    break;
                case 5009:
                    // Object Pays User
                    m_log.ErrorFormat("Unimplemented transactiontype {0}", e.transactiontype);
                    
                    // TODO: verify that this gets the right thing
                    part = s.GetSceneObjectPart(e.sender);
                    
                    descMap = buildBaseTransactionDescMap(regionname, regionID, "ObjectPaysUser", part);
                    
                    return;
                    break;
                default:
                    m_log.ErrorFormat("UNKNOWN Unimplemented transactiontype {0}", e.transactiontype);
                    return;
                    break;
            }
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
                remoteClient.SendBlueBoxMessage(UUID.Zero, "", "Buying is not enabled");
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
                remoteClient.SendAgentAlertMessage("Cannot buy at this price. Buy Failed. If you continue to get this relog.", false);
                return;
            }

            // Validate that the client sent the proper sale type the object has set 
            if (part.ObjectSaleType != saleType)
            {
                remoteClient.SendAgentAlertMessage("Cannot buy this way. Buy Failed. If you continue to get this relog.", false);
                return;
            }

            // Check that the IBuySellModule is accesible before submitting the transaction to Gloebit
            IBuySellModule module = s.RequestModuleInterface<IBuySellModule>();
            if (module == null) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] FAILED to access to IBuySellModule");
                remoteClient.SendAlertMessage("Transaction Failed.  Unable to access IBuySellModule");
                return;
            }

            string agentName = resolveAgentName(agentID);
            string regionname = s.RegionInfo.RegionName;
            string regionID = s.RegionInfo.RegionID.ToString();

            // string description = String.Format("{0} bought object {1}({2}) on {3}({4})@{5}", agentName, part.Name, part.UUID, regionname, regionID, m_gridnick);
            string description = String.Format("{0} object purchased on {1}, {2}", part.Name, regionname, m_gridnick);
                
            // Create a transaction ID
            UUID transactionID = UUID.Random();
                
            GloebitAPI.Asset asset = GloebitAPI.Asset.Init(transactionID, agentID, part.OwnerID, false, part.UUID, part.Name, categoryID, localID, saleType, salePrice);

            OSDMap descMap = buildBaseTransactionDescMap(regionname, regionID.ToString(), "ObjectBuy", part);
                
            doMoneyTransferWithAsset(agentID, part.OwnerID, salePrice, 2, description, asset, transactionID, descMap, remoteClient);
            
            m_log.InfoFormat("[GLOEBITMONEYMODULE] ObjectBuy Transaction queued {0}", transactionID.ToString());
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
    }
}
