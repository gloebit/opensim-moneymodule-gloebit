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
using System.IO;
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
using OpenSim.Region.OptionalModules.ViewerSupport;   // Necessary for SimulatorFeaturesHelper
using OpenSim.Services.Interfaces;
using OpenMetaverse.StructuredData;     // TODO: turn transactionData into a dictionary of <string, object> and remove this.
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;    // For ScriptBaseClass permissions constants

[assembly: Addin("Gloebit", "0.1")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]
[assembly: AddinDescription("OpenSim Addin for Gloebit Money Module")]
[assembly: AddinAuthor("Gloebit LLC gloebit@gloebit.com")]
//[assembly: ImportAddinFile("Gloebit.ini")]


namespace Gloebit.GloebitMoneyModule
{
    // TODO: Should this enum be inside of class or just the namespace?
    //       What about the other enums at the bottom of the file?
    enum GLBEnv {
        None = 0,
        Custom = 1,
        Sandbox = 2,
        Production = 3,
    }

    /// <summary>
    /// This is only the Gloebit Money Module which enables monetary transactions in OpenSim
    /// via the Gloebit API and Gloebit Services.
    /// </summary>

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "GloebitMoneyModule")]
    public class GloebitMoneyModule : IMoneyModule, ISharedRegionModule, GloebitAPI.IAsyncEndpointCallback, GloebitAPI.IAssetCallback
    {
        
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        
        /*** GMM CONFIGURATION ***/
        private IConfigSource m_gConfig;

        // TODO: Move these into the GAPI
        private const string SANDBOX_URL = "https://sandbox.gloebit.com/";
        private const string PRODUCTION_URL = "https://www.gloebit.com/";

        // m_enabled = true signifies that GMM is enabled across entire sim process
        // Combination of [Economy] economymodule set to "Gloebit" and [Gloebit] enabled set to true
        // If false, may still be enabled on individual regions.  see m_enabledRegions below.
        private bool m_enabled = true;
        // Set to false if anything is misconfigured
        private bool m_configured = true;
        
        // Populated from Gloebit.ini
        private UUID[] m_enabledRegions = null;         // Regions on sim to individually enable GMM.
                                                        // Only necessary if m_enabled is false
        private GLBEnv m_environment = GLBEnv.None;
        private string m_keyAlias;
        private string m_key;
        private string m_secret;
        private string m_apiUrl;
        private Uri m_overrideBaseURI;
        private static string m_contactGloebit = "Gloebit at OpenSimTransactionIssue@gloebit.com";
        private string m_contactOwner = "region or grid owner";
        private bool m_disablePerSimCurrencyExtras = false;
        
        // Populated from grid info
        private string m_gridnick = "unknown_grid";
        private string m_gridname = "unknown_grid_name";
        private Uri m_economyURL;
        private string m_dbProvider = null;
        private string m_dbConnectionString = null;
        
        // OpenSim Economic Data
        private bool m_sellEnabled = false;     // If not true, Object Buy txns won't work
        private float EnergyEfficiency = 0f;
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


        /*** GMM RUNTIME API AND DATA ***/
        // One API per sim to handle web calls to and from the Gloebit service
        private GloebitAPI m_api;

        /// <summary>
        /// Scenes by Region Handle
        /// </summary>
        private Dictionary<ulong, Scene> m_scenel = new Dictionary<ulong, Scene>();
        
        // Store land buy args necessary for completing land transactions
        private Dictionary<UUID, Object[]> m_landAssetMap = new Dictionary<UUID, Object[]>();
        // TODO: should assets be data stores or just in local memory? probably just local.
        // TODO: determine what info should be in transaction table and what should move to asset maps for other transactions
        
        // Store link to client we can't yet create auth for because there is no sub on file.  Handle in return from sub creation.
        private Dictionary<UUID, IClientAPI> m_authWaitingForSubMap = new Dictionary<UUID, IClientAPI>();
        
        // Some internal storage to only retrieve info once
        private string m_opensimVersion = String.Empty;
        private string m_opensimVersionNumber = String.Empty;
        private bool m_newLandPassFlow = false;
        


        #region IRegionModuleBase Interface
        
        public string Name {
            get { return "GloebitMoneyModule"; }
        }
        
        public Type ReplaceableInterface {
            get { return null; }
        }

        /// <summary>
        /// Called on startup so the module can be configured.
        /// </summary>
        /// <param name="config">Configuration source.</param>
        public void Initialise(IConfigSource config)
        {
            m_log.Info ("[GLOEBITMONEYMODULE] Initialising.");
            m_gConfig = config;

            LoadConfig(m_gConfig);

            string[] sections = {"Startup", "Gloebit", "Economy"};
            foreach (string section in sections) {
                IConfig sec_config = m_gConfig.Configs[section];

                if (null == sec_config) {
                    m_log.WarnFormat("[GLOEBITMONEYMODULE] Config section {0} is missing. Skipping.", section);
                    continue;
                }
                ReadConfigAndPopulate(sec_config, section);
            }
            
            // Load Grid info from GridInfoService if Standalone and GridInfo if Robust
            IConfig standalone_config = m_gConfig.Configs["GridInfoService"];
            IConfig robust_config = m_gConfig.Configs["GridInfo"];
            if (standalone_config == null && robust_config == null) {
                m_log.Warn("[GLOEBITMONEYMODULE] GridInfoService and GridInfo are both missing.  Can not retrieve GridInfoURI, GridName and GridNick.");
                // NOTE: we can continue and enable as this will just cause transaction history records to be missing some data.
            } else {
                if(standalone_config != null && robust_config != null) {
                    m_log.Warn("[GLOEBITMONEYMODULE] GridInfoService and GridInfo are both present.  Deferring to GridInfo to retrieve GridInfoURI, GridName and GridNick.");
                }
                if (robust_config != null) {
                    ReadConfigAndPopulate(robust_config, "GridInfo");
                } else {
                    ReadConfigAndPopulate(standalone_config, "GridInfoService");
                }
            }
            

            m_log.InfoFormat("[GLOEBITMONEYMODULE] Initialised. Gloebit enabled: {0}, GLBEnvironment: {1}, GLBApiUrl: {2} GLBKeyAlias {3}, GLBKey: {4}, GLBSecret {5}",
                m_enabled, m_environment, m_apiUrl, m_keyAlias, m_key, (m_secret == null ? "null" : "configured"));

            // TODO: I've added GLBEnv.Custom for testing.  Remove before we ship
            if(m_environment != GLBEnv.Sandbox && m_environment != GLBEnv.Production && m_environment != GLBEnv.Custom) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] Unsupported environment selected: {0}, disabling GloebitMoneyModule", m_environment);
                m_enabled = false;
                m_configured = false;
            }

            if (String.IsNullOrEmpty(m_dbProvider)) {
                // GLBSpecificStorageProvider wasn't specified so fall back to using the global
                // DatabaseService settings
                m_log.Info("[GLOEBITMONEYMODULE] using default StorageProvider and ConnectionString from DatabaseService");
                m_dbProvider = m_gConfig.Configs["DatabaseService"].GetString("StorageProvider");
                m_dbConnectionString = m_gConfig.Configs["DatabaseService"].GetString("ConnectionString");
            } else {
                m_log.Info("[GLOEBITMONEYMODULE] using GLBSpecificStorageProvider and GLBSpecificConnectionString");
            }

            if(String.IsNullOrEmpty(m_dbProvider) || String.IsNullOrEmpty(m_dbConnectionString)) {
                m_log.Error("[GLOEBITMONEYMODULE] database connection misconfigured, disabling GloebitMoneyModule");
                m_enabled = false;
                m_configured = false;
            }

            if(m_configured) {
                //string key = (m_keyAlias != null && m_keyAlias != "") ? m_keyAlias : m_key;
                m_api = new GloebitAPI(m_key, m_keyAlias, m_secret, new Uri(m_apiUrl), this, this);
                GloebitUserData.Initialise(m_dbProvider, m_dbConnectionString);
                GloebitTransactionData.Initialise(m_dbProvider, m_dbConnectionString);
                GloebitSubscriptionData.Initialise(m_dbProvider, m_dbConnectionString);
            }
        }

        /// <summary>
        /// Load Addin Configuration from Addin config dir
        /// </summary>
        /// <param name="config"></param>
        private void LoadConfig(IConfigSource config)
        {
           string configPath = string.Empty;
           bool created;
           string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
           if (!Util.MergeConfigurationFile(config, "Gloebit.ini", Path.Combine(assemblyDirectory, "Gloebit.ini.example"), out configPath, out created))
           {
               m_log.WarnFormat("[GLOEBITMONEYMODULE]: Gloebit.ini configuration file not merged");
               return;
           }
           if (created)
           {
               m_log.ErrorFormat("[GLOEBITMONEYMODULE]: PLEASE EDIT {0} BEFORE RUNNING THIS ADDIN", configPath);
               throw new Exception("Addin must be configured prior to running");
           }
        }

        /// <summary>
        /// Parse Standard MoneyModule Configuration
        /// </summary>
        /// <param name="config"></param>
        /// <param name="section"></param>
        private void ReadConfigAndPopulate(IConfig config, string section)
        {
            /********** [Startup] ************/
            if (section == "Startup") {
                // Standard set by prior money module systems to be in the Startup section.
                // Two money modules should never be enabled on the same region as they'll conflict,
                // and if all respect this param then this can't happen.
                // Unfortunately, while this prevents two from being enabled across the entire
                // sim process, it does not handle per-region configuration on a multi-region sim.
                // Gloebit for instance allows enabling by region if not enabled globally,
                // but many other money modules can not be enabled/disabled by region.
                m_enabled = (config.GetString("economymodule", "Gloebit") == "Gloebit");
                if(m_enabled) {
                    m_log.Info ("[GLOEBITMONEYMODULE] selected as global economymodule.");
                }
            }
            
            /********** [Gloebit] ************/
            if (section == "Gloebit") {
                /*** Get GloebitMoneyModule configuration details ***/
                // Is Gloebit disabled, enabled across the entire sim process, or on certain regions?
                bool enabled = config.GetBoolean("Enabled", false);
                m_enabled = m_enabled && enabled;
                if (!enabled) {
                    m_log.Info ("[GLOEBITMONEYMODULE] Not enabled globally. (to enable set \"Enabled = true\" in [Gloebit] and \"economymodule = Gloebit\" in [Startup])");
                }
                string enabledRegionIdsStr = config.GetString("GLBEnabledOnlyInRegions");
                if(!String.IsNullOrEmpty(enabledRegionIdsStr)) {
                    // null for the delimiter argument means split on whitespace
                    string[] enabledRegionIds = enabledRegionIdsStr.Split((string[])null, StringSplitOptions.RemoveEmptyEntries);
                    m_log.InfoFormat("[GLOEBITMONEYMODULE] GLBEnabledOnlyInRegions num regions: {0}", enabledRegionIds.Length);
                    m_enabledRegions = new UUID[enabledRegionIds.Length];
                    for(int i = 0; i < enabledRegionIds.Length; i++) {
                        m_enabledRegions[i] = UUID.Parse(enabledRegionIds[i]);
                        m_log.InfoFormat("[GLOEBITMONEYMODULE] selected as local economymodule for region {0}", enabledRegionIds[i]);
                    }
                }
                // Get region/grid owner contact details for transaction failure contact instructions.
                string ownerName = config.GetString("GLBOwnerName", "region or grid owner");
                string ownerEmail = config.GetString("GLBOwnerEmail", null);
                m_contactOwner = ownerName;
                if (!String.IsNullOrEmpty(ownerEmail)) {
                    m_contactOwner = String.Format("{0} at {1}", ownerName, ownerEmail);
                }
                // Should we disable adding info to OpenSimExtras map
                m_disablePerSimCurrencyExtras = config.GetBoolean("DisablePerSimCurrencyExtras", false);
                // Are we using custom db connection info
                m_dbProvider = config.GetString("GLBSpecificStorageProvider");
                m_dbConnectionString = config.GetString("GLBSpecificConnectionString");
                
                /*** Get Gloebit API configuration details ***/
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

            /********** [Economy] ************/
            if (section == "Economy") {
                /*** Get OpenSim built in pricing configuration info ***/
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

            /********** [GridInfoService] ************/
            if (section == "GridInfoService") {
                // If we're here, this is a standalone mode grid
                /*** Grab the grid info locally ***/
                setGridInfo(config.GetString("gridname", m_gridname), config.GetString("gridnick", m_gridnick), config.GetString("economy", null));
            }
            
            /********** [GridInfo] ************/
            if (section == "GridInfo") {
                // If we're here, this is a robust mode grid
                /*** Grab the grid info via the grid info uri ***/
                string gridInfoURI = config.GetString("GridInfoURI", null);
                // TODO: Should we store the info url?
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GRID INFO URL = {0}", gridInfoURI);
                if (String.IsNullOrEmpty(gridInfoURI)) {
                    m_log.ErrorFormat("[GloebitMoneyModule] Failed to retrieve GridInfoURI from [GridInfo] section of config.");
                    return;
                }
                // Create http web request from URL
                Uri requestURI = new Uri(new Uri(gridInfoURI), "json_grid_info");
                m_log.InfoFormat("[GLOEBITMONEYMODULE] Constructed and requesting URI = {0}", requestURI);
                HttpWebRequest request = (HttpWebRequest) WebRequest.Create(requestURI);
                request.Method = "GET";
                try {
                    // Get the response
                    HttpWebResponse response = (HttpWebResponse) request.GetResponse();
                    string status = response.StatusDescription;
                    m_log.InfoFormat("[GLOEBITMONEYMODULE] Grid Info status:{0}", status);
                    using(StreamReader response_stream = new StreamReader(response.GetResponseStream())) {
                        string response_str = response_stream.ReadToEnd();
                        m_log.InfoFormat("[GLOEBITMONEYMODULE] Grid Info:{0}", response_str);
                        // Parse the response
                        OSDMap responseData = (OSDMap)OSDParser.DeserializeJson(response_str);
                        // TODO: Can we assume these will all always be present, or do we need to use a TryGet?
                        setGridInfo(responseData["gridname"], responseData["gridnick"], responseData["economy"]);
                        // TODO: do we want anything else from grid info?
                    }
                } catch (Exception e) {
                    m_log.ErrorFormat("[GloebitMoneyModule] Failed to retrieve Grid Info.");
                }
            }
        }
        
        /// <summary>
        /// Helper funciton to store grid info regardless of whether the source is local (Standalone) or remote (Rubust).
        /// Stores gridName, gridNick, and economy config values
        /// </summary>
        /// <param name="gridName">Name of the Grid this sim process is connecting to.</param>
        /// <param name="gridNick">Nickname of Grid this sim process is connecting to.</param>
        /// <param name="ecoUrl">URL where the landtool.php and currency.php helper files are located.  These are necessary
        ///     for remnants of OpenSim which expected the grid to control these uniformly, not via the region.  Hopefully,
        ///     this will eventually be deprecated.</param>
        private void setGridInfo(string gridName, string gridNick, string ecoUrl) {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] Storing Grid Info: GridName:[{0}] GridNick:[{1}] EconomyURL:[{2}]", gridName, gridNick, ecoUrl);
            m_gridname = gridName;
            m_gridnick = gridNick;
            if (!String.IsNullOrEmpty(ecoUrl)) {
                m_economyURL = new Uri(ecoUrl);
            } else {
                m_economyURL = null;
                // We're not using this for the GloebitMoneyModule, but it is necessary for land sales.
                // It is also necessary for the buy-currency button to operate, though functionality of this flow is limited.
                m_log.WarnFormat("[GLOEBITMONEYMODULE] [GridInfoService] or [GridInfo] economy setting is not configured!  Land sales will not work.");
            }
        }
        
        public void Close()
        {
            m_enabled = false;
            m_configured = false;
        }

        public void AddRegion(Scene scene)
        {
            if(!m_configured) {
                return;
            }

            if (m_enabled || (m_enabledRegions != null && m_enabledRegions.Contains(scene.RegionInfo.RegionID)))
            {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] region added {0}", scene.RegionInfo.RegionID.ToString());
                scene.RegisterModuleInterface<IMoneyModule>(this);
                IHttpServer httpServer = MainServer.Instance;

                lock (m_scenel)
                {
                    // TODO: What happens if all regions are removed and one is re-created without a sim restart?
                    //       Should we use a bool instead of the count here?
                    if (m_scenel.Count == 0)
                    {
                        // XMLRPCHandler = scene;

                        // For land sales, buy-currency button, and the insufficent funds flows to operate,
                        // the economy helper uri needs to be present.  This can be set in the grid info or
                        // appended to the command line parameters used to start a client as follows:
                        //      -helperuri <URL to region on sim or to directory with landtool.php and currency.php helper scripts>
                        //      This commonly looks like -helperuri http://127.0.0.1:9000/
                       
                        // For a standalone grid, if the economy uri is directed at a region on this sim, then these
                        // handlers will enable land purchasing, buy-currency, and insufficient-funds flows.
                        // TODO: can these catch calls on a rubust grid?
                        httpServer.AddXmlRPCHandler("getCurrencyQuote", quote_func);
                        httpServer.AddXmlRPCHandler("buyCurrency", buy_func);
                        httpServer.AddXmlRPCHandler("preflightBuyLandPrep", preflightBuyLandPrep_func);
                        httpServer.AddXmlRPCHandler("buyLandPrep", landBuy_func);

                        /********** Register endpoints the Gloebit Service will call back into **********/
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

                // Register for events for user management
                scene.EventManager.OnNewClient += OnNewClient;                              // Registers client events
                scene.EventManager.OnClientClosed += ClientClosed;                          // Only used for debug log; maybe should clean up things
                scene.EventManager.OnAvatarEnteringNewParcel += AvatarEnteringParcel;       // Only used for debug log
                scene.EventManager.OnClientLogin += OnClientLogin;                          // Handles a login issue

                // Register for commerce events that come through scene
                scene.EventManager.OnMoneyTransfer += OnMoneyTransfer;                      // Handles 5001 (pay user) & 5008 (pay object) events
                scene.EventManager.OnValidateLandBuy += ValidateLandBuy;                    // Handles validation for free land transactions
                scene.EventManager.OnLandBuy += ProcessLandBuy;                             // Handles land purchases
                
            } else {
                if(m_enabledRegions != null) {
                    m_log.InfoFormat("[GLOEBITMONEYMODULE] SKIPPING region add {0} is not in enabled region list", scene.RegionInfo.RegionID.ToString());
                }
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
            if (!m_enabled && (m_enabledRegions == null || !m_enabledRegions.Contains(scene.RegionInfo.RegionID))) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] region not loaded as not enabled {0}", scene.RegionInfo.RegionID.ToString());
                return;
            }
            m_log.InfoFormat("[GLOEBITMONEYMODULE] region loaded {0}", scene.RegionInfo.RegionID.ToString());
            
            ISimulatorFeaturesModule featuresModule = scene.RequestModuleInterface<ISimulatorFeaturesModule>();
            bool enabled = !m_disablePerSimCurrencyExtras;
            if (enabled && featuresModule != null) {
                featuresModule.OnSimulatorFeaturesRequest += (UUID x, ref OSDMap y) => OnSimulatorFeaturesRequest(x, ref y, scene);
            }
            
            if (enabled) {
                // TODO: do we want to keep this?  Only using for logging debug info now.
                // TODO: Why is this here instead of in AddRegion with the rest of the scene events?
                scene.EventManager.OnNewPresence += OnNewPresence;
            }
        }
        
        private void OnSimulatorFeaturesRequest(UUID agentID, ref OSDMap features, Scene scene)
        {
            UUID regionID = scene.RegionInfo.RegionID;

            if (m_enabled || (m_enabledRegions != null && m_enabledRegions.Contains(regionID))) {
                // Get or create the extras section of the features map
                OSDMap extrasMap;
                if (features.ContainsKey("OpenSimExtras")) {
                    extrasMap = (OSDMap)features["OpenSimExtras"];
                } else {
                    extrasMap = new OSDMap();
                    features["OpenSimExtras"] = extrasMap;
                }
                
                // Add our values to the extras map
                extrasMap["currency"] = "G$";
                // replaced G$ with ₲ (hex 0x20B2 / unicode U+20B2), but screwed up balance display in Firestorm
                extrasMap["currency-base-uri"] = GetCurrencyBaseURI(scene);
            }
        }
        
        private string GetCurrencyBaseURI(Scene scene) {
            return scene.RegionInfo.ServerURI;
        }
        
        #endregion // IRegionModuleBase Interface
        
        #region ISharedRegionModule Interface

        public void PostInitialise()
        {
            m_opensimVersion = OpenSim.VersionInfo.Version;
            m_opensimVersionNumber = OpenSim.VersionInfo.VersionNumber;
            char[] delimiterChars = { '.' };
            string[] numbers = m_opensimVersionNumber.Split(delimiterChars, System.StringSplitOptions.RemoveEmptyEntries);
            int vn1 = int.Parse(numbers[0]);
            int vn2 = int.Parse(numbers[1]);
            int vn3 = int.Parse(numbers[2]);
            if ((vn1 > 0) || (vn2 > 9) || (vn2 == 9 && vn3 > 0)) {
                // 0.9.1 and beyond are the new land pass flow.
                // Note, there are some early versions of 0.9.1 before any release candidate which do not have the new
                // flow, but we can't easily determine those and no one should be running those in production.
                m_newLandPassFlow = true;
            } else if (vn1 == 0 && vn2 == 9 && vn3 == 0) {
                // 0.9.0-release pulled in 0.9.1 changes and is new flow, but rest of 0.9.0 is not.
                // assume dev on 0.9.0.1, 0.9.0.2 will be new flow
                int vn4 = int.Parse(numbers[3]);
                if (vn4 > 0) {
                    // 0.9.0.1, 0.9.0.2, etc.
                    m_newLandPassFlow = true;
                } else {
                    // Need to pull version flavour and check it.
                    // TODO: may need to split on spaces or hyphens and then pull last field because flavour is not friggin public
                    char[] dChars = { '-', ' ' };
                    string[] versionParts = m_opensimVersion.Split(dChars, System.StringSplitOptions.RemoveEmptyEntries);
                    string flavour = versionParts[versionParts.Length - 1];     // TODO: do we every have to worry about this being length 0?
                    if (flavour == OpenSim.VersionInfo.Flavour.Release.ToString()) {
                        // 0.9.0 release
                        m_newLandPassFlow = true;
                    }
                }
                // TOOD: Unclear if post-fixes is a necessary flavour check yet.
            }
        }
        
        #endregion // ISharedRegionModule Interface

        #region IMoneyModule Members
        
        // Dummy IMoneyModule interface which is not yet used.
        public void MoveMoney(UUID fromAgentID, UUID toAgentID, int amount, string text)
        {
            // Call the newer MoveMoney funciton with a transaction type and text string so that we report
            // errors on whatever this unimplemented transaction flow is.
            MoveMoney(fromAgentID, toAgentID, amount, (MoneyTransactionType)TransactionType.MOVE_MONEY_GENERAL, text);
        }
        
        // New IMoneyModule interface added in 0.9.1 dev to support ParcelBuyPass implementation
        // Prior to 0.9.1, see ParcelBuyPass event
        // MoveMoney(remote_client.AgentId, ldata.OwnerID, cost,MoneyTransactionType.LandPassSale, payDescription)
        public bool MoveMoney(UUID fromUser, UUID toUser, int amount, MoneyTransactionType type, string text)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] MoveMoney from {0}, to {1} amount {2} type {3} desc {4}", fromUser, toUser, amount, type, text);
            
            // Just approve if the amount is 0 or negative as no money needs to be moved.
            // Negative is undefined and may need to be re-assessed as other transaction types are added to this flow.
            if (amount <= 0) {
                return true;
            }
            
            // Grab info all vars we'll def need
            TransactionType txnType = TransactionType.MOVE_MONEY_GENERAL;
            string txnTypeString = "MoveMoneyGeneral";
            //Scene s = LocateSceneClientIn(agentID);
            //string regionID = s.RegionInfo.RegionID.ToString();
            Scene s = null;
            string regionName = String.Empty;
            UUID regionID = UUID.Zero;
            UUID partID = UUID.Zero;
            string partName = String.Empty;
            string partDescription = String.Empty;
            UUID categoryID = UUID.Zero;
            uint localID = 0;
            OSDMap descMap = null;
            string description = String.Empty;
            
            // TODO: switch on transaction type
            switch (type) {
                case MoneyTransactionType.LandPassSale:
                    // User buys time-limited pass to access parcel
                    // Comes through ParcelBuyPass event pre 0.9.1; MoveMoney 0.9.1 on.
                    txnTypeString = "ParcelBuyPass";
                    txnType = TransactionType.USER_BUYS_LANDPASS;
                    
                    // Try to retrieve the info we want by parsing the text string
                    // text = String.Format("Parcel '{0}' at region '{1} {2:0.###} hours access pass", ldata.Name, regionName, ldata.PassHours);
                    string[] delimiterStrings = { "Parcel '", "' at region '", " hours access pass" };
                    string[] pieces = text.Split(delimiterStrings, 5, System.StringSplitOptions.None);
                    partName = pieces[1];
                    int finalSpaceIndex = pieces[2].LastIndexOf(' ');
                    string test = pieces[2].Substring(0,0);
                    regionName = pieces[2].Substring(0, finalSpaceIndex);
                    string passHours = pieces[2].Substring(finalSpaceIndex + 1);
                    m_log.DebugFormat("[GLOEBITMONEYMODULE] MoveMoney for ParcelBuyPass on Parcel: [{0}], Region: [{1}]; for [{2}] hours", partName, regionName, passHours);
                    // Set our own description for uniformity
                    description = String.Format("{0} hour LandPass purchased for parcel {1} on {2}, {3}", passHours, partName, regionName, m_gridnick);
                    
                    // Retrieve scene from name
                    s = GetSceneByName(regionName);
                    regionID = s.RegionInfo.RegionID;
                    m_log.DebugFormat("[GLOEBITMONEYMODULE] region uuid {0}", regionID);
                    
                    // Since we don't have a parcelID, use info we have to locate the parcel related to this transaction.
                    // ILandObject parcel = s.LandChannel.GetLandObject(ParcelLocalID);
                    List <ILandObject> pl = s.LandChannel.AllParcels();
                    int found = 0;
                    ILandObject parcel = null;
                    string phStr;
                    foreach (ILandObject lo in pl) {
                        phStr = String.Format("{0:0.###}", lo.LandData.PassHours);
                        if (lo.LandData.Name == partName &&
                        phStr == passHours &&
                        lo.LandData.PassPrice == amount &&
                        lo.LandData.OwnerID == toUser &&
                        (lo.LandData.Flags & (uint)ParcelFlags.UsePassList) != 0)
                        {
                            m_log.DebugFormat("[GLOEBITMONEYMODULE] FOUND PARCEL {0} {1} {2} {3} {4}", lo.LandData.Name, lo.LandData.LocalID, lo.LandData.GlobalID, lo.LandData.PassPrice, lo.LandData.PassHours);
                            found++;
                            parcel = lo;
                        }
                    }
                    m_log.DebugFormat("[GLOEBITMONEYMODULE] found {0} matching parcels", found);
                    
                    // Create the descMap extra details using the parcel we've found if we've found exactly one match.
                    int parcelLocalID = 0;
                    if (found == 1) {
                        descMap = buildBaseTransactionDescMap(regionName, regionID.ToString(), "ParcelBuyPass", parcel.LandData);
                        partID = parcel.LandData.GlobalID;
                        partDescription = parcel.LandData.Description;
                        // NOTE: using category & localID to retrieve parcel on callback in pre 0.9.1 flow.
                        // Storing same data in txn for consistency.
                        // Should consider storing all LandData in assetMap to track PassHours too.
                        // Don't need these in new 0.9.1 flow for ParcelBuyPass.  Enact handled outside our control.
                        categoryID = regionID;
                        localID = (uint)parcel.LandData.LocalID;
                    } else {
                        descMap = buildBaseTransactionDescMap(regionName, regionID.ToString(), "ParcelBuyPass");
                    }
                    break;
                //case MoneyTransactionType.ClassifiedCharge: ---- Will likely implement group join fees here.
                //    // Classified Ad Fee
                //    description = String.Format("Classified Ad Fee on {0}, {1}", regionname, m_gridnick);
                //    txnTypeString = "ClassifiedAdFee";
                //    txnType = TransactionType.FEE_CLASSIFIED_AD;
                //    break;
                default:
                    // Other - not in core at time of writing.
                    //description = String.Format("Fee (type {0}) on {1}, {2}", type, regionname, m_gridnick);
                    txnTypeString = "MoveMoneyGeneral";
                    txnType = TransactionType.MOVE_MONEY_GENERAL;
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] MoveMoney for MoneyTransactionType [{0}] with description [{1}] not implemented.  Please contact Gloebit and ask them to implement.", type, text);
                    alertUsersTransactionPreparationFailure(TransactionType.MOVE_MONEY_GENERAL, TransactionPrecheckFailure.SALE_TYPE_INVALID, LocateClientObject(fromUser));
                    return false;
                    //break;
            }
            
            // Submit transaction and return result
            GloebitAPI.Transaction txn = buildTransaction(transactionID: UUID.Zero, transactionType: txnType,
                                                          payerID: fromUser, payeeID: toUser, amount: amount, subscriptionID: UUID.Zero,
                                                          partID: partID, partName: partName, partDescription: partDescription,
                                                          categoryID: categoryID, localID: localID, saleType: 0);
            if (txn == null) {
                // build failed, likely due to a reused transactionID.  Shouldn't happen.
                alertUsersTransactionPreparationFailure(TransactionType.USER_BUYS_LANDPASS, TransactionPrecheckFailure.EXISTING_TRANSACTION_ID, LocateClientObject(fromUser));
                return false;
            }
            bool transaction_result = SubmitSyncTransaction(txn, description, descMap);
            if (transaction_result) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] ParcelBuyPass SubmitSyncTransaction successful {0}", txn.TransactionID.ToString());
            }
            return transaction_result;
        }
        
        // Old IMoneyModule interface designed for LLGiveMoney instead of LLTransferLindenDollars.  Deprecated.
        public bool ObjectGiveMoney(UUID objectID, UUID fromID, UUID toID, int amount)
        {
            string reason = String.Empty;
            UUID txnID = UUID.Zero;
            return ObjectGiveMoney(objectID, fromID, toID, amount, txnID, out reason);
        }
        
        // New IMoneyModule interface.
        // If called from LLGiveMoney, txnID is UUID.Zero and reason is thrown away.
        // If called from LLTransferLindenDollars, txnID is set and reason is returned to script if function returns false.
        public bool ObjectGiveMoney(UUID objectID, UUID fromID, UUID toID, int amount, UUID txnID, out string reason)
        {
            string description = String.Format("Object {0} pays {1}", resolveObjectName(objectID), resolveAgentName(toID));
            m_log.DebugFormat("[GLOEBITMONEYMODULE] ******************ObjectGiveMoney {0}", description);
            
            reason = String.Empty;
            
            SceneObjectPart part = null;
            string regionname = "";
            string regionID = "";
            
            // Try to get part from scene the payee is in first.
            // TODO: is this worth trying before we just look for the part?
            Scene s = LocateSceneClientIn(toID);
            if (s != null) {
                part = s.GetSceneObjectPart(objectID);
                regionname = s.RegionInfo.RegionName;
                regionID = s.RegionInfo.RegionID.ToString();
            }
            // If we haven't found the part, cycle through the scenes
            if (part == null) {
                lock (m_scenel)
                {
                    foreach (Scene _scene in m_scenel.Values)
                    {
                        part = _scene.GetSceneObjectPart(objectID);
                        if (part != null) {
                            // TODO: Do we need to verify that this is not a child part?
                            s = _scene;
                            regionname = s.RegionInfo.RegionName;
                            regionID = s.RegionInfo.RegionID.ToString();
                            break;
                        }
                    }
                }
            }
            
            // If we still haven't found the part, we have a problem
            if (part == null) {
                reason = String.Format("[GLOEBITMONEYMODULE] ObjectGiveMoney - Can not locate scripted object with id:{0} which triggered payment request.", objectID);
                m_log.Error(reason);
                return false;
            }
            
            // Check subscription table.  If not exists, send create call to Gloebit
            m_log.DebugFormat("[GLOEBITMONEYMODULE] ObjectGiveMoney - looking for local subscription");
            GloebitAPI.Subscription sub = GloebitAPI.Subscription.Get(objectID, m_key, m_apiUrl);
            if (sub == null) {
                // Don't create unless the object has a name and description
                // Make sure Name and Description are not null to avoid pgsql issue with storing null values
                // Make sure neither are empty as they are required by Gloebit to create a subscription
                if (String.IsNullOrEmpty(part.Name) || String.IsNullOrEmpty(part.Description)) {
                     m_log.InfoFormat("[GLOEBITMONEYMODULE] ObjectGiveMoney - Can not create local subscription because part name or description is blank - Name:{0} Description:{1}", part.Name, part.Description);
                     // Send message to the owner to let them know they must edit the object and add a name and description
                     String imMsg = String.Format("Object with auto-debit script is missing a name or description.  Name and description are required by Gloebit in order to create a subscription for this auto-debit object.  Please enter a name and description in the object.  Current values are Name:[{0}] and Description:[{1}].", part.Name, part.Description);
                     sendMessageToClient(LocateClientObject(fromID), imMsg, fromID);
                     reason = "Owner has not yet created a subscription and object name or description are blank.  Name and Description are required.";
                     return false;
                }
                m_log.DebugFormat("[GLOEBITMONEYMODULE] ObjectGiveMoney - creating local subscription for {0}", part.Name);
                // Create local sub
                sub = GloebitAPI.Subscription.Init(objectID, m_key, m_apiUrl, part.Name, part.Description);
            }
            if (sub.SubscriptionID == UUID.Zero) {
                m_log.DebugFormat("[GLOEBITMONEYMODULE] ObjectGiveMoney - SID is ZERO -- calling GloebitAPI Create Subscription");
                
                // Message to user that we are creating the subscription.
                alertUsersSubscriptionTransactionFailedForSubscriptionCreation(fromID, toID, amount, sub);
                
                // Add this user to map of waiting for sub to creat auth.
                // TODO: signify that this was from a txn attempt so that we send dialog.
                IClientAPI client = LocateClientObject(fromID);
                if (client != null) {
                    lock (m_authWaitingForSubMap) {
                        m_authWaitingForSubMap[objectID] = client;
                    }
                }
                
                // call api to have Gloebit create
                m_api.CreateSubscription(sub, BaseURI);
                
                // return false so this the current transaciton terminates and object is alerted to failure
                reason = "Owner has not yet created a subscription.";
                return false;
            }
            
            // Check that user has authed Gloebit and token is on file.
            GloebitAPI.User payerUser = GloebitAPI.User.Get(m_api, fromID);
            if (payerUser != null && String.IsNullOrEmpty(payerUser.GloebitToken)) {
                // send message asking to auth Gloebit.
                alertUsersSubscriptionTransactionFailedForGloebitAuthorization(fromID, toID, amount, sub);
                reason = "Owner has not authorized this app with Gloebit.";
                return false;
            }
            
            // Checks done.  Ready to build and submit transaction.
            
            OSDMap descMap = buildBaseTransactionDescMap(regionname, regionID, "ObjectGiveMoney", part);
            
            GloebitAPI.Transaction txn = buildTransaction(transactionID: txnID, transactionType: TransactionType.OBJECT_PAYS_USER,
                                                          payerID: fromID, payeeID: toID, amount: amount, subscriptionID: sub.SubscriptionID,
                                                          partID: objectID, partName: part.Name, partDescription: part.Description,
                                                          categoryID: UUID.Zero, localID: 0, saleType: 0);
            
            if (txn == null) {
                // build failed, likely due to a reused transactionID.  Shouldn't happen, but possible in ObjectGiveMoney.
                IClientAPI payerClient = LocateClientObject(fromID);
                alertUsersTransactionPreparationFailure(TransactionType.OBJECT_PAYS_USER, TransactionPrecheckFailure.EXISTING_TRANSACTION_ID, payerClient);
                reason = "Transaction submitted with ID for existing transaction.";
                return false;
            }
            
            // This needs to be a sync txn because the object recieves the bool response and uses it as txn success or failure.
            // Todo: remove callbacks from this transaction since we don't use them.
            bool give_result = SubmitSyncTransaction(txn, description, descMap);

            if (!give_result) {
                reason = "Transaction failed during processing.  See logs or text chat for more details.";
                // TODO: pass failure back through SubmitSyncTransaction and design system to pull error string from a failure.
            }
            return give_result;
        }

        public int GetBalance(UUID agentID)
        {
            m_log.DebugFormat("[GLOEBITMONEYMODULE] GetBalance for agent {0}", agentID);
            
            // forceAuthOnInvalidToken = false.  If another system is calling this frequently, it will prevent spamming of users with auth requests.
            // client is null as it is only needed to request auth.
            return (int)GetAgentBalance(agentID, null, false);
        }

        // Please do not refactor these to be just one method
        // Existing implementations need the distinction
        //
        
        // Checks a user's balance to ensure they can cover an upload fee.
        // NOTE: we need to force a balance update because this immediately uploads the asset if we return true.  It does not wait for the charge response.
        // For more details, see:
        // --- BunchOfCaps.NewAgentInventoryRequest
        // --- AssetTransactionModule.HandleUDPUploadRequest
        // --- BunchOfCaps.UploadCompleteHandler
        public bool UploadCovered(UUID agentID, int amount)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] UploadCovered for agent {0}, price {1}", agentID, amount);
            
            IClientAPI client = LocateClientObject(agentID);
            double balance = 0.0;
            
            // force a balance update, then check against amount.
            // Retrieve balance from Gloebit if authed.  Reqeust auth if not authed.  Send purchase url if authed but lacking funds to cover amount.
            balance = UpdateBalance(agentID, client, amount);
            
            if (balance < amount) {
                return false;
            }
            return true;
        }
        
        // Checks a user's balance to ensure they can cover a fee.
        // For more details, see:
        // --- GroupsModule.CreateGroup
        // --- UserProfileModule.ClassifiedInfoUpdate
        public bool AmountCovered(UUID agentID, int amount)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] AmountCovered for agent {0}, price {1}", agentID, amount);
            
            IClientAPI client = LocateClientObject(agentID);
            double balance = 0.0;
            
            // force a balance update, then check against amount.
            // Retrieve balance from Gloebit if authed.  Reqeust auth if not authed.  Send purchase url if authed but lacking funds to cover amount.
            balance = UpdateBalance(agentID, client, amount);
            
            if (balance < amount) {
                return false;
            }
            return true;
        }

        // Please do not refactor these to be just one method
        // Existing implementations need the distinction
        //
        public void ApplyCharge(UUID agentID, int amount, MoneyTransactionType type, string extraData)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] ApplyCharge for agent {0} with extraData {1}", agentID, extraData);
            // As far as I can tell, this is not used in recent versions of OpenSim.
            // For backwards compatibility, call new ApplyCharge func
            ApplyCharge(agentID, amount, type);
        }

        // Charge user a fee
        // Group Creation
        // --- GroupsModule.CreateGroup
        // --- type = MoneyTransactionType.GroupCreate
        // --- Do not throw exception on error.  Group has already been created.  Response has not been sent to viewer.  Unclear what would fail.  Log error instead.
        // Classified Ad fee
        // --- UserProfileModule.ClassifiedInfoUpdate
        // --- type = MoneyTransactionType.ClassifiedCharge
        // --- Throw exception on failure.  Classified ad has not been created yet.
        public void ApplyCharge(UUID agentID, int amount, MoneyTransactionType type)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] ApplyCharge for agent {0}, MoneyTransactionType {1}", agentID, type);
            
            if (amount <= 0) {
                // TODO: Should we report this?  Should we ever get here?
                return;
            }
            
            Scene s = LocateSceneClientIn(agentID);
            string agentName = resolveAgentName(agentID);
            string regionname = s.RegionInfo.RegionName;
            string regionID = s.RegionInfo.RegionID.ToString();
            
            
            string description = String.Empty;
            string txnTypeString = "GeneralFee";
            TransactionType txnType = TransactionType.FEE_GENERAL;
            switch (type) {
                case MoneyTransactionType.GroupCreate:
                    // Group creation fee
                    description = String.Format("Group Creation Fee on {0}, {1}", regionname, m_gridnick);
                    txnTypeString = "GroupCreationFee";
                    txnType = TransactionType.FEE_GROUP_CREATION;
                    break;
                case MoneyTransactionType.ClassifiedCharge:
                    // Classified Ad Fee
                    description = String.Format("Classified Ad Fee on {0}, {1}", regionname, m_gridnick);
                    txnTypeString = "ClassifiedAdFee";
                    txnType = TransactionType.FEE_CLASSIFIED_AD;
                    break;
                default:
                    // Other - not in core at type of writing.
                    description = String.Format("Fee (type {0}) on {1}, {2}", type, regionname, m_gridnick);
                    txnTypeString = "GeneralFee";
                    txnType = TransactionType.FEE_GENERAL;
                    break;
            }
            
            OSDMap descMap = buildBaseTransactionDescMap(regionname, regionID.ToString(), txnTypeString);
            
            GloebitAPI.Transaction txn = buildTransaction(transactionID: UUID.Zero, transactionType: txnType,
                                                          payerID: agentID, payeeID: UUID.Zero, amount: amount, subscriptionID: UUID.Zero,
                                                          partID: UUID.Zero, partName: String.Empty, partDescription: String.Empty,
                                                          categoryID: UUID.Zero, localID: 0, saleType: 0);
            
            if (txn == null) {
                // build failed, likely due to a reused transactionID.  Shouldn't happen.
                IClientAPI payerClient = LocateClientObject(agentID);
                alertUsersTransactionPreparationFailure(txnType, TransactionPrecheckFailure.EXISTING_TRANSACTION_ID, payerClient);
                return;
            }
            
            bool transaction_result = SubmitTransaction(txn, description, descMap, false);
            
            if (!transaction_result) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] ApplyCharge failed to create HTTP Request for [{0}] from agent: [{1}] -- txnID: [{2}] -- agent likely received benefit without being charged.", description, agentID, txn.TransactionID.ToString());
            } else {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] ApplyCharge Transaction queued {0}", txn.TransactionID.ToString());
            }
        }

        // Process the upload charge
        // NOTE: Do not throw exception on failure.  Delivery is complete, but BunchOfCaps.m_FileAgentInventoryState has not been reset to idle.  Fire off an error log instead.
        // For more details, see:
        // --- Scene.AddUploadedInventoryItem "Asset upload"
        // --- BunchOfCaps.UploadCompleteHandler
        public void ApplyUploadCharge(UUID agentID, int amount, string text)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] ApplyUploadCharge for agent {0}, amount {1}, for {2}", agentID, amount, text);
            
            if (amount <= 0) {
                // TODO: Should we report this?  Should we ever get here?
                return;
            }
            
            Scene s = LocateSceneClientIn(agentID);
            string agentName = resolveAgentName(agentID);
            string regionname = s.RegionInfo.RegionName;
            string regionID = s.RegionInfo.RegionID.ToString();
            
            string description = String.Format("Asset Upload Fee on {0}, {1}", regionname, m_gridnick);
            string txnTypeString = "AssetUploadFee";
            TransactionType txnType = TransactionType.FEE_UPLOAD_ASSET;
            
            OSDMap descMap = buildBaseTransactionDescMap(regionname, regionID.ToString(), txnTypeString);
            
            GloebitAPI.Transaction txn = buildTransaction(transactionID: UUID.Zero, transactionType: txnType,
                                                          payerID: agentID, payeeID: UUID.Zero, amount: amount, subscriptionID: UUID.Zero,
                                                          partID: UUID.Zero, partName: String.Empty, partDescription: String.Empty,
                                                          categoryID: UUID.Zero, localID: 0, saleType: 0);
            
            if (txn == null) {
                // build failed, likely due to a reused transactionID.  Shouldn't happen.
                IClientAPI payerClient = LocateClientObject(agentID);
                alertUsersTransactionPreparationFailure(txnType, TransactionPrecheckFailure.EXISTING_TRANSACTION_ID, payerClient);
                return;
            }
            
            bool transaction_result = SubmitTransaction(txn, description, descMap, false);
            
            if (!transaction_result) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] ApplyUploadCharge failed to create HTTP Request for [{0}] from agent: [{1}] -- txnID: [{2}] -- agent likely received benefit without being charged.", description, agentID, txn.TransactionID.ToString());
            } else {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] ApplyUploadCharge Transaction queued {0}", txn.TransactionID.ToString());
            }
        }

        // property to store fee for uploading assets
        // NOTE: This is the prim BaseCost.  If mesh, this is calculated in BunchOfCaps
        // For more details, see:
        // --- BunchOfCaps.NewAgentInventoryRequest
        // --- AssetTransactionModule.HandleUDPUploadRequest
        // Returns the PriceUpload set by the economy section of the config
        public int UploadCharge
        {
            get { return PriceUpload; }
        }

        // property to store fee for creating a group
        // For more details, see:
        // --- GroupsModule.CreateGroup
        public int GroupCreationCharge
        {
            get { return PriceGroupCreate; }    // TODO: PriceGroupCreate is defaulted to -1, not 0.  Why is this?  How should we handle this?
        }

//#pragma warning disable 0067
        public event ObjectPaid OnObjectPaid;
//#pragma warning restore 0067

        #endregion // IMoneyModule members

        #region GMM Transaction Submission

        /***
         * All commerce flows must 
         * 1. Have a transaction type defined
         * 2. build a transaction (which includes a set of information common to all transactions)
         * 3. build a transaction description map of additional dynamic information specific to this transaction
         * 4. submit the transaction (with description and description map
         *** TODO: fill in rest of requirements (enact etc).
         ***/
        
        /// <summary>
        /// Build a GloebitAPI.Transaction for a specific TransactionType.  This Transaction will be:
        /// --- persistently stored
        /// --- used for submitting to Gloebit via the TransactU2U endpoint via <see cref="SubmitTransaction"/> and <see cref="SubmitSyncTransaction"/> functions,
        /// --- used for processing transact enact/consume/cancel callbacks to handle any other OpenSim components of the transaction(such as object delivery),
        /// --- used for tracking/reporting/analysis
        /// </summary>
        /// <param name="transactionID">UUID to use for this transaction.  If UUID.Zero, a random UUID is chosen.</param>
        /// <param name="transactionType">enum from OpenSim defining the type of transaction (buy object, pay object, pay user, object pays user, etc).  This will not affect how Gloebit process the monetary component of a transaction, but is useful for easily varying how OpenSim should handle processing once funds are transfered.</param>
        /// <param name="payerID">OpenSim UUID of agent sending gloebits.</param>
        /// <param name="payeeID">OpenSim UUID of agent receiving gloebits.  UUID.Zero if this is a fee being paid to the app owner (not a u2u txn).</param>
        /// <param name="amount">Amount of gloebits being transferred.</param>
        /// <param name="subscriptionID">UUID of subscription for automated transactions (Object pays user).  Otherwise UUID.Zero.</param>
        /// <param name="partID">UUID of the object, when transaciton involves an object.  UUID.Zero otherwise.</param>
        /// <param name="partName">string name of the object, when transaciton involves an object.  null otherwise.</param>
        /// <param name="partDescription">string description of the object, when transaciton involves an object.  String.Empty otherwise.</param>
        /// <param name="categoryID">UUID of folder in object used when transactionType is ObjectBuy and saleType is copy.  UUID.Zero otherwise.  Required by IBuySellModule.</param>
        /// <param name="localID">uint region specific id of object used when transactionType is ObjectBuy.  0 otherwise.  Required by IBuySellModule.</param>
        /// <param name="saleType">int differentiating between orginal, copy or contents for ObjectBuy.  Required by IBuySellModule to process delivery.</param>
        /// <returns>GloebitAPI.Transaction created. if successful.</returns>
        private GloebitAPI.Transaction buildTransaction(UUID transactionID, TransactionType transactionType, UUID payerID, UUID payeeID, int amount, UUID subscriptionID, UUID partID, string partName, string partDescription, UUID categoryID, uint localID, int saleType)
        {
            // TODO: we should store "transaction description" with the Transaction?
            
            bool isRandomID = false;
            if (transactionID == UUID.Zero) {
                // Create a transaction ID
                transactionID = UUID.Random();
                isRandomID = true;
            }
            
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
                case TransactionType.USER_BUYS_LAND:
                    // 5002 - OnLandBuy
                    transactionTypeString = "User Buys Land";
                    break;
                case TransactionType.USER_BUYS_LANDPASS:
                    // 5006 - OnParcelBuyPass pre 0.9.1; MoveMoney after;
                    transactionTypeString = "User Buys Land Pass";
                    break;
                case TransactionType.FEE_GROUP_CREATION:
                    // 1002 - ApplyCharge
                    transactionTypeString = "Group Creation Fee";
                    if (payeeID == UUID.Zero) {
                        payeeName = "App Owner";
                    }
                    break;
                case TransactionType.FEE_UPLOAD_ASSET:
                    // 1101 - ApplyUploadCharge
                    transactionTypeString = "Asset Upload Fee";
                    if (payeeID == UUID.Zero) {
                        payeeName = "App Owner";
                    }
                    break;
                case TransactionType.FEE_CLASSIFIED_AD:
                    // 1103 - ApplyCharge
                    transactionTypeString = "Classified Ad Fee";
                    if (payeeID == UUID.Zero) {
                        payeeName = "App Owner";
                    }
                    break;
                case TransactionType.FEE_GENERAL:
                    // 1104 - ApplyCharge - catch all in case there are modules which enable fees which are not used in the core.
                    transactionTypeString = "General Fee";
                    if (payeeID == UUID.Zero) {
                        payeeName = "App Owner";
                    }
                    break;
                default:
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] buildTransaction failed --- unknown transaction type: {0}", transactionType);
                    // TODO: should we throw an exception?  return null?  just continue?
                    break;
            }
            
            // Storing a null field in pgsql fails, so ensure partName and partDescription are not null incase those are not properly set to String.Empty when blank
            if (partName == null) {
                partName = String.Empty;
            }
            if (partDescription == null) {
                partDescription = String.Empty;
            }
            
            GloebitAPI.Transaction txn = GloebitAPI.Transaction.Create(transactionID, payerID, payerName, payeeID, payeeName, amount, (int)transactionType, transactionTypeString, isSubscriptionDebit, subscriptionID, partID, partName, partDescription, categoryID, localID, saleType);
            
            if (txn == null && isRandomID) {
                // Try one more time in case the incredibly unlikely event of a UUID.Random overlap has occurred.
                transactionID = UUID.Random();
                txn = GloebitAPI.Transaction.Create(transactionID, payerID, payerName, payeeID, payeeName, amount, (int)transactionType, transactionTypeString, isSubscriptionDebit, subscriptionID, partID, partName, partDescription, categoryID, localID, saleType);
            }
            
            return txn;
        }
        
        /// <summary>
        /// Submits a GloebitAPI.Transaction to gloebit for processing and provides any necessary feedback to user/platform.
        /// --- Must call buildTransaction() to create argument 1.
        /// --- Must call buildBaseTransactionDescMap() to create argument 3.
        /// </summary>
        /// <param name="txn">GloebitAPI.Transaction created from buildTransaction().  Contains vital transaction details.</param>
        /// <param name="description">Description of transaction for transaction history reporting.</param>
        /// <param name="descMap">Map of platform, location & transaction descriptors for tracking/querying and transaciton history details.  For more details, <see cref="GloebitMoneyModule.buildBaseTransactionDescMap"/> helper function.</param>
        /// <param name="u2u">boolean declaring whether this is a user-to-app (false) or user-to-user (true) transaction.</param>
        /// <returns>
        /// true if async transactU2U web request was built and submitted successfully; false if failed to submit request.
        /// If true:
        /// --- IAsyncEndpointCallback transactU2UCompleted should eventually be called with additional details on state of request.
        /// --- IAssetCallback processAsset[Enact|Consume|Cancel]Hold may eventually be called dependent upon processing.
        /// </returns>
        private bool SubmitTransaction(GloebitAPI.Transaction txn, string description, OSDMap descMap, bool u2u)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] SubmitTransaction Txn: {0}, from {1} to {2}, for amount {3}, transactionType: {4}, description: {5}", txn.TransactionID, txn.PayerID, txn.PayeeID, txn.Amount, txn.TransactionType, description);
            alertUsersTransactionBegun(txn, description);
            
            // TODO: Update all the alert funcs to handle fees properly.
            
            // TODO: Should we wrap TransactU2U or request.BeginGetResponse in Try/Catch?
            bool result = false;
            if (u2u) {
                result = m_api.TransactU2U(txn, description, descMap, GloebitAPI.User.Get(m_api, txn.PayerID), GloebitAPI.User.Get(m_api, txn.PayeeID), resolveAgentEmail(txn.PayeeID), BaseURI);
            } else {
                result = m_api.Transact(txn, description, descMap, GloebitAPI.User.Get(m_api, txn.PayerID), BaseURI);
            }
            
            if (!result) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] SubmitTransaction failed to create HttpWebRequest in GloebitAPI.TransactU2U");
                alertUsersTransactionFailed(txn, GloebitAPI.TransactionStage.SUBMIT, GloebitAPI.TransactionFailure.SUBMISSION_FAILED, String.Empty);
            } else {
                alertUsersTransactionStageCompleted(txn, GloebitAPI.TransactionStage.SUBMIT, String.Empty);
            }
            
            return result;
        }
        
        /// <summary>
        /// Submits a GloebitAPI.Transaction usings synchronous web requests to gloebit for processing and provides any necessary feedback to user/platform.
        /// Rather than solely receiving a "submission" response, TransactU2UCallback happens during request, and receives transaction success/failure response.
        /// --- Must call buildTransaction() to create argument 1.
        /// --- Must call buildBaseTransactionDescMap() to create argument 3.
        /// *** NOTE *** Only use this function if you need a synchronous transaction success response.  Use SubmitTransaction Otherwise.
        /// </summary>
        /// <param name="txn">GloebitAPI.Transaction created from buildTransaction().  Contains vital transaction details.</param>
        /// <param name="description">Description of transaction for transaction history reporting.</param>
        /// <param name="descMap">Map of platform, location & transaction descriptors for tracking/querying and transaciton history details.  For more details, <see cref="GloebitMoneyModule.buildBaseTransactionDescMap"/> helper function.</param>
        /// <returns>
        /// true if sync transactU2U web request was built and submitted successfully and Gloebit components of transaction were enacted successfully.
        /// false if failed to submit request or if txn failed at any stage prior to successfully enacting Gloebit txn components.
        /// If true:
        /// --- IAsyncEndpointCallback transactU2UCompleted has already been called with additional details on state of request.
        /// --- IAssetCallback processAsset[Enact|Consume|Cancel]Hold will eventually be called by the transaction processor if txn included callbacks.
        /// If false:
        /// --- If stage is any stage after SUBMIT, errors are handled by TransactU2UCompleted callback which was already called.
        /// --- If stage is SUBMIT, errors must be handled by this function
        /// </returns>
        private bool SubmitSyncTransaction(GloebitAPI.Transaction txn, string description, OSDMap descMap)
        {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] SubmitSyncTransaction Txn: {0}, from {1} to {2}, for amount {3}, transactionType: {4}, description: {5}", txn.TransactionID, txn.PayerID, txn.PayeeID, txn.Amount, txn.TransactionType, description);
            alertUsersTransactionBegun(txn, description);
            
            // TODO: Should we wrap TransactU2U or request.GetResponse in Try/Catch?
            GloebitAPI.TransactionStage stage = GloebitAPI.TransactionStage.BUILD;
            GloebitAPI.TransactionFailure failure = GloebitAPI.TransactionFailure.NONE;
            bool result = m_api.TransactU2USync(txn, description, descMap, GloebitAPI.User.Get(m_api, txn.PayerID), GloebitAPI.User.Get(m_api, txn.PayeeID), resolveAgentEmail(txn.PayeeID), BaseURI, out stage, out failure);
            
            if (!result) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] SubmitSyncTransaction failed in stage: {0} with failure: {1}", stage, failure);
                if (stage == GloebitAPI.TransactionStage.SUBMIT) {
                    // currently need to handle these errors here as the TransactU2UCallback is not called unless sumission is successful and we receive a response
                    alertUsersTransactionFailed(txn, GloebitAPI.TransactionStage.SUBMIT, failure, String.Empty);
                }
            } else {
                // TODO: figure out how/where to send this alert in a synchronous transaction.  Maybe it should always come from the API.
                // alertUsersTransactionStageCompleted(txn, GloebitAPI.TransactionStage.SUBMIT, String.Empty);
            }
            return result;
        }

        #region GMM Transaciton Desc Map helpers

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
            addDescMapEntry(descMap, "platform", "version", m_opensimVersion);
            addDescMapEntry(descMap, "platform", "version-number", m_opensimVersionNumber);
            // TODO: Should we add hosting-provider or more?

            // Add base location details
            addDescMapEntry(descMap, "location", "grid-name", m_gridname);
            addDescMapEntry(descMap, "location", "grid-nick", m_gridnick);
            addDescMapEntry(descMap, "location", "region-name", regionname);
            addDescMapEntry(descMap, "location", "region-id", regionID);

            // Add base transaction details
            //// TODO: change arg to toke a TxnTypeID, add that here, and create func to get the string name from a txnTypeId
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
        /// Helper function to build the minimal transaction description sent to the Gloebit transactU2U endpoint.
        /// Used for tracking as well as information provided in transaction histories.
        /// If transaction does not include Parcel.LandData, use the version which takes three paramaters instead.
        /// </summary>
        /// <param name="regionname">Name of the OpenSim region where this transaction is taking place.</param>
        /// <param name="regionID">OpenSim UUID of the region where this transaction is taking place.</param>
        /// <param name="txnType">String describing the type of transaction.  eg. ObjectBuy, PayObject, PayUser, etc.</param>
        /// <param name="pld">Object (as Parcel.LandData) which is involved in this transaction (pass being purchased for it).</param>
        /// <returns>OSDMap to be sent with the transaction request parameters.  Map contains six dictionary entries, each including an OSDArray.</returns>
        private OSDMap buildBaseTransactionDescMap(string regionname, string regionID, string txnType, LandData pld)
        {
            // Build universal base descMap
            OSDMap descMap = buildBaseTransactionDescMap(regionname, regionID, txnType);

            // Add base descMap details for transaciton involving an object/part
            if (descMap != null && pld != null) {
                addDescMapEntry(descMap, "location", "parcel-upper-corner-position", pld.AABBMax.ToString());
                addDescMapEntry(descMap, "location", "parcel-lower-corner-position", pld.AABBMin.ToString());
                addDescMapEntry(descMap, "location", "parcel-area", pld.Area.ToString());
                addDescMapEntry(descMap, "transaction", "parcel-name", pld.Name);
                addDescMapEntry(descMap, "transaction", "parcel-description", pld.Description);
                addDescMapEntry(descMap, "transaction", "parcel-global-id", pld.GlobalID.ToString());
                addDescMapEntry(descMap, "transaction", "parcel-local-id", pld.LocalID.ToString());
                if (pld.IsGroupOwned) {
                    addDescMapEntry(descMap, "transaction", "parcel-group-owner-id", pld.GroupID.ToString());
                } else {
                    addDescMapEntry(descMap, "transaction", "parcel-owner-name", resolveAgentName(pld.OwnerID));
                }
                addDescMapEntry(descMap, "transaction", "parcel-owner-id", pld.OwnerID.ToString());
                addDescMapEntry(descMap, "transaction", "pass-hours", pld.PassHours.ToString());
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

        #endregion // GMM Transaction Desc Map helpers

        #endregion // GMM Transaction Submission

        #region Standalone box enablers only
        
        /*****************************************************************************************************
         * On a standalone grid (single sim server.  not robust)
         * - These functions can handle the the calls to the currency helper-uri if it is configured to point
         *   at the sim.
         * - These functions handle some pre-flight checks which enable a land sales and provide some useful
         *   messaging for the buy-currency and insufficient-funds flows.  Unfortunately, we can not handle
         *   purchasing of currency directly through this flow.
         * - On a robust grid, these calls are not captured by these XmlRpc handlers.  The grid requires
         *   landtool.php and currency.php helper scripts in the directory pointed at by the currency
         *   helper-uri.  The landtool.php can provide the same functionality, but the currency.php file
         *   doesn't have access to the region of the transaction and therefor doesn't know what money
         *   module is enabled on a grid with multiple or in the case of a Gloebit region, if the user
         *   is authorized, so the flow here can not reach parity with the module handling these calls
         *   directly.  We are hoping to work with the core team to improve this interface eventually.
         * - We do provide a currency helper uri and currency symbol in the OpenSim Extras for viewers
         *   which would like to attempt to direct the land and currency calls at the region, but we don't
         *   know if any have implemented this or tested it yet.
         *****************************************************************************************************/

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

            GloebitAPI.User user = GloebitAPI.User.Get(m_api, agentId);
            if (!user.IsAuthed()) {
                IClientAPI client = LocateClientObject(agentId);
                m_api.Authorize(user, client.Name, BaseURI);
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

            GloebitAPI.User u = GloebitAPI.User.Get(m_api, agentId);
            Uri url = m_api.BuildPurchaseURI(BaseURI, u);
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
        
        #endregion // Standalone box enablers only
        
        #region GMM HTTP Callback Entrance Points
        
        /*********************************************************/
        /*** GloebitAPI Required HTTP Callback Entrance Points ***/
        /*********************************************************/

        // <summary>
        // Helper property for retrieving the base URI for HTTP callbacks from Gloebit Service back into GMM
        // Used throughout the GMM to provide the callback URI to Gloebit or provide URLs to the user.
        // The callbacks registered below should be avaialble at this base URI.
        //</summary>
        private Uri BaseURI {
            get {
                if(m_overrideBaseURI != null) {
                    // Overriding default behavior to hardcode callback base uri
                    // Generally used for testing
                    return m_overrideBaseURI;
                } else {
                    return new Uri(GetAnyScene().RegionInfo.ServerURI);
                }
            }
        }

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
            
            UUID parsedAgentId = UUID.Parse(agentId);
            GloebitAPI.User u = GloebitAPI.User.Get(m_api, parsedAgentId);

            m_api.ExchangeAccessToken(u, code, BaseURI);

            m_log.InfoFormat("[GLOEBITMONEYMODULE] authComplete_func started ExchangeAccessToken");
            
            Uri url = m_api.BuildPurchaseURI(BaseURI, u);
            
            Hashtable response = new Hashtable();
            response["int_response_code"] = 200;
            response["str_response_string"] = String.Format("<html><head><title>Gloebit authorized</title></head><body><h2>Gloebit authorized</h2>Thank you for authorizing Gloebit.  You may now close this window and return to OpenSim.<br /><br /><br />You'll now be able spend gloebits from your Gloebit account as the agent you authorized on this OpenSim Grid.<br /><br />If you need gloebits, you can <a href=\"{0}\">purchase them here</a>.</body></html>", url);
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
            m_log.DebugFormat("[GLOEBITMONEYMODULE] transactionState_func **************** Got Callback");
            foreach(DictionaryEntry e in requestData) { m_log.DebugFormat("{0}: {1}", e.Key, e.Value); }
            
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
        /// <param name="requestData">response data from GloebitAPI.Purchase</param>
        private Hashtable buyComplete_func(Hashtable requestData) {
            // TODO: As best I can tell, this is not implemented on the api side.  BuildPurchaseURI sets the return-to query arg to this, but that's
            // not what that query arg does.  That just overrides the default return-to url that is used by the return-to app link on the page.
            // We would have to create a new query arg for this alert, and then we'd need to build the functionality to call that url upon purchase completion.
            // We would probably pass this through and do it when we load the purchase success page.
            // TODO: also, since we have a success page and we're not prepared to allow alternate success pages, this should probably just be used
            // to inform the grid of a balance change and shouldn't produce html.
            
            UUID agentID = UUID.Parse(requestData["agentId"] as string);
            IClientAPI client = LocateClientObject(agentID);
            
            // Update balance in viewer.  Request auth if not authed.  Do not send the purchase url.
            UpdateBalance(agentID, client, 0);
            // TODO: When we implement this, we should supply the balance in the requestData and simply call client.SendMoneyBalance(...)
            
            Hashtable response = new Hashtable();
            response["int_response_code"] = 200;
            response["str_response_string"] = "<html><head><title>Purchase Complete</title></head><body><h2>Purchase Complete</h2>Thank you for purchasing Gloebits.  You may now close this window.</body></html>";
            response["content_type"] = "text/html";
            return response;
        }
        
        #endregion // GMM HTTP Callback Entrance Points
        
        #region GMM IAsyncEndpointCallback Interface
        
        /******************************************/
        /**** IAsyncEndpointCallback Interface ****/
        /******************************************/
        
        /* Completion of flows which make an asynchronous call to the Gloebit services and return without a result.
         * When the Gloebit Service call completes, these are the entrance points for completion
         * of these asynchronous flows
         */
        
        public void LoadAuthorizeUrlForUser(GloebitAPI.User user, Uri authorizeUri)
        {
            // Since we can't launch a website in OpenSim, we have to send the URL via an IM
            IClientAPI client = LocateClientObject(UUID.Parse(user.PrincipalID));
            string title = "AUTHORIZE GLOEBIT";
            string body = "To use Gloebit currency, please authorize Gloebit to link to your avatar's account on this web page:";
            SendUrlToClient(client, title, body, authorizeUri);
        }
        
        public void LoadSubscriptionAuthorizationUrlForUser(GloebitAPI.User user, Uri subAuthUri, GloebitAPI.Subscription sub, bool isDeclined)
        {
            // Since we can't launch a website in OpenSim, we have to send the URL via an IM
            IClientAPI client = LocateClientObject(UUID.Parse(user.PrincipalID));
            // TODO: adjust our wording
            string title = "GLOEBIT Subscription Authorization Request (scripted object auto-debit):";
            string body;
            if (!isDeclined) {
                body = String.Format("To approve or decline the request to authorize this object:\n   {0}\n   {1}\n\nPlease visit this web page:", sub.ObjectName, sub.ObjectID);
            } else {
                body = String.Format("You've already declined the request to authorize this object:\n   {0}\n   {1}\n\nIf you would like to review the request, or alter your response, please visit this web page:", sub.ObjectName, sub.ObjectID);
            }
            SendUrlToClient(client, title, body, subAuthUri);
        }
        
        public void exchangeAccessTokenCompleted(bool success, GloebitAPI.User user, OSDMap responseDataMap)
        {
            UUID agentID = UUID.Parse(user.PrincipalID);
            IClientAPI client = LocateClientObject(agentID);
            
            if (success) {
                // Update the user's balance.
                bool invalidatedToken;
                client.SendMoneyBalance(UUID.Zero, true, new byte[0], (int)m_api.GetBalance(user, out invalidatedToken), 0, UUID.Zero, false, UUID.Zero, false, 0, String.Empty);
                // we have this other version in comments: SendMoneyBalance(IClientAPI client, UUID agentID, UUID SessionID, UUID TransactionID)
            
                // Deliver Purchase URI in case the helper-uri is not working
                Uri url = m_api.BuildPurchaseURI(BaseURI, user);
                SendUrlToClient(client, "Gloebit Authorization Successful", "Buy gloebits you can spend on this grid:", url);
            } else {
                // May want to log an error or retry.
            }
        }
        
        public void transactU2UCompleted(OSDMap responseDataMap, GloebitAPI.User payerUser, GloebitAPI.User payeeUser, GloebitAPI.Transaction txn, GloebitAPI.TransactionStage stage, GloebitAPI.TransactionFailure failure)
        {
            // TODO: should pass success, reason and status through as arguments.  Should probably check tID in GAPI instead of here.
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
            
            // Only used for sending dialog messages for subscription issues.
            // TODO: consider getting client directly in dialog.
            IClientAPI buyerClient = LocateClientObject(txn.PayerID);
            IClientAPI sellerClient = LocateClientObject(txn.PayeeID);
            
            
            // Can/should this be moved to QUEUE with no errors?
            if (success) {
                // If we get here, queuing and early enact were successful.
                // When the processor runs this, we are guaranteed that it will call our enact URI eventually, or succeed if no callback-uris were provided.
                m_log.InfoFormat("[GLOEBITMONEYMODULE].transactU2UCompleted with SUCCESS reason:{0} id:{1}", reason, tID);
                if (reason == "success") {                                  /* successfully queued, early enacted all non-asset transaction parts */
                    // TODO: Once txn can't be null, turn this into an asset check.
                    if (txn == null) {
                        // TODO: examine the early enact without asset-callbacks code path and see if we need to handle other reasons not handled here.
                        // TODO: consider moving this alert to be called from the GAPI.
                        // TODO: we should really provide an interface for checking status or require at least a single callback uri.
                        alertUsersTransactionSucceeded(txn);
                    } else {
                        // Early enact also succeeded, so could add additional details that funds have successfully been transferred.
                        alertUsersTransactionStageCompleted(txn, GloebitAPI.TransactionStage.QUEUE, String.Empty);
                    }
                } else if (reason == "resubmitted") {                       /* transaction had already been created.  resubmitted to queue */
                    m_log.InfoFormat("[GLOEBITMONEYMODULE].transactU2UCompleted resubmitted transaction  id:{0}", tID);
                    alertUsersTransactionStageCompleted(txn, GloebitAPI.TransactionStage.QUEUE, "Transaction resubmitted to queue.");
                } else {                                                    /* Unhandled success reason */
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted unhandled response reason:{0}  id:{1}", reason, tID);
                    alertUsersTransactionStageCompleted(txn, GloebitAPI.TransactionStage.QUEUE, reason);
                }
                return;
            }
            
            m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted with FAILURE reason:{0} status:{1} id:{2}", reason, status, tID);
            
            // Handle errors
            switch (stage) {
                case GloebitAPI.TransactionStage.ENACT_GLOEBIT:
                    // Placed this first as it is an odd case where early-enact failed.
                    // Need to understand if this is guaranteed to be a failure.
                    m_log.InfoFormat("[GLOEBITMONEYMODULE].transactU2UCompleted transaction successfully queued for processing, but failed early enact.  id:{0} reason:{1}", tID, reason);
                    // TODO: Once txn can't be null, turn this into an asset check.
                    if (txn != null) {
                        // TODO: is this success or failure?  This likely means early enact failed.  Could this succeed after queued?
                        // TODO: why is this only for non-null txn?
                        alertUsersTransactionStageCompleted(txn, GloebitAPI.TransactionStage.QUEUE, String.Empty);
                    }
                    // TODO: Should we send a failure alert here?  Could transaction enact successfully?  Need to research this
                    // insufficient-balance; pending probably can't occur; something new?
                    if (failure == GloebitAPI.TransactionFailure.INSUFFICIENT_FUNDS) {
                        m_log.InfoFormat("[GLOEBITMONEYMODULE].transactU2UCompleted transaction failed.  Buyer has insufficent funds.  id:{0}", tID);
                        alertUsersTransactionFailed(txn, stage, failure, String.Empty);
                    } else {
                        // unhandled, so pass reason
                        m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted transaction failed during processing.  reason:{0} id:{1} failure:{2}", reason, tID, failure);
                        alertUsersTransactionFailed(txn, stage, failure, reason);
                    }
                    break;
                case GloebitAPI.TransactionStage.AUTHENTICATE:
                    // failed check of OAUTH2 token - currently only one error causes this - invalid token
                    // Could try a behind the scenes renewal/reauth for expired, and then resubmit, but we don't do that right now, so just fail.
                    m_log.InfoFormat("[GLOEBITMONEYMODULE].transactU2UCompleted transaction failed.  Authentication error.  Invalid token.  id:{0}", tID);
                    alertUsersTransactionFailed(txn, stage, failure, String.Empty);
                    break;
                case GloebitAPI.TransactionStage.VALIDATE:
                    m_log.InfoFormat("[GLOEBITMONEYMODULE].transactU2UCompleted transaction failed.  Validation error.  id:{0}", tID);
                    
                    // Prepare some variables necessary for log messages for some Validation failures.
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
                    
                    switch (failure) {
                        case GloebitAPI.TransactionFailure.FORM_GENERIC_ERROR:                    /* One of many form errors.  something needs fixing.  See reason */
                            // All form errors are errors the app needs to fix
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted Transaction failed.  App needs to fix something. id:{0} failure:{1} reason:{2}", tID, failure, reason);
                            alertUsersTransactionFailed(txn, stage, failure, reason);
                            break;
                        case GloebitAPI.TransactionFailure.FORM_MISSING_SUBSCRIPTION_ID:          /* marked as subscription, but did not include any subscription id */
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted Subscription-id missing from transaction marked as unattended/automated transaction.  transactionID:{0}", tID);
                            // TODO: Do we need to register a subscription, or is this a case we should never end up in?
                            alertUsersTransactionFailed(txn, stage, failure, String.Empty);
                            break;
                        case GloebitAPI.TransactionFailure.SUBSCRIPTION_NOT_FOUND:                /* No subscription exists under id provided */
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted Gloebit can not identify subscription from identifier(s).  transactionID:{0}, subscription-id:{1}, app-subscription-id:{2}", tID, subscriptionIDStr, appSubscriptionIDStr);
                            // TODO: We should wipe this subscription from the DB and re-create it.
                            alertUsersTransactionFailed(txn, stage, failure, String.Empty);
                            break;
                        case GloebitAPI.TransactionFailure.SUBSCRIPTION_AUTH_NOT_FOUND:           /* No sub_auth has been created for this user for this subscription */
                            m_log.InfoFormat("[GLOEBITMONEYMODULE].transactU2UCompleted Gloebit No subscription authorization in place.  transactionID:{0}, subscription-id:{1}, app-subscription-id:{2} PayerID:{3} PayerName:{4}", tID, subscriptionIDStr, appSubscriptionIDStr, txn.PayerID, txn.PayerName);
                            // TODO: Should we store auths so we know if we need to create it or just to ask user to auth it again?
                            // We have a valid subscription, but no subscription auth for this user-id-on-app+token(gloebit_uid) combo
                            // Ask user if they would like to authorize
                            // Don't call CreateSubscriptionAuthorization unless they do.  If this is fraud, the user will not want to see a pending auth.
                            
                            // TODO: should we use SubscriptionIDStr, validate that they match, or get rid of that?
                            
                            alertUsersTransactionFailed(txn, stage, failure, String.Empty);
                            
                            if (buyerClient != null) {
                                Dialog.Send(new CreateSubscriptionAuthorizationDialog(buyerClient, txn.PayerID, txn.PayerName, txn.PartID, txn.PartName, txn.PartDescription, txn.TransactionID, txn.PayeeID, txn.PayeeName, txn.Amount, txn.SubscriptionID, m_api, BaseURI));
                            } else {
                                // TODO: does the message eventually make it if the user is offline?  Is there a way to send a Dialog to a user the next time they log in?
                                // Should we just create the subscription_auth in this case?
                            }
                            break;
                        case GloebitAPI.TransactionFailure.SUBSCRIPTION_AUTH_PENDING:             /* User has not yet approved or declined the authorization for this subscription */
                            // User has been asked and chose to auth already.
                            // Subscription-authorization has already been created.
                            // User has not yet responded to that request, so send a dialog again to ask for auth and allow reporting of fraud.
                            
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] transactU2UCompleted - status:{0} \n subID:{1} appSubID:{2} apiUrl:{3} ", status, subscriptionIDStr, appSubscriptionIDStr, m_apiUrl);
                            
                            alertUsersTransactionFailed(txn, stage, failure, String.Empty);
                            
                            // Send request to user again
                            if (buyerClient != null) {
                                Dialog.Send(new PendingSubscriptionAuthorizationDialog(buyerClient, txn.PayerID, txn.PayerName, txn.PartID, txn.PartName, txn.PartDescription, txn.TransactionID, txn.PayeeID, txn.PayeeName, txn.Amount, txn.SubscriptionID, subscriptionAuthID, m_api, BaseURI));
                            } else {
                                // TODO: does the message eventually make it if the user is offline?  Is there a way to send a Dialog to a user the next time they log in?
                                // Should we just create the subscription_auth in this case?
                            }
                            break;
                        case GloebitAPI.TransactionFailure.SUBSCRIPTION_AUTH_DECLINED:            /* User has declined the authorization for this subscription */
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] transactU2UCompleted - FAILURE -- user declined subscription auth.  id:{0}", tID);
                            
                            alertUsersTransactionFailed(txn, stage, failure, String.Empty);
                            
                            // TODO: We should really send another dialog here like the PendingDialog instead of just a url here.
                            // Send dialog asking user to auth or report --- needs different message.
                            m_log.Info("[GLOEBITMONEYMODULE] TransactU2UCompleted - SUBSCRIPTION_AUTH_DECLINED - requesting SubAuth approval");
                            GloebitAPI.Subscription sub = GloebitAPI.Subscription.GetBySubscriptionID(subscriptionIDStr, m_api.m_url.ToString());
                            m_api.SendSubscriptionAuthorizationToUser(payerUser, subscriptionAuthIDStr, sub, true);
                            
                            break;
                        case GloebitAPI.TransactionFailure.PAYER_ACCOUNT_LOCKED:                  /* Buyer's gloebit account is locked and not allowed to spend gloebits */
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] transactU2UCompleted - FAILURE -- payer account locked.  id:{0}", tID);
                            alertUsersTransactionFailed(txn, stage, failure, String.Empty);
                            break;
                        case GloebitAPI.TransactionFailure.PAYEE_CANNOT_BE_IDENTIFIED:            /* can not identify merchant from params supplied by app */
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted Gloebit could not identify payee from params.  transactionID:{0} payeeID:{1}", tID, payeeUser.PrincipalID);
                            alertUsersTransactionFailed(txn, stage, failure, String.Empty);
                            break;
                        case GloebitAPI.TransactionFailure.PAYEE_CANNOT_RECEIVE:                  /* Seller's gloebit account can not receive gloebits */
                            // TODO: research if/when account is in this state.  Only by admin?  All accounts until merchants?
                            alertUsersTransactionFailed(txn, stage, failure, String.Empty);
                            break;
                        default:
                            // Shouldn't get here.
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted unhandled validation failure:{0}  transactionID:{1}", failure, tID);
                            alertUsersTransactionFailed(txn, stage, failure, reason);
                            break;
                    }
                    break;
                case GloebitAPI.TransactionStage.QUEUE:
                    switch (failure) {
                        case GloebitAPI.TransactionFailure.QUEUEING_FAILED:                     /* failed to queue.  net or processor error */
                            alertUsersTransactionFailed(txn, stage, failure, String.Empty);
                            break;
                        case GloebitAPI.TransactionFailure.RACE_CONDITION:                      /* race condition - already queued */
                            // nothing to tell user.  buyer doesn't need to know it was double submitted
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted race condition.  You double submitted transaction:{0}", tID);
                            break;
                        default:
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted unhandled queueing failure:{0}  transactionID:{1}", failure, tID);
                            alertUsersTransactionFailed(txn, stage, failure, reason);
                            break;
                    }
                    break;
                default:
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE].transactU2UCompleted unhandled Transaciton Stage:{0} failure:{1}  transactionID:{2}", stage, failure, tID);
                    alertUsersTransactionFailed(txn, stage, failure, reason);
                    break;
            }
            return;
        }
        
        public void createSubscriptionCompleted(OSDMap responseDataMap, GloebitAPI.Subscription subscription) {
            
            bool success = (bool)responseDataMap["success"];
            string reason = responseDataMap["reason"];
            string status = responseDataMap["status"];
            
            if (success) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE].createSubscriptionCompleted with SUCCESS reason:{0} status:{1}", reason, status);
                // TODO: Do we need to message any client?
                
                // Ask user to auth.  Do not try to restart stalled txn.
                
                // Look at authWaitingForSubMap - if auth waiting, start that flow.
                IClientAPI client = null;
                bool foundClient = false;
                lock(m_authWaitingForSubMap) {
                    foundClient = m_authWaitingForSubMap.TryGetValue(subscription.ObjectID, out client);
                    if (foundClient) {
                        m_authWaitingForSubMap.Remove(subscription.ObjectID);
                    }
                }
                // TODO: No longer sending dialog.  Should we separate flow from transaction failure and deliver dialog?
                if (foundClient) {
                    GloebitAPI.User user = GloebitAPI.User.Get(m_api, client.AgentId);
                    string agentName = resolveAgentName(client.AgentId);
                    m_api.CreateSubscriptionAuthorization(subscription, user, agentName, BaseURI, client);
                }
                return;
                
            } else if (status == "retry") {                                /* failure could be temporary -- retry. */
                m_log.InfoFormat("[GLOEBITMONEYMODULE].createSubscriptionCompleted with FAILURE but suggested retry.  reason:{0}", reason);
                
                // TODO: Should we retry?  How do we prevent infinite loop?
                
            } else if (status == "failed") {                                /* failure permanent -- requires fixing something. */
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].createSubscriptionCompleted with FAILURE permanently.  reason:{0}", reason);
                
                // TODO: Any action required?
                
            } else {                                                        /* failure - unexpected status */
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].createSubscriptionCompleted with FAILURE - unhandled status:{0} reason:{1}", status, reason);
            }
            // If we added to this map.  remove so we're not leaking memory in failure cases.
            lock(m_authWaitingForSubMap) {
                m_authWaitingForSubMap.Remove(subscription.ObjectID);
            }
            return;
        }
        
        public void createSubscriptionAuthorizationCompleted(OSDMap responseDataMap, GloebitAPI.Subscription sub, GloebitAPI.User user, IClientAPI client) {
            m_log.InfoFormat("[GLOEBITMONEYMODULE].createSubscriptionAuthorizationCompleted");
            
            bool success = (bool)responseDataMap["success"];
            string reason = responseDataMap["reason"];
            string status = responseDataMap["status"];
            
            UUID agentID = UUID.Parse(user.PrincipalID);
//            IClientAPI client = LocateClientObject(agentID);
            
            // TODO: we need to carry the transactionID through and retrieve toID, toName, amount
            //UUID toID = UUID.Zero;
            //UUID transactionID = UUID.Zero;
            //string toName = "testing name";
            //int amount = 47;
            
            if (success) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE].createSubscriptionAuthorizationCompleted with SUCCESS reason:{0} status:{1}", reason, status);
                switch (status) {
                    case "success":
                    case "created":
                    case "duplicate":
                        // grab subscription_authorization_id
                        string subAuthID = responseDataMap["id"];
                        
                        // Send Authorize URL
                        m_api.SendSubscriptionAuthorizationToUser(user, subAuthID, sub, false);
                        
                        break;
                    case "duplicate-and-already-approved-by-user":
                        // TODO: if we have a transaction pending, should we trigger it?
                        break;
                    case "duplicate-and-previously-declined-by-user":
                        // grab subscription_authorization_id
                        string declinedSubAuthID = responseDataMap["id"];
                        // TODO: Should we send a dialog message or just the url?
                        // Send Authorize URL
                        m_api.SendSubscriptionAuthorizationToUser(user, declinedSubAuthID, sub, true);
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
        
        #endregion // GMM IAsyncEndpointCallback Interface
        
        #region GMM IAssetCallback Interface
        
        /***************************************/
        /**** IAssetCallback Interface *********/
        /***************************************/
        
        /* Interface for handling state progression of assets.
         * Any transaction which includes a local asset will receive callbacks at each transaction stage.
         * ENACT -> Funds have been transfered.  Process local asset (generally deliver a product)
         * CONSUME -> Funds have been released to recipient.  Finalize anything necessary.
         * CANCEL -> Transaction has been canceled.  Undo anything necessary
         */
        
        public bool processAssetEnactHold(GloebitAPI.Transaction txn, out string returnMsg) {
            
            // If we've gotten this call, then the Gloebit components have enacted successfully
            // all funds have been transferred.
            alertUsersTransactionStageCompleted(txn, GloebitAPI.TransactionStage.ENACT_GLOEBIT, String.Empty);
            
            switch ((TransactionType)txn.TransactionType) {
                case TransactionType.USER_BUYS_OBJECT:
                    // 5000 - ObjectBuy
                    // Need to deliver the object/contents purchased.
                    IClientAPI payerClient = LocateClientObject(txn.PayerID);
                    bool delivered = deliverObject(txn, payerClient, out returnMsg);
                    if (!delivered) {
                        returnMsg = String.Format("Asset enact failed: {0}", returnMsg);
                        // Local Asset Enact failed - inform user
                        alertUsersTransactionFailed(txn, GloebitAPI.TransactionStage.ENACT_ASSET, GloebitAPI.TransactionFailure.ENACTING_ASSET_FAILED, returnMsg);
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
                        handleObjectPaid(txn.PartID, txn.PayerID, txn.Amount);
                    } else {
                        // This really shouldn't happen, as it would mean that the OpenSim region is not properly set up
                        // However, we won't fail here as expectation is unclear
                        m_log.ErrorFormat("[GLOEBITMONEYMODULE].processAssetEnactHold - IMoneyModule OnObjectPaid event not properly subscribed.  Object payment may have failed.");
                    }
                    break;
                case TransactionType.OBJECT_PAYS_USER:
                    // 5009 - ObjectGiveMoney
                    // nothing to enact.
                    break;
                case TransactionType.USER_BUYS_LAND:
                    // 5002 - OnLandBuy
                    // Need to transfer land
                    bool transferred = transferLand(txn, out returnMsg);
                    if (!transferred) {
                        returnMsg = String.Format("Asset enact failed: {0}", returnMsg);
                        // Local Asset Enact failed - inform user
                        alertUsersTransactionFailed(txn, GloebitAPI.TransactionStage.ENACT_ASSET, GloebitAPI.TransactionFailure.ENACTING_ASSET_FAILED, returnMsg);
                        // remove land asset from map since cancel will not get called
                        // TODO: should we do this here, or adjust ProcessAssetCancelHold to always be called and check state to see if something needs to be undone?
                        lock(m_landAssetMap) {
                            m_landAssetMap.Remove(txn.TransactionID);
                        }
                        return false;
                    }
                    break;
                case TransactionType.USER_BUYS_LANDPASS:
                    // 5006 - OnParcelBuyPass prior to 0.9.1; MoveMoney after;
                    // NOTE: new flow: delivery is outside of our system.  Nothing to enact.
                    if (!m_newLandPassFlow) {
                        bool landPassDelivered = deliverLandPass(txn, out returnMsg);
                        if (!landPassDelivered) {
                            returnMsg = String.Format("Asset enact failed: {0}", returnMsg);
                            // Local Asset Enact failed - inform user
                            alertUsersTransactionFailed(txn, GloebitAPI.TransactionStage.ENACT_ASSET, GloebitAPI.TransactionFailure.ENACTING_ASSET_FAILED, returnMsg);
                            return false;
                        }
                    }
                    break;
                case TransactionType.FEE_GROUP_CREATION:
                    // 1002 - ApplyCharge
                    // Nothing to do since the group was already created.  Ideally, this would create group or finalize creation.
                    break;
                case TransactionType.FEE_UPLOAD_ASSET:
                    // 1101 - ApplyUploadCharge
                    // Nothing to do since the asset was already uploaded.  Ideally, this would upload asset or finalize upload.
                    break;
                case TransactionType.FEE_CLASSIFIED_AD:
                    // 1103 - ApplyCharge
                    // Nothing to do since the ad was already placed.  Ideally, this would create ad finalize ad.
                    break;
                default:
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] processAssetEnactHold called on unknown transaction type: {0}", txn.TransactionType);
                    // TODO: should we throw an exception?  return null?  just continue?
                    // take no action.
                    break;
            }
            
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
                    // nothing to finalize
                    break;
                case TransactionType.USER_BUYS_LAND:
                    // 5002 - OnLandBuy
                    // Remove land asset from map
                    lock(m_landAssetMap) {
                        m_landAssetMap.Remove(txn.TransactionID);
                    }
                    break;
                case TransactionType.USER_BUYS_LANDPASS:
                    // 5006 - OnParcelBuyPass pre 0.9.1; MoveMoney after;
                    // nothing to finalize
                    break;
                case TransactionType.FEE_GROUP_CREATION:
                    // 1002 - ApplyCharge
                    // Nothing to do since the group was already created.  Ideally, this would finalize creation.
                    break;
                case TransactionType.FEE_UPLOAD_ASSET:
                    // 1101 - ApplyUploadCharge
                    // Nothing to do since the asset was already uploaded.  Ideally, this would finalize upload.
                    break;
                case TransactionType.FEE_CLASSIFIED_AD:
                    // 1103 - ApplyCharge
                    // Nothing to do since the ad was already placed.  Ideally, this would finalize ad.
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
            
            // nothing to cancel - either enact of asset failed or was never called if we're here.
            switch ((TransactionType)txn.TransactionType) {
                case TransactionType.USER_BUYS_OBJECT:
                    // 5000 - ObjectBuy
                    // no mechanism for reversing delivery
                    break;
                case TransactionType.USER_PAYS_USER:
                    // 5001 - OnMoneyTransfer - Pay User
                    // nothing to cancel
                    break;
                case TransactionType.USER_PAYS_OBJECT:
                    // 5008 - OnMoneyTransfer - Pay Object
                    // no mechanism for notifying object
                    break;
                case TransactionType.OBJECT_PAYS_USER:
                    // 5009 - ObjectGiveMoney
                    // nothing to cancel
                    break;
                case TransactionType.USER_BUYS_LAND:
                    // 5002 - OnLandBuy
                    // nothing to cancel, if we're here, it is because land was not transferred successfully.
                    break;
                case TransactionType.USER_BUYS_LANDPASS:
                    // 5006 - OnParcelBuyPass pre 0.9.1; MoveMoney after;
                    // nothing to cancel, if we're here, it is because landpass was not granted successfully.
                    break;
                case TransactionType.FEE_GROUP_CREATION:
                    // 1002 - ApplyCharge
                    // TODO: can we delete the group?
                    break;
                case TransactionType.FEE_UPLOAD_ASSET:
                    // 1101 - ApplyUploadCharge
                    // TODO: can we delete the asset?
                    break;
                case TransactionType.FEE_CLASSIFIED_AD:
                    // 1103 - ApplyCharge
                    // TODO: can we delete the ad?
                    break;
                default:
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] processAssetCancelHold called on unknown transaction type: {0}", txn.TransactionType);
                    // TODO: should we throw an exception?  return null?  just continue?
                    // take no action.
                    break;
            }
            
            alertUsersTransactionStageCompleted(txn, GloebitAPI.TransactionStage.CANCEL_ASSET, String.Empty);
            returnMsg = "Asset cancel succeeded";
            return true;
        }

        #endregion // GMM IAssetCallback Interface
        
        #region GMM Asset Enact Helpers
        
        /* Helper functions to handle enacting of local asset components of transactions
         * IE: Deliver the object; Transfer the land.
         */
        
        private bool deliverObject(GloebitAPI.Transaction txn, IClientAPI buyerClient, out string returnMsg) {
            // TODO: this could fail if user logs off right after submission.  Is this what we want?
            // TODO: This basically always fails when you crash opensim and recover during a transaction.  Is this what we want?
            if (buyerClient == null) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].deliverObject FAILED to locate buyer agent.  Agent may have logged out prior to delivery.");
                returnMsg = "Can't locate buyer.";
                return false;
            }
            
            // Retrieve BuySellModule used for dilivering this asset
            Scene s = LocateSceneClientIn(buyerClient.AgentId);
            // TODO: we should be locating the scene the part is in instead of the agent in case the agent moved (to a non Gloebit region) -- maybe store scene ID in asset -- see processLandBuy?
            IBuySellModule module = s.RequestModuleInterface<IBuySellModule>();
            if (module == null) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].deliverObject FAILED to access to IBuySellModule");
                returnMsg = "Can't access IBuySellModule.";
                return false;
            }
            
            // Rebuild delivery params from Asset and attempt delivery of object
            uint localID;
            if (!txn.TryGetLocalID(out localID)) {
                SceneObjectPart part;
                if (s.TryGetSceneObjectPart(txn.PartID, out part)) {
                    localID = part.LocalId;
                } else {
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE].deliverObject FAILED to deliver asset - could not retrieve SceneObjectPart from ID");
                    returnMsg = "Failed to deliver asset.  Could not retrieve SceneObjectPart from ID.";
                    return false;
                }
            }
            bool success = module.BuyObject(buyerClient, txn.CategoryID, localID, (byte)txn.SaleType, txn.Amount);
            if (!success) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].deliverObject FAILED to deliver asset");
                returnMsg = "IBuySellModule.BuyObject failed delivery attempt.";
            } else {
                m_log.InfoFormat("[GLOEBITMONEYMODULE].deliverObject SUCCESS - delivered asset");
                returnMsg = "object delivery succeeded";
            }
            return success;
        }
        
        private bool transferLand(GloebitAPI.Transaction txn, out string returnMsg) {
            //// retrieve LandBuyArgs from assetMap
            bool foundArgs = m_landAssetMap.ContainsKey(txn.TransactionID);
            if (!foundArgs) {
                returnMsg = "Could not locate land asset for transaction.";
                return false;
            }
            Object[] landBuyAsset = m_landAssetMap[txn.TransactionID];
            UUID regionID = (UUID)landBuyAsset[0];
            EventManager.LandBuyArgs e = (EventManager.LandBuyArgs)landBuyAsset[1];
            // Set land buy args that need setting
            // TODO: should we be creating a new LandBuyArgs and copying the data instead in case anything else subscribes to the LandBuy events and mucked with these?
            e.economyValidated = true;
            e.amountDebited = txn.Amount;
            e.landValidated = false;
            
            //// retrieve client
            IClientAPI sender = LocateClientObject(txn.PayerID);
            if (sender == null) {
                // TODO: Does it matter if we can't locate the client?  Does this break if sender is null?
                returnMsg = "Could not locate buyer.";
                return false;
            }
            //// retrieve scene
            Scene s = GetSceneByUUID(regionID);
            if (s == null) {
                returnMsg = "Could not locate scene.";
                return false;
            }
            
            //// Trigger validate
            s.EventManager.TriggerValidateLandBuy(sender, e);
            // Check land validation
            if (!e.landValidated) {
                returnMsg = "Land validation failed.";
                return false;
            }
            if (e.parcelOwnerID != txn.PayeeID) {
                returnMsg = "Parcel owner changed.";
                return false;
            }
            
            //// Trigger process
            s.EventManager.TriggerLandBuy(sender, e);
            // Verify that land transferred successfully - sad that we have to check this.
            ILandObject parcel = s.LandChannel.GetLandObject(e.parcelLocalID);
            UUID newOwnerID = parcel.LandData.OwnerID;
            if (newOwnerID != txn.PayerID) {
                // This should only happen if due to race condition.  Unclear if possible or result.
                returnMsg = "Land transfer failed.  Owner is not buyer.";
                return false;
            }
            returnMsg = "Transfer of land succeeded.";
            return true;
        }
        
        #endregion // GMM Asset Enact Helpers

        #region local Fund Management

        /// <summary>
        /// Retrieves the gloebit balance of the gloebit account linked to the OpenSim agent defined by the agentID.
        /// If there is no token, or an invalid token on file, and forceAuthOnInvalidToken is true, we request authorization from the user.
        /// </summary>
        /// <param name="agentID">OpenSim AgentID for the user whose balance is being requested</param>
        /// <param name="client">IClientAPI for agent.  Need to pass this in because locating returns null when called from OnNewClient.
        ///                         moved to OnCompleteMovementToRegion, but may still be more efficient until removed from auth.</param>
        /// <param name ="forceAuthOnInvalidToken">Bool indicating whether we should request auth on failures from lack of auth</param>
        /// <returns>Gloebit balance for the gloebit account linked to this OpenSim agent or 0.0.</returns>
        private double GetAgentBalance(UUID agentID, IClientAPI client, bool forceAuthOnInvalidToken)
        {
            m_log.DebugFormat("[GLOEBITMONEYMODULE] GetAgentBalance AgentID:{0}", agentID);
            double returnfunds = 0.0;
            bool needsAuth = false;
            
            // Get User for agent
            GloebitAPI.User user = GloebitAPI.User.Get(m_api, agentID);
            if(!user.IsAuthed()) {
                // If no auth token on file, request authorization.
                needsAuth = true;
            } else {
                returnfunds = m_api.GetBalance(user, out needsAuth);
                // if GetBalance fails due to invalidToken, needsAuth is set to true
                
                // Fix for having a few old tokens out in the wild without an app_user_id stored as the user.GloebitID
                // TODO: Remove this  once it's been released for awhile, as this fix should only be necessary for a short time.
                if (String.IsNullOrEmpty(user.GloebitID) || user.GloebitID == UUID.Zero.ToString()) {
                    m_log.InfoFormat("[GLOEBITMONEYMODULE] GetAgentBalance AgentID:{0} INVALIDATING TOKEN FROM GMM", agentID);
                    user.InvalidateToken();
                    needsAuth = true;
                }
            }
            
            if (needsAuth && forceAuthOnInvalidToken) {
                m_api.Authorize(user, client.Name, BaseURI);
            }
            
            return returnfunds;
        }
        
        /// <summary>
        /// Requests the user's balance from Gloebit if authorized.
        /// If not authorized, sends an auth request to the user.
        /// Sends the balance to the client (or sends 0 if failure due to lack of auth).
        /// If the balance is less than the purchaseIndicator, sends the purchase url to the user.
        /// NOTE: Does not provide any transaction details in the SendMoneyBalance call.  Do not use this helper for updates within a transaction.
        /// </summary>
        /// <param name="agentID">OpenSim AgentID for the user whose balance is being updated.</param>
        /// <param name="client">IClientAPI for agent.  Need to pass this in because locating returns null when called from OnNewClient</param>
        /// <param name ="purchaseIndicator">int indicating whether we should deliver the purchase url to the user when we have an authorized user.
        ///                 -1: always deliver
        ///                 0: never deliver
        ///                 positive number: deliver if user's balance is below this indicator
        /// </param>
        /// <returns>Gloebit balance for the gloebit account linked to this OpenSim agent or 0.0.</returns>
        private double UpdateBalance(UUID agentID, IClientAPI client, int purchaseIndicator)
        {
            int returnfunds = 0;
            double realBal = 0.0;
            
            try
            {
                // Request balance from Gloebit.  Request Auth from Gloebit if necessary
                realBal = GetAgentBalance(agentID, client, true);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] UpdateBalance Failure, Exception:{0}",e.Message);
                client.SendAlertMessage(e.Message + " ");
            }
            
            // Get balance rounded down (may not be int for merchants)
            returnfunds = (int)realBal;
            // NOTE: if updating as part of a transaction, call SendMoneyBalance directly with transaction information instead of using UpdateBalance
            client.SendMoneyBalance(UUID.Zero, true, new byte[0], returnfunds, 0, UUID.Zero, false, UUID.Zero, false, 0, String.Empty);
            
            if (purchaseIndicator == -1 || purchaseIndicator > returnfunds) {
                // Send purchase URL to make it easy to find out how to buy more gloebits.
                GloebitAPI.User u = GloebitAPI.User.Get(m_api, client.AgentId);
                if (u.IsAuthed()) {
                    // Deliver Purchase URI in case the helper-uri is not working
                    Uri url = m_api.BuildPurchaseURI(BaseURI, u);
                    SendUrlToClient(client, "Need more gloebits?", "Buy gloebits you can spend on this grid:", url);
                }
            }
            return realBal;
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
        
        /// <summary>
        /// Utility function to get a Scene by Region Name in a module
        /// Recommend retrieving by UUID if possible.  Not idea, but
        /// necessary because MoveMoney doesn't supply the Scene or RegionID
        /// </summary>
        /// <param name="RegionName"></param>
        /// <returns></returns>
        private Scene GetSceneByName(string regionName)
        {
            lock (m_scenel)
            {
                foreach (Scene rs in m_scenel.Values)
                {
                    if (rs.RegionInfo.RegionName == regionName)
                    {
                        return rs;
                    }
                }
            }
            return null;
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
            string avatarname = String.Empty;
            Scene scene = GetAnyScene();

            // Try using IUserManagement module which works for both local users and hypergrid visitors
            IUserManagement umModule = scene.RequestModuleInterface<IUserManagement>();
            if (umModule != null) {
                avatarname = umModule.GetUserName(agentID);
            }

            // If above didn't work, try old method which doesn't work for hypergrid visitors
            if (String.IsNullOrEmpty(avatarname)) {
                UserAccount account = scene.UserAccountService.GetUserAccount(scene.RegionInfo.ScopeID, agentID);
                if (account != null)
                {
                    avatarname = account.FirstName + " " + account.LastName;
                } else {
                    // both methods failed.  Log error.
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE]: Could not resolve name for user {0}", agentID);
                }
            }

            return avatarname;
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

        #endregion

        #region event Handlers

        private void OnNewPresence(ScenePresence presence) {
            m_log.DebugFormat("[GLOEBITMONEYMODULE] OnNewPresence viewer:{0}", presence.Viewer);
        }

        /// <summary>
        /// New Client Event Handler
        /// </summary>
        /// <param name="client"></param>
        private void OnNewClient(IClientAPI client)
        {
            m_log.DebugFormat("[GLOEBITMONEYMODULE] OnNewClient for {0}", client.AgentId);

            // Subscribe to money related messages
            client.OnEconomyDataRequest += EconomyDataRequestHandler;           // Handles sending OpenSim economy data
            client.OnMoneyBalanceRequest += MoneyBalanceRequest;                // Handles updating balance and sending to client
            client.OnRequestPayPrice += requestPayPrice;                        // Handles OpenSim price request
            client.OnObjectBuy += ObjectBuy;                                    // Handles 5000 (Object Buy) event
            client.OnScriptAnswer += handleScriptAnswer;                        // Handle response of granting auto-debit permissions

            if (!m_newLandPassFlow) {
                client.OnParcelBuyPass += ParcelBuyPass;                        // Handle purchase of timed access to parcel
            }

            // Subscribe to other events
            client.OnLogout += ClientLoggedOut;                                 // Handles cleanup
            client.OnCompleteMovementToRegion += OnCompleteMovementToRegion;    // Handles balance update and new session messaging
        }

        /// <summary>
        /// Event triggered when agent enters new region.
        /// Handles updating of information necessary when a user has arrived at a new region, sim, or grid.
        /// Requests balance from Gloebit if authed and delivers to viewer.
        /// If this is a new session, if not authed, requests auth.  If authed, sends purchase url.
        /// </summary>
        private void OnCompleteMovementToRegion(IClientAPI client, bool blah) {
            // TODO: may now be albe to remove client from these funcs (since we moved this out of OnNewClient, but this still might be simpler.
            m_log.DebugFormat("[GLOEBITMONEYMODULE] OnCompleteMovementToRegion for {0} with bool {1}", client.AgentId, blah);
            m_log.DebugFormat("[GLOEBITMONEYMODULE] OnCompleteMovementToRegion SessionId:{0} SecureSessionId:{1}", client.SessionId, client.SecureSessionId);

            GloebitAPI.User user = GloebitAPI.User.Get(m_api, client.AgentId);
            // If authed, update balance immediately
            if (user.IsAuthed()) {
                // Don't send Buy Gloebits messaging so that we don't spam
                UpdateBalance(client.AgentId, client, 0);
            }
            if (user.IsNewSession(client.SessionId)) {
                // Send welcome messaging and buy gloebits messaging or auth messaging
                SendNewSessionMessaging(client, user);
            }
        }

        #region ParcelBuyPass pre-0.9 Flow
        // This code is only used if m_newLandPassFlow is false.
        // When true, see MoveMoney() for TransactionType::USER_BUYS_LANDPASS

        private void ParcelBuyPass(IClientAPI client, UUID agentID, int ParcelLocalID) {
            // This function is only registered if we are in the old LandPassFlow.  See m_newLandPassFlow

            m_log.DebugFormat("[GLOEBITMONEYMODULE] ParcelBuyPass event {0} {1}", agentID, ParcelLocalID);

            if (client == null) {
                m_log.Warn("[GLOEBITMONEYMODULE] ParcelBuyPass event with null client.  Returning.");
            }
            Scene s = (Scene)client.Scene;
            ILandObject parcel = s.LandChannel.GetLandObject(ParcelLocalID);

            // Some basic checks
            if (parcel == null) {
                m_log.Warn("[GLOEBITMONEYMODULE] ParcelBuyPass event with null parcel.  Returning.");
                alertUsersTransactionPreparationFailure(TransactionType.USER_BUYS_LANDPASS, TransactionPrecheckFailure.OBJECT_NOT_FOUND, client);
                return;
            }
            // Make sure parcel is set to sell passes
            if ((parcel.LandData.Flags & (uint)ParcelFlags.UsePassList) == 0) {
                m_log.WarnFormat("[GLOEBITMONEYMODULE] AgentID:{0} attempted to buy a pass on parcel:{1} which is not set to sell passes", agentID, parcel.LandData.GlobalID);
                alertUsersTransactionPreparationFailure(TransactionType.USER_BUYS_LANDPASS, TransactionPrecheckFailure.SALE_TYPE_INVALID, client);
                // TODO: Message user
                return;
            }
            // Holding off on this check as the parcel owner can functionally be added to the access list.  Maybe there is a reason an owner would want to do this
            // If owner, don't charge
            //if ((parcel.LandData.OwnerID == agentID)) {
            // TODO: Message user
            //    return;
            //}
            // We can't handle group transacitons, so fail that
            if(parcel.LandData.IsGroupOwned)
            {
                m_log.WarnFormat("[GLOEBITMONEYMODULE] AgentID:{0} attempted to buy a pass on parcel:{1} which is group owned.  Group Transactions are not defined.", agentID, parcel.LandData.GlobalID);
                alertUsersTransactionPreparationFailure(TransactionType.USER_BUYS_LANDPASS, TransactionPrecheckFailure.GROUP_OWNED, client);
                return;
            }

            int price = parcel.LandData.PassPrice;
            float hours = parcel.LandData.PassHours;
            string parcelName = parcel.LandData.Name;

            // If 0G$ txn, don't build and submit txn
            if (price == 0) {
                // Nothing to submit to Gloebit.  Just deliver the object
                string returnMsg = "";
                bool timeAdded = addPassTimeToParcelAccessList(s, parcel, agentID, out returnMsg);
                // Inform the user of success or failure.
                if (!timeAdded) {
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] ParcelBuyPass delivery of free land pass failed; reason:{0}.", returnMsg);
                    sendMessageToClient(client, String.Format("Delivery of free land pass failed\nParcel Name: {0}\nReason: {1}", parcelName, returnMsg), agentID);
                } else {
                    m_log.DebugFormat("[GLOEBITMONEYMODULE] ParcelBuyPass delivery of free land pass succeeded.");
                    sendMessageToClient(client, String.Format("Delivery of free land pass succeeded\nParcel Name: {0}", parcelName), agentID);
                }
                return;
            }

            // Build Transaction
            string agentName = resolveAgentName(agentID);
            string regionname = s.RegionInfo.RegionName;
            UUID regionID = s.RegionInfo.RegionID;

            string description = String.Format("{0} hour LandPass purchased for parcel {1} on {2}, {3}", hours, parcelName, regionname, m_gridnick);

            OSDMap descMap = buildBaseTransactionDescMap(regionname, regionID.ToString(), "LandPassBuy", parcel.LandData);

            GloebitAPI.Transaction txn = buildTransaction(transactionID: UUID.Zero, transactionType: TransactionType.USER_BUYS_LANDPASS,
                payerID: agentID, payeeID: parcel.LandData.OwnerID, amount: price, subscriptionID: UUID.Zero,
                partID: parcel.LandData.GlobalID, partName: parcel.LandData.Name, partDescription: parcel.LandData.Description,
                categoryID: regionID, localID: (uint)parcel.LandData.LocalID, saleType: 0);
            // NOTE: using category & localID to retrieve parcel on callback incase GlobalID doesn't work.
            // Should consider storing all LandData in assetMap to ensure PassHours hasn't changed.

            if (txn == null) {
                // build failed, likely due to a reused transactionID.  Shouldn't happen.
                alertUsersTransactionPreparationFailure(TransactionType.USER_BUYS_LANDPASS, TransactionPrecheckFailure.EXISTING_TRANSACTION_ID, client);
                return;
            }

            bool transaction_result = SubmitTransaction(txn, description, descMap, true);

            m_log.InfoFormat("[GLOEBITMONEYMODULE] ParcelBuyPass Transaction queued {0}", txn.TransactionID.ToString());
        }

        // Called for flow pre-0.9.1 where we deliver asset.  0.9.1 and after, flow does delivery, so this is not called
        private bool deliverLandPass(GloebitAPI.Transaction txn, out string returnMsg) {
            m_log.DebugFormat("[GLOEBITMONEYMODULE] deliverLandPass to:{0}", txn.PayerID);
            // Get Parcel back
            uint localID;
            if (!txn.TryGetLocalID(out localID)) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE].deliverLandPass FAILED to deliver asset - could not retrieve parcel LocalID");
                returnMsg = "Failed to deliver land pass.  Could not retrieve parcel LocalID.";
                return false;
            }
            int parcelLocalID = (int)localID;
            UUID agentID = txn.PayerID;
            UUID regionID = txn.CategoryID;
            Scene s = GetSceneByUUID(regionID);
            if (s == null) {
                // Should probably never happen
                m_log.WarnFormat("[GLOEBITMONEYMODULE].deliverLandPass FAILED because we couldn't retrieve the scene with the parcel.");
                returnMsg = "Could not locate scene with parcel selling land pass.";
                return false;
            }
            ILandObject parcel = s.LandChannel.GetLandObject(parcelLocalID);
            if (parcel == null) {
                // Parcel was deleted perhaps
                m_log.WarnFormat("[GLOEBITMONEYMODULE].deliverLandPass FAILED because we couldn't retrieve the parcel selling the land pass.");
                returnMsg = "Could not locate parcel selling land pass.";
                return false;
            }

            // Make sure nothing vital about the parcel changed
            // Make sure parcel is still set to sell passes
            if ((parcel.LandData.Flags & (uint)ParcelFlags.UsePassList) == 0) {
                m_log.WarnFormat("[GLOEBITMONEYMODULE] AgentID:{0} attempted to buy a pass on parcel:{1} which turned off pass sales before transaciton completed.", agentID, parcel.LandData.GlobalID);
                returnMsg = "Parcel is no longer selling passes.";
                return false;
            }
            // Make sure owner hasn't changed
            if ((parcel.LandData.OwnerID != txn.PayeeID)) {
                m_log.WarnFormat("[GLOEBITMONEYMODULE] AgentID:{0} attempted to buy a pass on parcel:{1} which changed ownership before transaction completed.", agentID, parcel.LandData.GlobalID);
                returnMsg = "Parcel ownership has changed.  Please retry purchase.";
                return false;
            }
            // Make sure price hasn't changed
            if(parcel.LandData.PassPrice != txn.Amount)
            {
                m_log.WarnFormat("[GLOEBITMONEYMODULE] AgentID:{0} attempted to buy a pass on parcel:{1} which changed price before transaction completed.", agentID, parcel.LandData.GlobalID);
                returnMsg = "Price of land pass has changed.  Please retry purchase.";
                return false;
            }
            // TODO: Should really double check PassHours, but we have no float in txn and haven't crated an asset map just for this.
            // TODO: Could put this in salesType since OpenSim pre 0.9.1 had a bug making this an int.

            // Do the real work
            bool timeAdded = addPassTimeToParcelAccessList(s, parcel, agentID, out returnMsg);
            if (!timeAdded) {
                m_log.WarnFormat("[GLOEBITMONEYMODULE] AgentID:{0} attempted to buy a pass on parcel:{1} but adding access time failed with reason:{2}.", agentID, parcel.LandData.GlobalID, returnMsg);
                //returnMsg is set from out parameter of addPassTimeToParcelAccessList
                return false;
            }
            // We're Done.  Success.
            returnMsg = "land pass delivery succeeded";
            return true;
        }

        private bool addPassTimeToParcelAccessList(Scene s, ILandObject parcel, UUID agentID, out string returnMsg) {
            m_log.DebugFormat("[GLOEBITMONEYMODULE] addPassToParcelAccessList agent:{0} for parcel:{1}", agentID, parcel.LandData.GlobalID);

            float hours = parcel.LandData.PassHours;
            int parcelLocalID = parcel.LandData.LocalID;
            string parcelName = parcel.LandData.Name;

            // COPIED from llAddToLandPassList and then modified/improved
            int expires = 0;
            int hoursInSeconds = (int)(3600.0 * hours);
            int nowInSeconds = Util.UnixTimeSinceEpoch();
            if (hours != 0) {
                expires = Util.UnixTimeSinceEpoch() + (int)(3600.0 * hours);
            }
            int idx = parcel.LandData.ParcelAccessList.FindIndex(
                delegate(LandAccessEntry e)
                {
                    if (e.AgentID == agentID && e.Flags == AccessList.Access) {
                        return true;
                    }
                    return false;
                });
            // If there is already a parcel access entry for agent, we may have to change the expiration
            if (idx != -1) {
                int prevExp = parcel.LandData.ParcelAccessList[idx].Expires;
                if (prevExp == 0) {
                    // This user has permanent access without a time limit.  Let them know and don't charge them
                    m_log.WarnFormat("[GLOEBITMONEYMODULE].deliverLandPass FAILED because agent already has permanent access.");
                    returnMsg = "Payer already has a land pass with no expiration for this parcel.";
                    return false;
                } else if (hours == 0) {
                    // User is getting perm access.  Doesn't matter what their current expiration is.  Set to 0
                    expires = 0;
                } else if (nowInSeconds < prevExp) {
                    // User is buying time and has some left on last pass.  Add together
                    // TODO: message user that they had Expires-now and are adding hours for new exp
                    expires = prevExp + hoursInSeconds;
                }
            }

            // Remove existing entry
            if (idx != -1) {
                // TODO: is there a reason we remove this rather than edit it?
                // TODO: Do we need to lock this?  How do these work?  Does TriggerLandObjectUpdated fix this for us?
                parcel.LandData.ParcelAccessList.RemoveAt(idx);
            }

            // Add agent to parcel access list with expiration
            LandAccessEntry entry = new LandAccessEntry();
            entry.AgentID = agentID;
            entry.Flags = AccessList.Access;
            entry.Expires = expires;
            parcel.LandData.ParcelAccessList.Add(entry);
            //s.EventManager.TriggerLandObjectUpdated((uint)ParcelLocalID, parcel);
            s.EventManager.TriggerLandObjectUpdated((uint)parcelLocalID, parcel);

            returnMsg = "Time successfully added to parcel access list entry for agent";
            return true;
        }

        #endregion //ParcelBuyPass pre-0.9 Flow

        /// <summary>
        /// Event triggered when a client responds yes to a script question (for permissions).
        /// </summary>
        /// <param name="client">Client which responded.</param>
        /// <param name="objectID">SceneObjectPart UUID of the item the client is granting permissions on</param>
        /// <param name="itemID">UUID of the TaskInventoryItem associated with this SceneObjectPart which handles permissions</param>
        /// <param name="answer">Bitmap of the permissions which are being granted</param>
        private void handleScriptAnswer(IClientAPI client, UUID objectID, UUID itemID, int answer) {
            // m_log.InfoFormat("[GLOEBITMONEYMODULE] handleScriptAnswer for client:{0} with objectID:{1}, itemID:{2}, answer:{3}", client.AgentId, objectID, itemID, answer);

            if ((answer & ScriptBaseClass.PERMISSION_DEBIT) == 0)
            {
                // This is not PERMISSION_DEBIT
                // m_log.InfoFormat("[GLOEBITMONEYMODULE] handleScriptAnswer This is not a debit request");
                return;
            }
            // User granted permission debit.  Let's create a sub and sub-auth and provide link to user.
            m_log.DebugFormat("[GLOEBITMONEYMODULE] handleScriptAnswer for a grant of debit permissions");

            ////// Check if we have an auth for this objectID.  If not, request it. //////

            // Check subscription table.  If not exists, send create call to Gloebit.
            m_log.DebugFormat("[GLOEBITMONEYMODULE] handleScriptAnswer - looking for local subscription");
            GloebitAPI.Subscription sub = GloebitAPI.Subscription.Get(objectID, m_key, m_apiUrl);
            if (sub == null) {
                // Don't create unless the object has a name and description
                // Make sure Name and Description are not null to avoid pgsql issue with storing null values
                // Make sure neither are empty as they are required by Gloebit to create a subscription
                SceneObjectPart part = findPrim(objectID);
                if (part == null) {
                    return;
                }
                if (String.IsNullOrEmpty(part.Name) || String.IsNullOrEmpty(part.Description)) {
                    m_log.InfoFormat("[GLOEBITMONEYMODULE] handleScriptAnswer - Can not create local subscription because part name or description is blank - Name:{0} Description:{1}", part.Name, part.Description);
                    // Send message to the owner to let them know they must edit the object and add a name and description
                    String imMsg = String.Format("Object with auto-debit script is missing a name or description.  Name and description are required by Gloebit in order to create a subscription for this auto-debit object.  Please enter a name and description in the object.  Current values are Name:[{0}] and Description:[{1}].", part.Name, part.Description);
                    sendMessageToClient(client, imMsg, client.AgentId);
                    return;
                }
                m_log.DebugFormat("[GLOEBITMONEYMODULE] handleScriptAnswer - creating local subscription for {0}", part.Name);
                // Create local sub
                sub = GloebitAPI.Subscription.Init(objectID, m_key, m_apiUrl, part.Name, part.Description);
            }
            if (sub.SubscriptionID == UUID.Zero) {
                m_log.DebugFormat("[GLOEBITMONEYMODULE] handleScriptAnswer - SID is ZERO -- calling GloebitAPI Create Subscription");

                // Add this user to map of waiting for sub to creat auth.
                lock(m_authWaitingForSubMap) {
                    m_authWaitingForSubMap[objectID] = client;
                }
                // call api to have Gloebit create
                m_api.CreateSubscription(sub, BaseURI);
                return;     // Async creating sub.  when it returns, we'll continue flow in SubscriptionCreationCompleted
            }
            // We have a Subscription.  Call create on an auth.
            GloebitAPI.User user = GloebitAPI.User.Get(m_api, client.AgentId);
            string agentName = resolveAgentName(client.AgentId);
            m_api.CreateSubscriptionAuthorization(sub, user, agentName, BaseURI, client);
            return;     // Async creating auth.  When returns, will send link to user.
        }

        private void OnClientLogin(IClientAPI client)
        {
            m_log.DebugFormat("[GLOEBITMONEYMODULE] OnClientLogin for {0}", client.AgentId);

            // Bit of a hack
            // OnCompleteMovementToRegion requests balance and asks for auth if not authed
            // -- This is required to cover teleports and crossing into new regions from non GMM region.
            // But, If this was due to a login, the viewer also requests the balance which triggers the same auth or purchase messaging.
            // -- Unfortunately, the event at login from the viewer is the same as when a user manually clicks on their balance.
            // Two auths look bad.
            // So, we tell our balance request to ignore the one right after login from the viewer.
            // We set a timestamp in case any viewers have removed this request, so that this ignore flag expires within a few seconds.
            LoginBalanceRequest lbr = LoginBalanceRequest.Get(client.AgentId);
            lbr.IgnoreNextBalanceRequest = true;
        }

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
            m_log.DebugFormat("[GLOEBITMONEYMODULE] ClientClosed {0}", AgentID);
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
            m_log.DebugFormat("[GLOEBITMONEYMODULE] EconomyDataRequestHandler {0}", user.AgentId);
            Scene s = (Scene)user.Scene;

            user.SendEconomyData(EnergyEfficiency, s.RegionInfo.ObjectCapacity, ObjectCount, PriceEnergyUnit, PriceGroupCreate,
                                 PriceObjectClaim, PriceObjectRent, PriceObjectScaleFactor, PriceParcelClaim, PriceParcelClaimFactor,
                                 PriceParcelRent, PricePublicObjectDecay, PricePublicObjectDelete, PriceRentLight, PriceUpload,
                                 TeleportMinPrice, TeleportPriceExponent);
        }

        /// <summary>
        /// Requests the agent's balance from Gloebit and sends it to the client
        /// NOTE:
        /// --- This is triggered by the OnMoneyBalanceRequest event
        /// ------ This appears to get called at login and when a user clicks on his/her balance.  The TransactionID is zero in both cases.
        /// ------ This may get called in other situations, but buying an object does not seem to trigger it.
        /// ------ It appears that The system which calls ApplyUploadCharge calls immediately after (still with TransactionID of Zero).
        /// </summary>
        /// <param name="client"></param>
        /// <param name="agentID"></param>
        /// <param name="SessionID"></param>
        /// <param name="TransactionID"></param>
        private void MoneyBalanceRequest(IClientAPI client, UUID agentID, UUID SessionID, UUID TransactionID)
        {
            m_log.DebugFormat("[GLOEBITMONEYMODULE] SendMoneyBalance request from {0} about {1} for transaction {2}", client.AgentId, agentID, TransactionID);

            if (client.AgentId == agentID && client.SessionId == SessionID)
            {
                // HACK to ignore request when this is just after we delivered the balance at login
                LoginBalanceRequest lbr = LoginBalanceRequest.Get(client.AgentId);
                if (lbr.IgnoreNextBalanceRequest) {
                    lbr.IgnoreNextBalanceRequest = false;
                    return;
                }

                // Request balance from Gloebit.  Request Auth if not authed.  If Authed, always deliver Gloebit purchase url.
                // NOTE: we are not passing the TransactionID to SendMoneyBalance as it appears ot always be UUID.Zero.
                UpdateBalance(agentID, client, -1);
            }
            else
            {
                m_log.WarnFormat("[GLOEBITMONEYMODULE] SendMoneyBalance - Unable to send money balance");
                client.SendAlertMessage("Unable to send your money balance to you!");
            }
        }

        /*********************************/
        /*** Land Purchasing Functions ***/
        /*********************************/
        
        // NOTE:
        // This system first calls the preflightBuyLandPrep XMLRPC function to run some checks and produce some info for the buyer.  If this is not implemented, land purchasing will not proceed.
        // When a user click's buy, this sends an event to the server which triggers the IClientAPI's HandleParcelBuyRequest function.
        // --- validates the agentID and sessionID
        // --- sets agentID, groupID, final, groupOwned, removeContribution, parcelLocalID, parcelArea, and parcelPrice to the packet data and authenticated to false.
        // ------ authenticated should probably be true since this is what the IClientAPI does, but it is set to false and ignored.
        // --- Calls all registered OnParcelBuy events, one (and maybe the only) of which is the Scene's ProcessParcelBuy function.
        // Scene's ProcessParcelBuy func in Scene.PacketHandlers.cs
        // This function creates the LandBuyArgs and then calls two EventManager functions in succession:
        // --- TriggerValidateLandBuy: Calls all registered OnValidateLandBuy functions.  These are expected to set some variables, run some checks, and set the landValidated and economyValidated bools.
        // --- TriggerLandBuy: Calls all registered OnLandBuy functions which check the land/economyValidated bools.  If both are true, they proceed and process the land purchase.
        // This system is problematic because the order of validations and process landBuys is unknown, and they lack a middle step to place holds/enact.  Because of this, we need to do a complex integration here.
        // --- In validate, set economyValidated to false to ensure that the LandManager won't process the LandBuy on the first run.
        // --- In process, if landValidated = true, create and send a u2u transaction for the purchase to Gloebit.
        // --- In the asset enact response for the Gloebit Transaction, callTriggerValidateLandBuy.  This time, the GMM can set economyValidated to true.
        // ------ If landValidated is false, return false to enact to cancel transaction.
        // ------ If landValidated is true, call TriggerLandBuy.  GMM shouldn't have to do anything during ProcessLandBuy
        // --------- Ideally, we can verify that the land transferred.  If not, return false to cancel txn.  If true, return true to signal enacted so that txn will be consumed.
        
        /// <summary>
        /// Event triggered when a client chooses to purchase land.
        /// Called to validate that the monetary portion of a land sale is possible before attempting to process that land sale.
        /// Should set LandBuyArgs.economyValidated to true if/when land sale should proceed.
        /// After all validation functions are called, all process functions are called.
        /// see also ProcessLandBuy, ProcessAssetEnactHold, and transferLand
        /// </summary>
        /// <param name="osender">Object Scene which sent the request.</param>
        /// <param name="LandBuyArgs">EventManager.LandBuyArgs passed through the event chain
        /// --- agentId: UUID of buyer
        /// --- groupId: UUID of group if being purchased for a group (see LandObject.cs UpdateLandSold)
        /// --- parcelOwnerID: UUID of seller - Set by land validator, so cannot be relied upon in validation.
        /// ------ ***NOTE*** if land is group owned (see LandObject.cs DeedToGroup & UpdateLandSold), this is a GroupID.
        /// ------ ********** If bought for group, may still be buyers agentID.
        /// ------ ********** We don't know how to handle sales to or by a group yet.
        /// --- final: bool
        /// --- groupOwned: bool - whether this is being purchased for a group (see LandObject.cs UpdateLandSold)
        /// --- removeContribution: bool - if true, removes tier contribution if purchase is successful
        /// --- parcelLocalID: int ID of parcel in region
        /// --- parcelArea: int meters square size of parcel
        /// --- parcelPrice: int price buyer will pay
        /// --- authenticated: bool - set to false by IClientAPI and ignored.
        /// --- landValidated: bool set by the LandMangementModule during validation
        /// --- economyValidated: bool this validate function should set to true or false
        /// --- transactionID: int - Not used.  Commented out.  Was intended to store auction ID if land was purchased at auction. (see LandObject.cs UpdateLandSold)
        /// --- amountDebited: int - should be set by GMM
        /// </param>
        private void ValidateLandBuy(Object osender, EventManager.LandBuyArgs e)
        {
            // m_log.InfoFormat("[GLOEBITMONEYMODULE] ValidateLandBuy osender: {0}\nLandBuyArgs: \n   agentId:{1}\n   groupId:{2}\n   parcelOwnerID:{3}\n   final:{4}\n   groupOwned:{5}\n   removeContribution:{6}\n   parcelLocalID:{7}\n   parcelArea:{8}\n   parcelPrice:{9}\n   authenticated:{10}\n   landValidated:{11}\n   economyValidated:{12}\n   transactionID:{13}\n   amountDebited:{14}", osender, e.agentId, e.groupId, e.parcelOwnerID, e.final, e.groupOwned, e.removeContribution, e.parcelLocalID, e.parcelArea, e.parcelPrice, e.authenticated, e.landValidated, e.economyValidated, e.transactionID, e.amountDebited);
            
            if (e.economyValidated == false) {  /* Don't reValidate if something has said it's ready to go. */
                if (e.parcelPrice == 0) {
                    // No monetary component, so we can just approve this.
                    e.economyValidated = true;
                    // Should be redundant, but we'll set them anyway.
                    e.amountDebited = 0;
                    e.transactionID = 0;
                } else {
                    // We have a new request that requires a monetary transaction.
                    // Do nothing for now.
                    //// consider: we could create the asset here.
                }
            }
        }

        /// <summary>
        /// Event triggered when a client chooses to purchase land.
        /// Called after all validation functions have been called.
        /// Called to process the monetary portion of a land sale.
        /// Should only proceed if LandBuyArgs.economyValidated and LandBuyArgs.landValidated are both true.
        /// Should set LandBuyArgs.amountDebited
        /// Also see ValidateLandBuy, ProcessAssetEnactHold and transferLand
        /// </summary>
        /// <param name="osender">Object Scene which sent the request.</param>
        /// <param name="LandBuyArgs">EventManager.LandBuyArgs passed through the event chain
        /// --- agentId: UUID of buyer
        /// --- groupId: UUID of group if being purchased for a group (see LandObject.cs UpdateLandSold)
        /// --- parcelOwnerID: UUID of seller - Set by land validator, so cannot be relied upon in validation.
        /// ------ ***NOTE*** if land is group owned (see LandObject.cs DeedToGroup & UpdateLandSold), this is a GroupID.
        /// ------ ********** If bought for group, may still be buyers agentID.
        /// ------ ********** We don't know how to handle sales to or by a group yet.
        /// --- final: bool
        /// --- groupOwned: bool - whether this is being purchased for a group (see LandObject.cs UpdateLandSold)
        /// --- removeContribution: bool - if true, removes tier contribution if purchase is successful
        /// --- parcelLocalID: int ID of parcel in region
        /// --- parcelArea: int meters square size of parcel
        /// --- parcelPrice: int price buyer will pay
        /// --- authenticated: bool - set to false by IClientAPI and ignored.
        /// --- landValidated: bool set by the LandMangementModule during validation
        /// --- economyValidated: bool this validate function should set to true or false
        /// --- transactionID: int - Not used.  Commented out.  Was intended to store auction ID if land was purchased at auction. (see LandObject.cs UpdateLandSold)
        /// --- amountDebited: int - should be set by GMM
        /// </param>
        private void ProcessLandBuy(Object osender, EventManager.LandBuyArgs e)
        {
            // m_log.InfoFormat("[GLOEBITMONEYMODULE] ProcessLandBuy osender: {0}\nLandBuyArgs: \n   agentId:{1}\n   groupId:{2}\n   parcelOwnerID:{3}\n   final:{4}\n   groupOwned:{5}\n   removeContribution:{6}\n   parcelLocalID:{7}\n   parcelArea:{8}\n   parcelPrice:{9}\n   authenticated:{10}\n   landValidated:{11}\n   economyValidated:{12}\n   transactionID:{13}\n   amountDebited:{14}", osender, e.agentId, e.groupId, e.parcelOwnerID, e.final, e.groupOwned, e.removeContribution, e.parcelLocalID, e.parcelArea, e.parcelPrice, e.authenticated, e.landValidated, e.economyValidated, e.transactionID, e.amountDebited);
            
            if (e.economyValidated == false) {  /* first time through */
                if (!e.landValidated) {
                    // Something's wrong with the land, can't continue
                    // Ideally, the land system would message this error, but they don't, so we will.
                    IClientAPI payerClient = LocateClientObject(e.agentId);
                    alertUsersTransactionPreparationFailure(TransactionType.USER_BUYS_LAND, TransactionPrecheckFailure.LAND_VALIDATION_FAILED, payerClient);
                    return;
                } else {
                    // Land is good to go.  Let's submit a transaction
                    //// TODO: verify that e.parcelPrice > 0;
                    //// TODO: what if parcelOwnerID is a groupID?
                    //// TODO: what if isGroupOwned is true and GroupID is not zero?
                    //// We'll have to test this and see if/how it fails when groups are involved.
                    string agentName = resolveAgentName(e.agentId);
                    string ownerName = resolveAgentName(e.parcelOwnerID);
                    Scene s = (Scene) osender;
                    string regionname = s.RegionInfo.RegionName;
                    string regionID = s.RegionInfo.RegionID.ToString();
                    
                    string description = String.Format("{0} sq. meters of land with parcel id {1} on {2}, {3}, purchased by {4} from {5}", e.parcelArea, e.parcelLocalID, regionname, m_gridnick, agentName,  ownerName);
                    
                    OSDMap descMap = buildBaseTransactionDescMap(regionname, regionID.ToString(), "LandBuy");
                    
                    GloebitAPI.Transaction txn = buildTransaction(transactionID: UUID.Zero, transactionType: TransactionType.USER_BUYS_LAND,
                                                                  payerID: e.agentId, payeeID: e.parcelOwnerID, amount: e.parcelPrice, subscriptionID: UUID.Zero,
                                                                  partID: UUID.Zero, partName: String.Empty, partDescription: String.Empty,
                                                                  categoryID: UUID.Zero, localID: 0, saleType: 0);
                    
                    if (txn == null) {
                        // build failed, likely due to a reused transactionID.  Shouldn't happen.
                        IClientAPI payerClient = LocateClientObject(e.agentId);
                        alertUsersTransactionPreparationFailure(TransactionType.USER_BUYS_LAND, TransactionPrecheckFailure.EXISTING_TRANSACTION_ID, payerClient);
                        return;
                    }
                    
                    // Add region UUID and LandBuyArgs to dictionary accessible for callback and wait for callback
                    // Needs to happen before we submit because C# can delay wakeup for this synchronous call and
                    // the enact could be received before we know if the submission succeeded.
                    lock(m_landAssetMap) {
                        m_landAssetMap[txn.TransactionID] = new Object[2]{s.RegionInfo.originRegionID, e};
                    }
                    bool submission_result = SubmitTransaction(txn, description, descMap, true);
                    // See TransactU2UCompleted and helper messaging funcs for error messaging on failure - no action required.
                    // See ProcessAssetEnactHold for proceeding with txn on success.
                    
                    if (!submission_result) {
                        // payment failed.  message user and halt attempt to transfer land
                        //// TODO: message error
                        lock(m_landAssetMap) {
                            m_landAssetMap.Remove(txn.TransactionID);
                        }
                        return;
                    }
                }
            } else {                            /* economy is validated.  Second time through or 0G txn */
                if (e.parcelPrice == 0) {
                    // Free land.  No economic part.
                    e.amountDebited = 0;
                } else {
                    // Second time through.  Completing a transaction we launched the first time through.
                    // if e.landValidated, land has or will transfer.
                    // We can't verify here because the land process may happen after economy, so do nothing here.
                    // See processAssetEnactHold and transferLand for resolution.
                }
            }
        }

        /// <summary>
        /// This method gets called when someone pays someone else as a gift.
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
            UUID fromID = UUID.Zero;
            UUID toID = UUID.Zero;
            UUID partID = UUID.Zero;
            string partName = String.Empty;
            string partDescription = String.Empty;
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
                    break;
                case TransactionType.OBJECT_PAYS_USER:
                    // 5009 - ObjectGiveMoney
                    m_log.ErrorFormat("******* OBJECT_PAYS_USER received in OnMoneyTransfer - Unimplemented transactiontype: {0}", e.transactiontype);
                    
                    // TransactionType 5009 is handled by ObjectGiveMoney and should never trigger a call to OnMoneyTransfer
                    /*
                    partID = e.sender;
                    part = s.GetSceneObjectPart(partID);
                    partName = part.Name;
                    partDescription = part.Description;
                    fromID = part.OwnerID;
                    toID = e.receiver;
                    descMap = buildBaseTransactionDescMap(regionname, regionID, "ObjectPaysUser", part);
                    description = e.description;
                    */
                    return;
                    break;
                default:
                    m_log.ErrorFormat("UNKNOWN Unimplemented transactiontype received in OnMoneyTransfer: {0}", e.transactiontype);
                    return;
                    break;
            }
            
            /******** Set up necessary parts for gloebit transact-u2u **********/
            
            GloebitAPI.Transaction txn = buildTransaction(transactionID: UUID.Zero, transactionType: (TransactionType)e.transactiontype,
                                                          payerID: fromID, payeeID: toID, amount: e.amount, subscriptionID: UUID.Zero,
                                                          partID: partID, partName: partName, partDescription: partDescription,
                                                          categoryID: UUID.Zero, localID: 0, saleType: 0);
            
            if (txn == null) {
                // build failed, likely due to a reused transactionID.  Shouldn't happen.
                IClientAPI payerClient = LocateClientObject(fromID);
                alertUsersTransactionPreparationFailure((TransactionType)e.transactiontype, TransactionPrecheckFailure.EXISTING_TRANSACTION_ID, payerClient);
                return;
            }
            
            bool transaction_result = SubmitTransaction(txn, description, descMap, true);
            
            // TODO - do we need to send any error message to the user if things failed above?`
        }

        /// <summary>
        /// Event Handler for when the client logs out.
        /// </summary>
        /// <param name="AgentId"></param>
        private void ClientLoggedOut(IClientAPI client)
        {
            // Deregister OnChatFromClient if we have one.
            Dialog.DeregisterAgent(client);
            
            // Remove from s_LoginBalanceRequestMap
            LoginBalanceRequest.Cleanup(client.AgentId);
            
            // Remove from s_userMap
            GloebitAPI.User.Cleanup(client.AgentId);
        
            m_log.DebugFormat("[GLOEBITMONEYMODULE] ClientLoggedOut {0}", client.AgentId);
        }

        /// <summary>
        /// Event Handler for when an Avatar enters one of the parcels in the simulator.
        /// </summary>
        /// <param name="avatar"></param>
        /// <param name="localLandID"></param>
        /// <param name="regionID"></param>
        private void AvatarEnteringParcel(ScenePresence avatar, int localLandID, UUID regionID)
        {
            m_log.DebugFormat("[GLOEBITMONEYMODULE] AvatarEnteringParcel {0}", avatar.Name);
        }

        // <summary>
        // Client.OnObjectBuy event handler
        // event triggered when user clicks on buy for an object which is for sale
        // </summary>
        private void ObjectBuy(IClientAPI remoteClient, UUID agentID,
                UUID sessionID, UUID groupID, UUID categoryID,
                uint localID, byte saleType, int salePrice)
        {
            m_log.DebugFormat("[GLOEBITMONEYMODULE] ObjectBuy client:{0}, agentID: {1}", remoteClient.AgentId, agentID);
            
            if (!m_sellEnabled)
            {
                alertUsersTransactionPreparationFailure(TransactionType.USER_BUYS_OBJECT, TransactionPrecheckFailure.BUYING_DISABLED, remoteClient);
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
                alertUsersTransactionPreparationFailure(TransactionType.USER_BUYS_OBJECT, TransactionPrecheckFailure.OBJECT_NOT_FOUND, remoteClient);
                return;
            }
            
            // Validate that the client sent the price that the object is being sold for 
            if (part.SalePrice != salePrice)
            {
                alertUsersTransactionPreparationFailure(TransactionType.USER_BUYS_OBJECT, TransactionPrecheckFailure.AMOUNT_MISMATCH, remoteClient);
                return;
            }

            // Validate that is the client sent the proper sale type the object has set
            if (saleType < 1 || saleType > 3) {
                // Should not get here unless an object purchase is submitted with a bad or new (but unimplemented) saleType.
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] ObjectBuy Unrecognized saleType:{0} --- expected 1,2 or 3 for original, copy, or contents", saleType);
                alertUsersTransactionPreparationFailure(TransactionType.USER_BUYS_OBJECT, TransactionPrecheckFailure.SALE_TYPE_INVALID, remoteClient);
                return;
            }
            if (part.ObjectSaleType != saleType) {
                alertUsersTransactionPreparationFailure(TransactionType.USER_BUYS_OBJECT, TransactionPrecheckFailure.SALE_TYPE_MISMATCH, remoteClient);
                return;
            }

            // Check that the IBuySellModule is accesible before submitting the transaction to Gloebit
            IBuySellModule module = s.RequestModuleInterface<IBuySellModule>();
            if (module == null) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] ObjectBuy FAILED to access to IBuySellModule");
                alertUsersTransactionPreparationFailure(TransactionType.USER_BUYS_OBJECT, TransactionPrecheckFailure.BUY_SELL_MODULE_INACCESSIBLE, remoteClient);
                return;
            }
            
            // If 0G$ txn, don't build and submit txn
            if (salePrice == 0) {
                // Nothing to submit to Gloebit.  Just deliver the object
                bool delivered = module.BuyObject(remoteClient, categoryID, localID, saleType, salePrice);
                // Inform the user of success or failure.
                if (!delivered) {
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] ObjectBuy delivery of free object failed.");
                    // returnMsg = "IBuySellModule.BuyObject failed delivery attempt.";
                    sendMessageToClient(remoteClient, String.Format("Delivery of free object failed\nObject Name: {0}", part.Name), agentID);
                } else {
                    m_log.DebugFormat("[GLOEBITMONEYMODULE] ObjectBuy delivery of free object succeeded.");
                    // returnMsg = "object delivery succeeded";
                    sendMessageToClient(remoteClient, String.Format("Delivery of free object succeeded\nObject Name: {0}", part.Name), agentID);
                }
                return;
            }

            string agentName = resolveAgentName(agentID);
            string regionname = s.RegionInfo.RegionName;
            string regionID = s.RegionInfo.RegionID.ToString();

            // string description = String.Format("{0} bought object {1}({2}) on {3}({4})@{5}", agentName, part.Name, part.UUID, regionname, regionID, m_gridnick);
            string description = String.Format("{0} object purchased on {1}, {2}", part.Name, regionname, m_gridnick);

            OSDMap descMap = buildBaseTransactionDescMap(regionname, regionID.ToString(), "ObjectBuy", part);
            
            GloebitAPI.Transaction txn = buildTransaction(transactionID: UUID.Zero, transactionType: TransactionType.USER_BUYS_OBJECT,
                                                          payerID: agentID, payeeID: part.OwnerID, amount: salePrice, subscriptionID: UUID.Zero,
                                                          partID: part.UUID, partName: part.Name, partDescription: part.Description,
                                                          categoryID: categoryID, localID: localID, saleType: saleType);
            
            if (txn == null) {
                // build failed, likely due to a reused transactionID.  Shouldn't happen.
                alertUsersTransactionPreparationFailure(TransactionType.USER_BUYS_OBJECT, TransactionPrecheckFailure.EXISTING_TRANSACTION_ID, remoteClient);
                return;
            }
            
            bool transaction_result = SubmitTransaction(txn, description, descMap, true);
            
            m_log.InfoFormat("[GLOEBITMONEYMODULE] ObjectBuy Transaction queued {0}", txn.TransactionID.ToString());
        }

        #endregion // event handlers

        #region GMM User Messaging
        
        /******************************************/
        /********* User Messaging Section *********/
        /******************************************/
        
        //**** Notes on OpenSim client messages ***/
        // AlertMessage: top right.  fades away. also appears in nearby chat (only to intended user). character limit of about 254
        // BlueBoxMessage: top right. fades away.  slightly darker than AlertMessage in Firestorm.  Also appears in nearby chat (only to intended user). no character limit
        // AgentAlertMessage w False: top right.  has "OK" button.  Fades away but stays in messages. character limit about 253
        // AgentAlertMessage w True: center. has "OK" button.  Does not fade away.  Requires clicking ok before interacting with anything else. character limit about 250
        
        /// <summary>
        /// Sends a message with url to user.
        /// </summary>
        /// <param name="client">IClientAPI of client we are sending the URL to</param>
        /// <param name="title">string title of message we are sending with the url</param>
        /// <param name="body">string body of message we are sending with the url</param>
        /// <param name="uri">full url we are sending to the client</param>
        private static void SendUrlToClient(IClientAPI client, string title, string body, Uri uri)
        {
            // Since we are trying to make the GloebitAPI strictly C# and not specific to OpenSim, we have removed IClientAPI where possible.
            // This means that in some flows where we start with a client but make an async call to the API, we only send the AgentId and
            // later have to re-retrieve the client from the AgentId.  Sometimes, this fails, and returns null, perhaps because a user has
            // logged out during the flow.  This causes a crash here because we dereference the client.  Instead, we will log a warning
            // (in case we start missing messages for users who are logged in) and return instead of attempting to message the client.
            if (client == null) {
                m_log.WarnFormat("[GLOEBITMONEYMODULE] SendUrlToClient called with null client.  Intended message was title:{0}; body:{1}; Uri:{2}", title, body, uri);
                return;
            }
            
            // Prep and send message
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
        
        /// <summary>
        /// Sends a message to a client.  If user is logged out and OfflineMessageModule is enabled, tries to save message to deliver at next login.
        /// </summary>
        /// <param name="client">IClientAPI of user we are messaging.</param>
        /// <param name="message">String message we are sending to client.</param>
        /// <param name="agentID">UUID of client we are messaging - only used if user is offline, to attempt saving of message.</param>
        private void sendMessageToClient(IClientAPI client, string message, UUID agentID)
        {
            //payerClient.SendBlueBoxMessage(UUID.Zero, "What is this?", String.Format("BlueBoxMessage: {0}", message));
            //payerClient.SendAgentAlertMessage(String.Format("AgentAlertMessage: {0}", message), false);
            //payerClient.SendAgentAlertMessage(String.Format("AgentAlertMessage True: {0}", message), true);
            //payerClient.SendAlertMessage(String.Format("AlertMessage: {0}", message));

            if (client != null) {
                //string imMessage = String.Format("{0}\n\n{1}", "Gloebit:", message);
                string imMessage = message;
                UUID fromID = UUID.Zero;
                string fromName = String.Empty; // Left blank as this is not used for the MessageBox message type
                UUID toID = client.AgentId;
                bool isFromGroup = false;
                UUID imSessionID = toID;        // Don't know what this is used for.  Saw it hacked to agent id in friendship module
                bool isOffline = true;          // Don't know what this is for.  Should probably try both.
                bool addTimestamp = false;
                    
                // TODO: add alternate MessageFromAgent which includes an ok button and doesn't show up in chat, rather goes to notifications
                GridInstantMessage im = new GridInstantMessage(client.Scene, fromID, fromName, toID, (byte)InstantMessageDialog.MessageBox, isFromGroup, imMessage, imSessionID, isOffline, Vector3.Zero, new byte[0], addTimestamp);
                client.SendInstantMessage(im);
            } else {
                // TODO: do we want to send an email or do anything else?
                
                // Attempt to save a message for the offline user.
                if (agentID != UUID.Zero) {     // Necessary because some txnPrecheckFailures don't currently pass the agentID
                    // If an OfflineMessageModule is set up and a service is registered at the following, this might work for offline messaging.
                    // SynchronousRestObjectRequester.MakeRequest<GridInstantMessage, bool>("POST", m_RestURL+"/SaveMessage/", im, 10000)
                    Scene s = GetAnyScene();
                    IMessageTransferModule tr = s.RequestModuleInterface<IMessageTransferModule>();
                    if (tr != null) {
                        GridInstantMessage im2 = new GridInstantMessage(null, UUID.Zero, "Gloebit", agentID, (byte)InstantMessageDialog.MessageFromAgent, false, message, agentID, true, Vector3.Zero, new byte[0], true);
                        tr.SendInstantMessage(im2, delegate(bool success) {});
                    }
                }
            }
        }

        /// <summary>
        /// Deliver intro messaging for user in new session or new enviromnet.
        /// --- "Welcome to area running Gloebit in Sandbox for app MYAPP"
        /// </summary>
        private void SendNewSessionMessaging(IClientAPI client, GloebitAPI.User user) {
            // TODO: Add in AppName to messages if we have it -- may need a new endpoint.
            string msg;
            if (m_environment == GLBEnv.Sandbox) {
                msg = String.Format("Welcome {0}.  This area is using the Gloebit Money Module in Sandbox Mode for testing.  All payments and transactions are fake.  Try it out.", client.Name);
            } else if (m_environment == GLBEnv.Production) {
                msg = String.Format("Welcome {0}.  This area is using the Gloebit Money Module.  You can transact with gloebits.", client.Name);
            } else {
                msg = String.Format("Welcome {0}.  This area is using the Gloebit Money Module in a Custom Devloper Mode.", client.Name);
            }
            // Add instructions for clicking balance to see auth or purchase url
            // TODO: Should this be a separate message?
            if (user.IsAuthed()) {
                msg = String.Format("{0}\nClick on your balance in the top right to purchase more gloebits.", msg);
            } else {
                msg = String.Format("{0}\nClick on your balance in the top right to link this avatar on this app to your Gloebit account.", msg);

            }
            // Delay messaging for a cleaner experience
            int delay = 1; // Delay 1 seconds on crossing or teleport where viewer is already loaded
            if (LoginBalanceRequest.ExistsAndJustLoggedIn(client.AgentId)) {
                delay = 10; // Delay 10 seconds if viewer isn't fully loaded, shows up as offline while away
            }
            Thread welcomeMessageThread = new Thread(delegate() {
                Thread.Sleep(delay * 1000);  // Delay miliseconds
                // Deliver welcome message
                sendMessageToClient(client, msg, client.AgentId);
                // If authed, delivery url where user can purchase gloebits
                if (user.IsAuthed()) {
                    // Uri url = m_api.BuildPurchaseURI(BaseURI, user);
                    // SendUrlToClient(client, "How to purchase gloebits:", "Buy gloebits you can spend in this area:", url);
                } else {
                    // If not Authed, request auth.
                    m_api.Authorize(user, client.Name, BaseURI);
                }
            });
            welcomeMessageThread.Start();
        }
        
        /// <summary>
        /// See version with additional agentID argument.  Most status messages go to payer and we should have a valid client and in the case
        /// the payer is offline, can grab the agentID we need.  This version removes the need to supply that argument every time.
        /// In the case we are notifying the payee or someone else, the correct agentID can be supplied directly.
        /// </summary>
        private void sendTxnStatusToClient(GloebitAPI.Transaction txn, IClientAPI client, string baseStatus, bool showTxnDetails, bool showTxnID)
        {
            sendTxnStatusToClient(txn, client, baseStatus, showTxnDetails, showTxnID, txn.PayerID);
        }
        
        /// <summary>
        /// Builds a status string and sends it to the client
        /// Always includes an intro with shortened txn id and a base message.
        /// May include addtional transaction details and txn id based upon bool arguments and bool overrides.
        /// </summary>
        /// <param name="txn">GloebitAPI.Transaction this status is in regards to.</param>
        /// <param name="client">Client we are messaging.  If null, our sendMessage func will handle properly.</param>
        /// <param name="baseStatus">String Status message to deliver.</param>
        /// <param name="showTxnDetails">If true, include txn details in status (can be overriden by global overrides).</param>
        /// <param name="showTxnID">If true, include full transaction id in status (can be overriden by global overrides).</param>
        /// <param name="agentID">UUID of user we are messaging.  Only used if user is offline and client is null.</param>
        private void sendTxnStatusToClient(GloebitAPI.Transaction txn, IClientAPI client, string baseStatus, bool showTxnDetails, bool showTxnID, UUID agentID)
        {
            // Determine if we're including Details and ID based on args and overrides
            bool alwaysShowTxnDetailsOverride = false;
            bool alwaysShowTxnIDOverride = false;
            bool neverShowTxnDetailsOverride = false;
            bool neverShowTxnIDOverride = false;
            bool includeDetails = (alwaysShowTxnDetailsOverride || (showTxnDetails && !neverShowTxnDetailsOverride));
            bool includeID = (alwaysShowTxnIDOverride || (showTxnID && !neverShowTxnIDOverride));
            
            // Get shortened txn id
            //int shortenedID = (int)(txn.TransactionID.GetULong() % 10000);
            string sid = txn.TransactionID.ToString().Substring(0,4).ToUpper();
            
            // Build status string
            string status = String.Format("Gloebit Transaction [{0}]:\n{1}"/*\n"*/, sid, baseStatus);
            if (includeDetails) {
                // build txn details string
                string paymentFrom = String.Format("Payment from: {0}", txn.PayerName);
                string paymentTo = String.Format("Payment to: {0}", txn.PayeeName);
                string amountStr = String.Format("Amount: {0:n0} gloebits", txn.Amount);
                // TODO: add description back in once txn includes it.
                // string descStr = String.Format("Description: {0}", description);
                string txnDetails = String.Format("Details:\n   {0}\n   {1}\n   {2}", paymentFrom, paymentTo, amountStr/*, descStr*/);
                
                status = String.Format("{0}\n{1}", status, txnDetails);
            }
            if (includeID) {
                // build txn id string
                string idStr = String.Format("Transaction ID: {0}", txn.TransactionID);
                
                status = String.Format("{0}\n{1}", status, idStr);
            }
            
            // Send status string to client
            sendMessageToClient(client, status, agentID);
        }
        
        /**** Functions to handle messaging users upon precheck failures in GMM before txn is created ****/
        
        /// <summary>
        /// Inform users of txn precheck failure due to subscription requiring creation.
        /// Triggered first time an auto-debit transaction comes from an object.
        /// </summary>
        /// <param name="payerID">UUID of payer from transaction that triggered this alert.</param>
        /// <param name="payeeID">UUID of payee from transaction that triggered this alert.</param>
        /// <param name="amount">Int amount of gloebits from transaction that triggered this alert.</param>
        /// <param name="sub">GloebitAPI.Subscription being sent to Gloebit for creation.</param>
        private void alertUsersSubscriptionTransactionFailedForSubscriptionCreation(UUID payerID, UUID payeeID, int amount, GloebitAPI.Subscription sub)
        {
            IClientAPI payerClient = LocateClientObject(payerID);
            IClientAPI payeeClient = LocateClientObject(payeeID);
            
            string failedTxnDetails = String.Format("Failed Transaction Details:\n   Object Name: {0}\n   Object Description: {1}\n   Payment From: {2}\n   Payment To: {3}\n   Amount: {4}", sub.ObjectName, sub.Description, resolveAgentName(payerID), resolveAgentName(payeeID), amount);
            
            // TODO: Need to alert payer whether online or not as action is required.
            sendMessageToClient(payerClient, String.Format("Gloebit: Scripted object attempted payment from you, but failed because no subscription exists for this recurring, automated payment.  Creating subscription now.  Once created, the next time this script attempts to debit your account, you will be asked to authorize that subscription for future auto-debits from your account.\n\n{0}", failedTxnDetails), payerID);
            
            // TODO: is this message bad if fraudster?
            // Should alert payee if online as might be expecting feedback
            sendMessageToClient(payeeClient, String.Format("Gloebit: Scripted object attempted payment to you, but failed because no subscription exists for this recurring, automated payment.  Creating subscription now.  If you triggered this transaction with an action, you can retry in a minute.\n\n{0}", failedTxnDetails), payeeID);
        }
        
        /// <summary>
        /// Inform users of txn precheck failure due to subscription transaction where payee never authorized with Gloebit, or revoked authorization.
        /// </summary>
        /// <param name="payerID">UUID of payer from transaction that triggered this alert.</param>
        /// <param name="payeeID">UUID of payee from transaction that triggered this alert.</param>
        /// <param name="amount">Int amount of gloebits from transaction that triggered this alert.</param>
        /// <param name="sub">GloebitAPI.Subscription that triggered this alert.</param>
        private void alertUsersSubscriptionTransactionFailedForGloebitAuthorization(UUID payerID, UUID payeeID, int amount, GloebitAPI.Subscription sub)
        {
            IClientAPI payerClient = LocateClientObject(payerID);
            IClientAPI payeeClient = LocateClientObject(payeeID);
            
            string failedTxnDetails = String.Format("Failed Transaction Details:\n   Object Name: {0}\n   Object Description: {1}\n   Payment From: {2}\n   Payment To: {3}\n   Amount: {4}", sub.ObjectName, sub.Description, resolveAgentName(payerID), resolveAgentName(payeeID), amount);
            
            // TODO: Need to alert payer whether online or not as action is required.
            sendMessageToClient(payerClient, String.Format("Gloebit: Scripted object attempted payment from you, but failed because you have not authorized this application from Gloebit.  Once you authorize this application, the next time this script attempts to debit your account, you will be asked to authorize that subscription for future auto-debits from your account.\n\n{0}", failedTxnDetails), payerID);
            if (payerClient != null) {
                GloebitAPI.User user = GloebitAPI.User.Get(m_api, payerID);
                m_api.Authorize(user, payerClient.Name, BaseURI);
            }
            
            // TODO: is this message bad if fraudster?
            // Should alert payee if online as might be expecting feedback
            sendMessageToClient(payeeClient, String.Format("Gloebit: Scripted object attempted payment to you, but failed because the object owner has not yet authorized this subscription to make recurring, automated payments.  Requesting authorization now.\n\n{0}", failedTxnDetails), payeeID);
        }
        
        /// <summary>
        /// Called when application preparation of a transaction fails before submission to Gloebit is attempted.
        /// Use to inform users or log issues
        /// At a minimum, this should inform the user who triggered the transaction of failure so they have feedback.
        /// This is separated from alertUsersTransactionBegun because there may not be a transaction yet and therefore
        /// different arguments are needed.
        /// </summary>
        /// <param name="typeID">TransactionType that was being prepared.</param>
        /// <param name="failure">TransactionPrecheckFailure that occurred.</param>
        /// <param name="payerClient">IClientAPI of payer or null.</param>
        private void alertUsersTransactionPreparationFailure(TransactionType typeID, TransactionPrecheckFailure failure, IClientAPI payerClient)
        {
            // TODO: move these to a string resource at some point.
            // Set up instruction strings which are used mutliple times
            string tryAgainRelog = "Please retry your purchase.  If you continue to get this error, relog.";
            string tryAgainContactOwner = String.Format("Please try again.  If problem persists, contact {0}.", m_contactOwner);
            string tryAgainContactGloebit = String.Format("Please try again.  If problem persists, contact {0}.", m_contactGloebit);
            
            // Set up temp strings to hold failure messages based on transaction type and failure
            string txnTypeFailure = String.Empty;
            string precheckFailure = String.Empty;
            string instruction = String.Empty;
            
            // Retrieve failure strings into temp variables based on transaction type and failure
            switch (typeID) {
                case TransactionType.USER_BUYS_OBJECT:
                    // Alert payer only
                    txnTypeFailure = "Attempt to buy object failed prechecks.";
                    switch (failure) {
                        case TransactionPrecheckFailure.BUYING_DISABLED:
                            precheckFailure = "Buying is not enabled in economy settings.";
                            instruction = String.Format("If you believe this should be enabled on this region, please contact {0}.", m_contactOwner);
                            break;
                        case TransactionPrecheckFailure.OBJECT_NOT_FOUND:
                            precheckFailure = "Unable to buy now. The object was not found.";
                            instruction = tryAgainRelog;
                            break;
                        case TransactionPrecheckFailure.AMOUNT_MISMATCH:
                            precheckFailure = "Cannot buy at this price.  Price may have changed.";
                            instruction = tryAgainRelog;
                            break;
                        case TransactionPrecheckFailure.SALE_TYPE_INVALID:
                            precheckFailure = "Invalid saleType.";
                            instruction = tryAgainContactOwner;
                            break;
                        case TransactionPrecheckFailure.SALE_TYPE_MISMATCH:
                            precheckFailure = "Sale type mismatch.  Cannot buy this way.  Sale type may have changed.";
                            instruction = tryAgainRelog;
                            break;
                        case TransactionPrecheckFailure.BUY_SELL_MODULE_INACCESSIBLE:
                            precheckFailure = "Unable to access IBuySellModule necessary for transferring inventory.";
                            instruction = tryAgainContactOwner;
                            break;
                        default:
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] alertUsersTransactionPreparationFailure: Unimplemented failure TransactionPrecheckFailure [{0}] TransactionType.", failure, typeID);
                            break;
                    }
                    break;
                case TransactionType.USER_BUYS_LAND:
                    // Alert payer only as payer triggered txn
                    txnTypeFailure = "Attempt to buy land failed prechecks.";
                    precheckFailure = "Validation of parcel ownership and sale parameters failed.";
                    instruction = tryAgainRelog;
                    break;
                case TransactionType.USER_BUYS_LANDPASS:
                    // Alert Payer only
                    txnTypeFailure = "Attempt to buy land pass failed prechecks.";
                    switch (failure) {
                        case TransactionPrecheckFailure.OBJECT_NOT_FOUND:
                            precheckFailure = "Unable to buy now. The parcel was not found.";
                            instruction = tryAgainRelog;
                            break;
                        case TransactionPrecheckFailure.SALE_TYPE_INVALID:
                            precheckFailure = "Parcel is not set to sell passes.";
                            instruction = tryAgainRelog;
                            break;
                        case TransactionPrecheckFailure.GROUP_OWNED:
                            precheckFailure = "Parcel is group owned.  Transactions involving groups are undefined in OpenSim and thus have not been implemented yet by Gloebit.";
                            instruction = String.Format("If you would like Gloebit to prioritize this functionality, please contact {0}.", m_contactGloebit);
                            break;
                        default:
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] alertUsersTransactionPreparationFailure: Unimplemented failure TransactionPrecheckFailure [{0}] TransactionType.", failure, typeID);
                            break;
                    }
                    break;
                case TransactionType.MOVE_MONEY_GENERAL:
                    // MoveMoney unimplemented transaction type.
                    // Alert payer only
                    txnTypeFailure = "Unimplemented transaciton type.";
                    instruction = String.Format("Please contact {0}, tell them what you were doing that requires a payment, and ask them to implement this transaction type.", m_contactGloebit);
                    break;
                case TransactionType.OBJECT_PAYS_USER:
                    // Alert payer and payee
                    // never happens currently
                    // txnTypeFailure = "Attempt by scripted object to pay user failed prechecks.";
                case TransactionType.USER_PAYS_USER:
                    // Alert payer only
                    // never happens currently
                    // txnTypeFailure = "Attempt to pay user failed prechecks.";
                case TransactionType.USER_PAYS_OBJECT:
                    // Alert payer only
                    // never happens currently
                    // txnTypeFailure = "Attempt to pay object failed prechecks.";
                default:
                    // Alert payer and payee
                    txnTypeFailure = "Transaction attempt failed prechecks.";
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] alertUsersTransactionPreparationFailure: Unimplemented failure TransactionType [{0}] Failure [{1}].", typeID, failure);
                    break;
            }
            
            // Handle some failures that could happen from any transaction type
            switch (failure) {
                case TransactionPrecheckFailure.EXISTING_TRANSACTION_ID:
                    precheckFailure = "Transaction conflicted with existing transacion record with identical ID.";
                    instruction = tryAgainRelog;
                    break;
            }
            
            // build failure message from temp strings
            string failureDetails = String.Format("Details:\n   {0}", txnTypeFailure);
            if (!String.IsNullOrEmpty(precheckFailure)) {
                failureDetails = String.Format("{0}\n   {1}", failureDetails, precheckFailure);
            }
            string failureMsg = String.Format("Transaction precheck FAILURE.\n{0}\n\n{1}\n", failureDetails, instruction);
            
            // send failure message to client
            // For now, only alert payer for simplicity and since We should only ever get here from an ObjectBuy
            // TODO: replace UUID.Zero with the agentID of the payer once we add it to funciton args. -- not vital that these make it to offline user.
            sendMessageToClient(payerClient, failureMsg, UUID.Zero);

        }
        
        /**** Functions to handle messaging transaction status to users (after GloebitAPI.Transaction has been built) ****/
        
        /// <summary>
        /// Called just prior to the application submitting a transaction to Gloebit.
        /// This function should be used to provide immediate feedback to a user that their request/interaction was received.
        /// It is assumed that this is almost instantaneous and should be the source of immediate feedback that the user's action
        /// has resulted in a transaction.  If something added to the application's preparation is likely to delay this, then
        /// the application may wish to lower the priority of this message in favor of messaging the start of preparation.
        /// Once this is called, an alert for 1 or more stage status will be received and a transaction completion alert.
        /// </summary>
        /// <param name="txn">Transaction that failed.</param>
        /// <param name="description">String containing txn description since this is not in the Transaction class yet.</param>
        private void alertUsersTransactionBegun(GloebitAPI.Transaction txn, string description)
        {
            // TODO: make user configurable
            bool showDetailsWithTxnBegun = true;
            bool showIDWithTxnBegun = false;
            
            // TODO: consider using Txn.TransactionTypeString
            String actionStr = String.Empty;
            String payeeActionStr = String.Empty;
            bool messagePayee = false;
            switch ((TransactionType)txn.TransactionType) {
                case TransactionType.USER_BUYS_OBJECT:
                    // Alert payer only; payee will be null
                    switch (txn.SaleType) {
                        case 1: // Sell as original (in-place sale)
                            actionStr = String.Format("Purchase Original: {0}", txn.PartName);
                            break;
                        case 2: // Sell a copy
                            actionStr = String.Format("Purchase Copy: {0}", txn.PartName);
                            break;
                        case 3: // Sell contents
                            actionStr = String.Format("Purchase Contents: {0}", txn.PartName);
                            break;
                        default:
                            // Should not get here as this should fail before transaction is built.
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] Transaction Begun With Unrecognized saleType:{0} --- expected 1,2 or 3 for original, copy, or contents", txn.SaleType);
                            // TODO: Assert this.
                            //assert(txn.TransactionType >= 1 && txn.TransactionType <= 3);
                            break;
                    }
                    break;
                case TransactionType.OBJECT_PAYS_USER:
                    // Alert payer and payee, as we don't know who triggered it.
                    // This looks like a message for payee, but is sent to payer
                    actionStr = String.Format("Auto-debit created by object: {0}", txn.PartName);
                    payeeActionStr = String.Format("Payment to you from object: {0}", txn.PartName);
                    messagePayee = true;
                    break;
                case TransactionType.USER_PAYS_USER:
                    // Alert payer only
                    actionStr = String.Format("Paying User: {0}", txn.PayeeName);
                    break;
                case TransactionType.USER_PAYS_OBJECT:
                    // Alert payer only
                    actionStr = String.Format("Paying Object: {0}", txn.PartName);
                    break;
                case TransactionType.USER_BUYS_LAND:
                    // Alert payer only
                    actionStr = "Purchase Land.";
                    break;
                case TransactionType.USER_BUYS_LANDPASS:
                    // Alert Payer only
                    actionStr = "Purcahase Land Pass.";
                    break;
                case TransactionType.FEE_GROUP_CREATION:
                    // Alert payer only.  Payee is App.
                    actionStr = "Paying Grid to create a group";
                    break;
                case TransactionType.FEE_UPLOAD_ASSET:
                    // Alert payer only.  Payee is App.
                    actionStr = "Paying Grid to upload an asset";
                    break;
                case TransactionType.FEE_CLASSIFIED_AD:
                    // Alert payer only.  Payee is App.
                    actionStr = "Paying Grid to place a classified ad";
                    break;
                default:
                    // Alert payer and payee
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] alertUsersTransactionPreparationFailure: Unimplemented TransactionBegun TransactionType [{0}] with description [{1}].", txn.TransactionType, description);
                    actionStr = "";
                    break;
            }
            
            // Alert payer
            IClientAPI payerClient = LocateClientObject(txn.PayerID);
            // TODO: remove description once in txn and managed in sendTxnStatusToClient
            //string baseStatus = String.Format("Submitting transaction request...\n   {0}", actionStr);
            string baseStatus = String.Format("Submitting transaction request...\n   {0}\nDescription: {1}", actionStr, description);
            sendTxnStatusToClient(txn, payerClient, baseStatus, showDetailsWithTxnBegun, showIDWithTxnBegun);
            
            // If necessary, alert Payee
            if (messagePayee && (txn.PayerID != txn.PayeeID)) {
                IClientAPI payeeClient = LocateClientObject(txn.PayeeID);
                // TODO: remove description once in txn and managed in sendTxnStatusToClient
                // string payeeBaseStatus = String.Format("Submitting transaction request...\n   {0}", payeeActionStr);
                string payeeBaseStatus = String.Format("Submitting transaction request...\n   {0}\nDescription: {1}", payeeActionStr, description);
                sendTxnStatusToClient(txn, payeeClient, payeeBaseStatus, showDetailsWithTxnBegun, showIDWithTxnBegun, txn.PayeeID);
            }
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
        ///             --- currently, this is txn == null which doesn't happen in OpenSim and will eventually switch to using an "asset".
        /// --- Enacted:
        /// ------ Funds transferred: AssetEnact started (all Gloebit components of transaction enacted successfully)
        /// ------ Asset enacted: In Opensim, object delivery or notification of payment completing (local components of transaction enacted successfully)
        /// --- Consumed: Finalized (notified of successful enact across all components.  commit enacts.  Eventually, all enacts will be consumed/finalized/committed).
        /// --- Canceled: Probably shouldn't ever get called.  Worth logging.
        /// </summary>
        /// <param name="txn">Transaction that failed.</param>
        /// <param name="stage">TransactionStage which the transaction successfully completed to drive this alert.</param>
        /// <param name="additionalDetails">String containing additional details to be appended to the alert message.</param>
        private void alertUsersTransactionStageCompleted(GloebitAPI.Transaction txn, GloebitAPI.TransactionStage stage, string additionalDetails)
        {
            // TODO: make user configurable
            bool reportAllTxnStagesOverride = false;
            bool reportNoTxnStagesOverride = false;
            Dictionary<GloebitAPI.TransactionStage, bool> reportTxnStageMap = new Dictionary<GloebitAPI.TransactionStage, bool>();
            reportTxnStageMap[GloebitAPI.TransactionStage.ENACT_ASSET] = true;
            
            // Determine if we are going to report this stage
            bool reportThisStage = false;
            if (reportTxnStageMap.ContainsKey(stage)) {
                reportThisStage = reportTxnStageMap[stage];
            }
            if (!(reportAllTxnStagesOverride || (reportThisStage && !reportNoTxnStagesOverride))) {
                return;
            }
            
            // TODO: make user configurable
            bool showDetailsWithTxnStage = false;
            bool showIDWithTxnStage = false;
            
            string status = String.Empty;
            
            switch (stage) {
                case GloebitAPI.TransactionStage.SUBMIT:
                    status = "Successfully submitted to Gloebit service.";
                    break;
                case GloebitAPI.TransactionStage.QUEUE:
                    // a) queued and gloebits transfered.
                    // b) resubmitted
                    // c) queued, but early enact failure
                    status = "Successfully received by Gloebit and queued for processing.";
                    break;
                case GloebitAPI.TransactionStage.ENACT_GLOEBIT:
                    status = "Successfully transferred gloebits.";
                    break;
                case GloebitAPI.TransactionStage.ENACT_ASSET:
                    switch ((TransactionType)txn.TransactionType) {
                        case TransactionType.USER_BUYS_OBJECT:
                            // 5000 - ObjectBuy
                            // delivered the object/contents purchased.
                            switch (txn.SaleType) {
                                case 1: // Sell as original (in-place sale)
                                    status = "Successfully delivered object.";
                                    break;
                                case 2: // Sell a copy
                                    status = "Successfully delivered copy of object to inventory.";
                                    break;
                                case 3: // Sell contents
                                    status = "Successfully delivered object contents to inventory.";
                                    break;
                                default:
                                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] alertUsersTransactionStageCompleted called on unknown sale type: {0}", txn.SaleType);
                                    ////status = "Successfully enacted local components of transaction.";
                                    break;
                            }
                            break;
                        case TransactionType.USER_PAYS_USER:
                            // 5001 - OnMoneyTransfer - Pay User
                            // nothing local enacted
                            ////status = "Successfully enacted local components of transaction.";
                            break;
                        case TransactionType.USER_PAYS_OBJECT:
                            // 5008 - OnMoneyTransfer - Pay Object
                            // alerted the object that it has been paid.
                            status = "Successfully notified object of payment.";
                            break;
                        case TransactionType.OBJECT_PAYS_USER:
                            // 5009 - ObjectGiveMoney
                            // nothing local enacted
                            ////status = "Successfully enacted local components of transaction.";
                            break;
                        case TransactionType.USER_BUYS_LAND:
                            // 5002 - OnLandBuy
                            // land transferred
                            status = "Successfully transferred parcel to new owner.";
                            break;
                        case TransactionType.USER_BUYS_LANDPASS:
                            // 5006 - OnParcelBuyPass pre 0.9.1; MoveMoney post;
                            // avatar added to parcel access list with expiration
                            if (!m_newLandPassFlow) {
                                status = "Successfully added you to parcel access list.";
                            } else {
                                status = "Land Management System should be adding you to the parcel access list now.";
                            }
                            break;
                        case TransactionType.FEE_GROUP_CREATION:
                            // 1002 - ApplyCharge
                            // Nothing local enacted.
                            break;
                        case TransactionType.FEE_UPLOAD_ASSET:
                            // 1101 - ApplyUploadCharge
                            // Nothing local enacted
                            break;
                        case TransactionType.FEE_CLASSIFIED_AD:
                            // 1103 - ApplyCharge
                            // Nothing local enacted
                            break;
                        default:
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] alertUsersTransactionStageCompleted called on unknown transaction type: {0}", txn.TransactionType);
                            // TODO: should we throw an exception?  return null?  just continue?
                            // take no action.
                            ////status = "Successfully enacted local components of transaction.";
                            break;
                    }
                    break;
                case GloebitAPI.TransactionStage.CONSUME_GLOEBIT:
                    status = "Successfully finalized transfer of gloebits.";
                    break;
                case GloebitAPI.TransactionStage.CONSUME_ASSET:
                    status = "Successfully finalized local components of transaction.";
                    break;
                case GloebitAPI.TransactionStage.CANCEL_GLOEBIT:
                    status = "Successfully canceled and rolled back transfer of gloebits.";
                    break;
                case GloebitAPI.TransactionStage.CANCEL_ASSET:
                    status = "Successfully canceled and rolled back local components of transaction.";
                    break;
                default:
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] alertUsersTransactionStageCompleted called on unhandled transaction stage : {0}", stage);
                    // TODO: should we throw an exception?  return null?  just continue?
                    status = "Successfully completed undefined transaction stage";
                    break;
            }
            
            // If this is a stage we have not stored a status for, then don't send a message
            if (String.IsNullOrEmpty(status)) {
                return;
            }
            
            if (!String.IsNullOrEmpty(additionalDetails)) {
                status = String.Format("{0}\n{1}", status, additionalDetails);
            }
            
            // for now, we're only giong to send these to the payer.
            IClientAPI payerClient = LocateClientObject(txn.PayerID);
            sendTxnStatusToClient(txn, payerClient, status, showDetailsWithTxnStage, showIDWithTxnStage);
        }
        
        /// <summary>
        /// Called when transaction completes with failure.
        /// At a minimum, this should always be messaged to the user who triggered the transaction.
        /// </summary>
        /// <param name="txn">Transaction that failed.</param>
        /// <param name="stage">TransactionStage in which the transaction failed to drive this alert.</param>
        /// <param name="failure">TransactionFailure code providing necessary differentiation on specific failure within a stage.</param>
        /// <param name="additionalFailureDetails">String containing additional details to be appended to the alert message.</param>
        private void alertUsersTransactionFailed(GloebitAPI.Transaction txn, GloebitAPI.TransactionStage stage, GloebitAPI.TransactionFailure failure, string additionalFailureDetails)
        {
            // TODO: make user configurable
            bool showDetailsWithTxnFailed = false;
            bool showIDWithTxnFailed = true;
            // TODO: how does this work with instructions?
            
            // TODO: move these to a string resource at some point.
            // Set up instruction strings which are used mutliple times
            string tryAgainContactOwner = String.Format("Please try again.  If problem persists, contact {0}.", m_contactOwner);
            string tryAgainContactGloebit = String.Format("Please try again.  If problem persists, contact {0}.", m_contactGloebit);
            string subAuthDialogComing = "You will be presented with an additional message instructing you how to approve or deny authorization for future automated transactions for this subscription.";
            string contactPayee = "Please alert seller/payee to this issue if possible and have seller/payee contact Gloebit.";
            string contactPayer = "Please alert buyer/payer to this issue.";
            
            // Set up temp strings to hold failure messages based on transaction type and failure
            string error = String.Empty;
            string instruction = String.Empty;
            string payeeInstruction = String.Empty;
            //string payeeAlert = String.Empty;
            
            // Separate message for when payee needs an alert whether or not payee knew about transaciton start.
            bool messagePayee = false;
            string payeeMessage = String.Empty;
            
            // Retrieve failure strings into temp variables based on transaction type and failure
            switch (stage) {
                case GloebitAPI.TransactionStage.SUBMIT:
                    error = "Region failed to propery create and send request to Gloebit.";
                    instruction = payeeInstruction = tryAgainContactOwner;
                    break;
                case GloebitAPI.TransactionStage.AUTHENTICATE:
                    // Only thing that should cause this right now is an invalid token, so we'll ignore the failure variable.
                    error = "Payer's authorization of this app has been revoked or expired.";
                    instruction = "Please re-authenticate with Gloebit.";
                    // TODO: write a better message.  Also, should we trigger auth message with link?
                    break;
                case GloebitAPI.TransactionStage.VALIDATE:
                    switch (failure) {
                        // Validate Form
                        case GloebitAPI.TransactionFailure.FORM_GENERIC_ERROR:                    /* One of many form errors.  something needs fixing.  See reason */
                            error = "Application provided malformed transaction to Gloebit.";
                            instruction = payeeInstruction = tryAgainContactOwner;
                            break;
                        case GloebitAPI.TransactionFailure.FORM_MISSING_SUBSCRIPTION_ID:          /* marked as subscription, but did not include any subscription id */
                            error = "Missing subscription-id from transaction marked as subscription payment.";
                            instruction = payeeInstruction = tryAgainContactOwner;
                            break;
                        // Validate Subscription
                        case GloebitAPI.TransactionFailure.SUBSCRIPTION_NOT_FOUND:              /* No sub found under app + identifiers provided */
                            error = "Gloebit did not find a subscription with the id provided for this subscription payment.";
                            instruction = payeeInstruction = tryAgainContactOwner;
                            break;
                        // Validate Subscription Authorization
                        case GloebitAPI.TransactionFailure.SUBSCRIPTION_AUTH_NOT_FOUND:         /* No sub_auth has been created to request authorizaiton yet */
                            error = "Payer has not authorized payments for this subscription.";
                            instruction = subAuthDialogComing;
                            payeeInstruction = contactPayer;
                            break;
                        case GloebitAPI.TransactionFailure.SUBSCRIPTION_AUTH_PENDING:
                            error = "Payer has a pending authorization for this subscription.";
                            instruction = subAuthDialogComing;
                            payeeInstruction = contactPayer;
                            break;
                        case GloebitAPI.TransactionFailure.SUBSCRIPTION_AUTH_DECLINED:
                            error = "Payer has declined authorization for this subscription.";
                            instruction = "You can review and alter your respsonse subscription authorization requests from the Subscriptions section of the Gloebit website.";
                            payeeInstruction = contactPayer;
                            break;
                        // Validate Payer
                        case GloebitAPI.TransactionFailure.PAYER_ACCOUNT_LOCKED:
                            // TODO: should this message be BUYER ONLY?  Is this a privacy issue?
                            error = "Payer's Gloebit account is locked.";
                            instruction = "Please contact Gloebit to resolve any account status issues.";
                            payeeInstruction = contactPayer;
                            break;
                        // Validate Payee
                        case GloebitAPI.TransactionFailure.PAYEE_CANNOT_BE_IDENTIFIED:
                            // message payer and payee
                            error = "Gloebit can not identify payee from OpenSim account.";
                            instruction = "Please alert seller/payee to this issue.  They should run through the authorization flow from this grid to link their OpenSim agent to a Gloebit account.";
                            payeeInstruction = "Please ensure your OpenSim account has an email address, and that you have verified this email address in your Gloebit account.  If you are a hypergrid user with a foreign home grid, then your email address is not provided, so you will need to authorize this Grid in order to create a link from this agent to your Gloebit account.  You can immediately revoke your authorization if you don't want this Grid to be able to charge your account.  We will continue to send received funds to the last Gloebit account linked to this avatar.";
                            messagePayee = true;
                            payeeMessage = String.Format("Gloebit:\nAttempt to pay you failed because we cannot identify your Gloebit account from your OpenSim account.\n\n{0}", payeeInstruction);
                            break;
                        case GloebitAPI.TransactionFailure.PAYEE_CANNOT_RECEIVE:
                            // message payer and payee
                            // TODO: Is it a privacy issue to alert buyer here?
                            // TODO: research if/when account is in this state.  Only by admin?  All accounts until merchants?
                            error = "Payee's Gloebit account is unable to receive gloebits.";
                            instruction = contactPayee;
                            payeeInstruction = String.Format("Please contact {0} to address this issue", m_contactGloebit);
                            messagePayee = true;
                            payeeMessage = String.Format("Gloebit:\nAttempt to pay you failed because your Gloebit account cannot receive gloebits.\n\n{0}", payeeInstruction);
                            break;
                        default:
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] alertUsersTransactionFailed called on unhandled validation failure : {0}", failure);
                            error = "Validation error.";
                            instruction = payeeInstruction = tryAgainContactOwner;
                            break;
                    }
                    break;
                case GloebitAPI.TransactionStage.QUEUE:
                    // only one error which should make it here right now, and it's generic.
                    error = "Queuing Error.";
                    instruction = payeeInstruction = tryAgainContactGloebit;
                    break;
                case GloebitAPI.TransactionStage.ENACT_GLOEBIT:
                    // We get these through early-enact failures via transactU2UCompleted call
                    error = "Transfer of gloebits failed.";
                    switch (failure) {
                        case GloebitAPI.TransactionFailure.INSUFFICIENT_FUNDS:
                            error = String.Format("{0}  Insufficient funds.", error);
                            instruction = "Go to https://www.gloebit.com/purchase to get more gloebits.";
                            payeeInstruction = contactPayer;    // not considering privacy issue since caused by auto-debit and payer would want to know of failure.
                            break;
                        default:
                            error = String.Format("{0}  Failure during processing.", error);
                            instruction = payeeInstruction = tryAgainContactOwner;
                            break;
                    }
                    break;
                case GloebitAPI.TransactionStage.ENACT_ASSET:
                    switch ((TransactionType)txn.TransactionType) {
                        case TransactionType.USER_BUYS_OBJECT:
                            // 5000 - ObjectBuy
                            // delivered the object/contents purchased.
                            //// additional_details will include one of the following
                            ////"Can't locate buyer."
                            ////"Can't access IBuySellModule."
                            ////"IBuySellModule.BuyObject failed delivery attempt."
                            switch (txn.SaleType) {
                                case 1: // Sell as original (in-place sale)
                                    error = "Delivery of object failed.";
                                    break;
                                case 2: // Sell a copy
                                    error = "Delivery of object copy failed.";
                                    break;
                                case 3: // Sell contents
                                    error = "Delivery of object contents failed.";
                                    break;
                                default:
                                    error = "Enacting of local transaction components failed.";
                                    break;
                            }
                            break;
                        case TransactionType.USER_PAYS_USER:
                            // 5001 - OnMoneyTransfer - Pay User
                            // nothing local enacted
                            // Currently, shouldn't ever get here.
                            error = "Enacting of local transaction components failed.";
                            break;
                        case TransactionType.USER_PAYS_OBJECT:
                            // 5008 - OnMoneyTransfer - Pay Object
                            // alerted the object that it has been paid.
                            error = "Object payment notification failed.";
                            break;
                        case TransactionType.OBJECT_PAYS_USER:
                            // 5009 - ObjectGiveMoney
                            // TODO: who to alert payee, or payer.
                            // Currently, shouldn't ever get here.
                            error = "Enacting of local transaction components failed.";
                            break;
                        case TransactionType.USER_BUYS_LAND:
                            // 5002 - OnLandBuy
                            // land transfer failed
                            error = "Transfer of parcel to new owner failed.";
                            instruction = tryAgainContactOwner;
                            break;
                        default:
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] alertUsersTransactionFailed called on unknown transaction type: {0}", txn.TransactionType);
                            // TODO: should we throw an exception?  return null?  just continue?
                            error = "Enacting of local transaction components failed.";
                            break;
                    }
                    break;
                default:
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] alertUsersTransactionFailed called on unhandled transaction stage : {0}", stage);
                    // TODO: should we throw an exception?  return null?  just continue?
                    error = "Unhandled transaction failure.";
                    break;
            }
            
            // build failure alert from temp strings
            string status = String.Format("Transaction FAILED.\n   {0}", error);
            if (!String.IsNullOrEmpty(additionalFailureDetails)) {
                status = String.Format("{0}\n{1}", status, additionalFailureDetails);
            }
            string statusAndInstruction = status;
            if (!String.IsNullOrEmpty(instruction)) {
                statusAndInstruction = String.Format("{0}\n{1}", status, instruction);
            }
            
            // send failure alert to payer
            IClientAPI payerClient = LocateClientObject(txn.PayerID);
            sendTxnStatusToClient(txn, payerClient, statusAndInstruction, showDetailsWithTxnFailed, showIDWithTxnFailed);
            
            // Determine if alert needs to be sent to payee and send
            IClientAPI payeeClient = null;
            if (txn.TransactionType == (int)TransactionType.OBJECT_PAYS_USER || messagePayee) {
                // locate payee since we'll need to message
                payeeClient = LocateClientObject(txn.PayeeID);
            }
            // If this is a transaction type where we notified the payer the txn started, we should alert to failure as payer may have triggered the txn
            if (txn.TransactionType == (int)TransactionType.OBJECT_PAYS_USER) {
                // build failure alert from temp strings
                statusAndInstruction = status;
                if (!String.IsNullOrEmpty(payeeInstruction)) {
                    statusAndInstruction = String.Format("{0}\n{1}", status, payeeInstruction);
                }
                sendTxnStatusToClient(txn, payeeClient, statusAndInstruction, showDetailsWithTxnFailed, showIDWithTxnFailed, txn.PayeeID);
            }
            
            // If necessary, send separate message to Payee
            if (messagePayee) {
                sendMessageToClient(payeeClient, payeeMessage, txn.PayeeID);
                // TODO: this message should be delivered to email if client is not online and didn't trigger this message.
                
                // Since unidentified seller can now be fixed by auth, send the auth link if they are online
                if (payeeClient != null && failure == GloebitAPI.TransactionFailure.PAYEE_CANNOT_BE_IDENTIFIED) {
                    GloebitAPI.User payeeUser = GloebitAPI.User.Get(m_api, payeeClient.AgentId);
                    m_api.Authorize(payeeUser, payeeClient.Name, BaseURI);
                }
            }
            
        }
        
        /// <summary>
        /// Called when a transaction has successfully completed so that necessary notification can be triggered.
        /// At a minimum, this should notify the user who triggered the transaction.
        /// </summary>
        /// <param name="txn">Transaction that succeeded.</param>
        private void alertUsersTransactionSucceeded(GloebitAPI.Transaction txn)
        {
            // TODO: make user configurable
            bool showDetailsWithTxnSucceeded = false;
            bool showIDWithTxnSucceeded = false;
            
            IClientAPI payerClient = LocateClientObject(txn.PayerID);
            IClientAPI payeeClient = LocateClientObject(txn.PayeeID);   // get this regardless of messaging since we'll try to update balance
            
            // send success message to payer
            sendTxnStatusToClient(txn, payerClient, "Transaction SUCCEEDED.", showDetailsWithTxnSucceeded, showIDWithTxnSucceeded);
            
            // If this is a transaction type where we notified the payee the txn started, we should alert to successful completion
            if ((txn.TransactionType == (int)TransactionType.OBJECT_PAYS_USER) && (txn.PayerID != txn.PayeeID)) {
                sendTxnStatusToClient(txn, payeeClient, "Transaction SUCCEEDED.", showDetailsWithTxnSucceeded, showIDWithTxnSucceeded, txn.PayeeID);
            }
            // If this transaction was one user paying another, if the user is online, we should let them know they received a payment
            if (txn.TransactionType == (int)TransactionType.USER_PAYS_USER) {
                string message = String.Format("You've received Gloebits from {0}.", resolveAgentName(payerClient.AgentId));
                sendTxnStatusToClient(txn, payeeClient, message, true, showIDWithTxnSucceeded, txn.PayeeID);
            }
            // TODO: consider if we want to send an alert that payee earned money with transaction details for other transaction types
            
            // TODO: should consider updating API to return payee ending balance as well.  Potential privacy issue here if not approved to see balance.
            
            // TODO: Once we store description in txn, change 3rd arg in SMB below to Utils.StringToBytes(description)
            
            // Update Payer & Payee balances if still logged in.
            if (payerClient != null) {
                if (txn.PayerEndingBalance >= 0) {  /* if -1, got an invalid balance in response.  possible this shouldn't ever happen */
                    payerClient.SendMoneyBalance(txn.TransactionID, true, new byte[0], txn.PayerEndingBalance, txn.TransactionType, txn.PayerID, false, txn.PayeeID, false, txn.Amount, txn.PartDescription);
                } else {
                    // TODO: consider what this delays while it makes non async call GetBalance from GetAgentBalance call get balance
                    int payerBalance = (int)GetAgentBalance(txn.PayerID, payerClient, true);
                    payerClient.SendMoneyBalance(txn.TransactionID, true, new byte[0], payerBalance, txn.TransactionType, txn.PayerID, false, txn.PayeeID, false, txn.Amount, txn.PartDescription);
                }
            }
            if ((payeeClient != null) && (txn.PayerID != txn.PayeeID)) {
                // TODO: consider what this delays while it makes non async call GetBalance from GetAgentBalance call get balance
                int payeeBalance = (int)GetAgentBalance(txn.PayeeID, payeeClient, false);
                payeeClient.SendMoneyBalance(txn.TransactionID, true, new byte[0], payeeBalance, txn.TransactionType, txn.PayerID, false, txn.PayeeID, false, txn.Amount, txn.PartDescription);
            }
        }
        
        #endregion // GMM User Messaging
        
        #region GMM enums
        
        // TODO: consider replacing with libOpenMetaverse MoneyTransactionType
        // https://github.com/openmetaversefoundation/libopenmetaverse/blob/master/OpenMetaverse/AgentManager.cs#L342
        public enum TransactionType : int
        {
            /* Fees */
            FEE_GROUP_CREATION  = 1002,             // comes through ApplyCharge
            FEE_UPLOAD_ASSET    = 1101,             // comes through ApplyUploadCharge
            FEE_CLASSIFIED_AD   = 1103,             // comes through ApplyCharge
            FEE_GENERAL         = 1104,             // here for anything we're unaware of yet.
            
            /* Purchases */
            USER_BUYS_OBJECT    = 5000,             // comes through ObjectBuy
            USER_PAYS_USER      = 5001,             // comes through OnMoneyTransfer
            USER_BUYS_LAND      = 5002,             // comes through scene events OnValidateLandBuy and OnLandBuy
            REFUND              = 5005,             // not yet implemented
            USER_BUYS_LANDPASS  = 5006,             // comes through ParcelBuyPass pre 0.9.1; MoveMoney post 0.9.1
            USER_PAYS_OBJECT    = 5008,             // comes through OnMoneyTransfer
            
            /* Auto-Debit Subscription */
            OBJECT_PAYS_USER    = 5009,             // script auto debit owner - comes thorugh ObjectGiveMoney
            
            /* Catch-all for unimplemented MoveMoney types */
            MOVE_MONEY_GENERAL  = 5011,             // Unimplemented MoveMoney - will fail.
        }
        
        public enum TransactionPrecheckFailure : int
        {
            BUYING_DISABLED,
            OBJECT_NOT_FOUND,
            AMOUNT_MISMATCH,
            SALE_TYPE_INVALID,
            SALE_TYPE_MISMATCH,
            BUY_SELL_MODULE_INACCESSIBLE,
            LAND_VALIDATION_FAILED,
            EXISTING_TRANSACTION_ID,
            GROUP_OWNED,
        }
        
        #endregion // GMM enums
        
        #region GMM helper classes
        
        // TODO: Can these be moved to another file?  Do they need to be in the GMM class?
        
        /*********************************************************
         ********** LOGIN BALANCE REQUEST helper class ***********
         *********************************************************/
        
        /// <summary>
        /// Class which is a hack to deal with the fact that a balance request is made
        /// twice when a user logs into a GMM enabled region (once for connect to region and once by viewer after login).
        /// This causes the balance to be reqeusted twice, and if not authed, the user to be asked to auth twice.
        /// This class is designed solely for preventing the second request in that single case.
        /// </summary>
        private class LoginBalanceRequest
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
        
        /*********************************************************
         ********** DIALOG helper class **************************
         *********************************************************/
        
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
                // m_log.InfoFormat("[GLOEBITMONEYMODULE] OnChatFromClientAPI from:{0} chat:{1}", sender, chat);
                // m_log.InfoFormat("[GLOEBITMONEYMODULE] OnChatFromClientAPI \n\tmessage:{0} \n\ttype: {1} \n\tchannel: {2} \n\tposition: {3} \n\tfrom: {4} \n\tto: {5} \n\tsender: {6} \n\tsenderObject: {7} \n\tsenderUUID: {8} \n\ttargetUUID: {9} \n\tscene: {10}", chat.Message, chat.Type, chat.Channel, chat.Position, chat.From, chat.To, chat.Sender, chat.SenderObject, chat.SenderUUID, chat.TargetUUID, chat.Scene);
                
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
                // if (chat.SenderUUID != UUID.Zero || chat.TargetUUID != UUID.Zero || !String.IsNullOrEmpty(chat.From) || !String.IsNullOrEmpty(chat.To) || chat.Type != ChatTypeEnum.Region) {
                if (chat.SenderUUID != UUID.Zero || !String.IsNullOrEmpty(chat.From) || chat.Type != ChatTypeEnum.Region) {
                    // m_log.WarnFormat("[GLOEBITMONEYMODULE] OnChatFromClientAPI Received message on Gloebit dialog channel:{0} which may be an attempted impersonation. SenderUUID:{1}, TargetUUID:{2}, From:{3} To:{4} Type: {5} Message:{6}", chat.Channel, chat.SenderUUID, chat.TargetUUID, chat.From, chat.To, chat.Type, chat.Message);
                    m_log.WarnFormat("[GLOEBITMONEYMODULE] OnChatFromClientAPI Received message on Gloebit dialog channel:{0} which may be an attempted impersonation. SenderUUID:{1}, From:{2}, Type: {3}, Message:{4}", chat.Channel, chat.SenderUUID, chat.From, chat.Type, chat.Message);
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
                    // IF null, there was a db error on storing this -- test store functions for db impl
                    if (sub == null) {
                        string msg = String.Format("[GLOEBITMONEYMODULE] CreateSubscriptionAuthorizationDialog.ProcessResponse Could not retrieve subscription.  Likely DB error when storing subID:{0}", subscriptionIDStr);
                        m_log.Error(msg);
                        throw new Exception(msg);
                    }
                    
                    // Get GloebitAPI.User for this agent
                    GloebitAPI.User user = GloebitAPI.User.Get(this.api, AgentID);
                    
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
                    GloebitAPI.User user = GloebitAPI.User.Get(api, client.AgentId);
                    api.SendSubscriptionAuthorizationToUser(user, SubscriptionAuthorizationID.ToString(), sub, false);
                    
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
        
        #endregion // GMM helper classes
        
    }
}
