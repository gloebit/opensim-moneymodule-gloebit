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

        private string m_key;
        private string m_keyAlias;
        private string m_secret;
        private Uri m_url;


        public class User {
            public string PrincipalID;
            public string GloebitID;
            public string GloebitToken;

            // TODO - update tokenMap to be a proper LRU Cache and hold User objects
            private static Dictionary<string,string> s_tokenMap = new Dictionary<string, string>();

            public User() {
            }

            private User(string principalID, string gloebitID, string token) {
                this.PrincipalID = principalID;
                this.GloebitID = gloebitID;
                this.GloebitToken = token;
            }

            public static User Get(UUID agentID) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] in User.Get");
                string agentIdStr = agentID.ToString();
                string token;
                lock(s_tokenMap) {
                    s_tokenMap.TryGetValue(agentIdStr, out token);
                }

                if(token == null) {
                    m_log.InfoFormat("[GLOEBITMONEYMODULE] Looking for prior token for {0}", agentIdStr);
                    User[] users = GloebitUserData.Instance.Get("PrincipalID", agentIdStr);

                    switch(users.Length) {
                        case 1:
                            User u = users[0];
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] FOUND USER TOKEN! {0} {1}", u.PrincipalID, u.GloebitToken);
                            return u;
                        case 0:
                            return new User(agentIdStr, null, token);
                        default:
                           throw new Exception(String.Format("[GLOEBITMONEYMODULE] Failed to find exactly one prior token for {0}", agentIdStr));
                    }
                    // TODO - use the Gloebit identity service for userId
                }

                //return null;
                return new User (agentIdStr, null, token);
            }

            public static User Init(UUID agentId, string token) {
                string agentIdStr = agentId.ToString();
                lock(s_tokenMap) {
                    s_tokenMap[agentIdStr] = token;
                }

                User u = new User(agentIdStr, UUID.Zero.ToString(), token);
                GloebitUserData.Instance.Store(u);
                return u;
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

        public GloebitAPI(string key, string keyAlias, string secret, Uri url) {
            m_key = key;
            m_keyAlias = keyAlias;
            m_secret = secret;
            m_url = url;
        }
        
        /************************************************/
        /******** OAUTH2 AUTHORIZATION FUNCTIONS ********/
        /************************************************/


        /// <summary>
        /// Helper function to build the auth redirect callback url consistently everywhere.
        /// <param name="baseURL">The base url where this server's http services can be accessed.</param>
        /// <param name="baseURL">The uuid of the agent being authorized.</param>
        /// </summary>
        private static Uri BuildAuthCallbackURL(Uri baseURL, UUID agentId) {
            UriBuilder redirect_uri = new UriBuilder(baseURL);
            redirect_uri.Path = "gloebit/auth_complete";
            redirect_uri.Query = String.Format("agentId={0}", agentId);
            return redirect_uri;
        }

        /// <summary>
        /// Request Authorization for this grid/region to enact Gloebit functionality on behalf of the specified OpenSim user.
        /// Sends Authorize URL to user which will launch a Gloebit authorize dialog.  If the user launches the URL and approves authorization from a Gloebit account, an authorization code will be returned to the redirect_uri.
        /// This is how a user links a Gloebit account to this OpenSim account.
        /// </summary>
        /// <param name="user">OpenSim User for which this region/grid is asking for permission to enact Gloebit functionality.</param>
        public void Authorize(IClientAPI user, Uri baseURL) {

            //********* BUILD AUTHORIZE QUERY ARG STRING ***************//
            ////Dictionary<string, string> auth_params = new Dictionary<string, string>();
            OSDMap auth_params = new OSDMap();

            auth_params["client_id"] = m_key;
            if(m_keyAlias != null && m_keyAlias != "") {
                auth_params["r"] = m_keyAlias;
            }

            auth_params["scope"] = "user balance transact";
            auth_params["redirect_uri"] = BuildAuthCallbackURL(baseURL, user.AgentId).ToString();
            auth_params["response_type"] = "code";
            auth_params["user"] = user.AgentId.ToString();
            // TODO - make use of 'state' param for XSRF protection
            // auth_params["state"] = ???;

            string query_string = BuildURLEncodedParamString(auth_params);

            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Authorize query_string: {0}", query_string);

            //********** BUILD FULL AUTHORIZE REQUEST URI **************//

            Uri request_uri = new Uri(m_url, String.Format("oauth2/authorize?{0}", query_string));
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Authorize request_uri: {0}", request_uri);
            
            //*********** SEND AUTHORIZE REQUEST URI TO USER ***********//
            // currently can not launch browser directly for user, so send in message

            string message = String.Format("To use Gloebit currency, please autorize Gloebit to link to your avatar's account on this web page: {0}", request_uri);
            user.SendBlueBoxMessage(UUID.Zero, "Gloebit", message);
            // use SendBlueBoxMessage as all others including SendLoadURL truncate to 255 char or below

        }
        
        /// <summary>
        /// Begins request to exchange an authorization code granted from the Authorize endpoint for an access token necessary for enacting Gloebit functionality on behalf of this OpenSim user.
        /// This begins the second phase of the OAuth2 process.  It is activated by the redirect_uri of the Authorize function.
        /// This occurs completely behind the scenes for security purposes.
        /// </summary>
        /// <returns>The authenticated User object containing the access token necessary for enacting Gloebit functionality on behalf of this OpenSim user.</returns>
        /// <param name="user">OpenSim User for which this region/grid is asking for permission to enact Gloebit functionality.</param>
        /// <param name="auth_code">Authorization Code returned to the redirect_uri from the Gloebit Authorize endpoint.</param>
        public void ExchangeAccessToken(IClientAPI user, string auth_code, Uri baseURL) {
            
            //TODO stop logging auth_code
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.ExchangeAccessToken Name:[{0}] AgentID:{1} auth_code:{1}", user.Name, user.AgentId, auth_code);
            
            // ************ BUILD EXCHANGE ACCESS TOKEN POST REQUEST ******** //
            OSDMap auth_params = new OSDMap();

            auth_params["client_id"] = m_key;
            auth_params["client_secret"] = m_secret;
            auth_params["code"] = auth_code;
            auth_params["grant_type"] = "authorization_code";
            auth_params["scope"] = "user balance transact";
            auth_params["redirect_uri"] = BuildAuthCallbackURL(baseURL, user.AgentId).ToString();
            
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
                        // TODO - do something to handle the "refresh_token" field properly
                        if(token != String.Empty) {
                            User u = User.Init(user.AgentId, token);
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CompleteExchangeAccessToken Success User:{0}", u);

                            // TODO: If we need to alert any process that this is complete, now is the time.
                        } else {
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CompleteExchangeAccessToken error: {0}, reason: {1}", responseDataMap["error"], responseDataMap["reason"]);
                            // TODO: signal error;
                        }
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
        public double GetBalance(User user) {
            
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.balance for agentID:{0}", user.PrincipalID);
            
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

                double balance = responseData["balance"].AsReal();
                return balance;
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
        /// Request Gloebit transaction for the gloebit amount specified from the sender to the recipient.
        /// </summary>
        /// <param name="senderID">User object for the user sending the gloebits. <see cref="GloebitAPI.User.Get(UUID)"/></param>
        /// <param name="senderName">OpenSim Name of the user on this grid sending the gloebits.</param>
        /// <param name="recipient">User object for the user receiving the gloebits. <see cref="GloebitAPI.User.Get(UUID)"/></param>
        /// <param name="recipientName">OpenSim Name of the user on this grid receiving the gloebits.</param>
        /// <param name="recipientEmail">Email address of the user on this grid receiving the gloebits.  Empty string if user created account without email.</param>
        /// <param name="amount">quantity of gloebits to be transacted.</param>
        /// <param name="description">Description of purpose of transaction recorded in Gloebit transaction histories.</param>

        public void TransactU2U(User sender, string senderName, User recipient, string recipientName, string recipientEmail, int amount, string description) {

            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U senderID:{0} senderName:{1} recipientID:{2} recipientName:{3} recipientEmail:{4} amount:{5} description:{6}", sender.PrincipalID, senderName, recipient.PrincipalID, recipientName, recipientEmail, amount, description);
            
            // ************ IDENTIFY GLOEBIT RECIPIENT ******** //
            // TODO: How do we identify recipient?  Get email from profile from OpenSim UUID?
            // TODO: If we use emails, we may need to make sure account merging works for email/3rd party providers.
            // TODO: If we allow anyone to receive, need to ensure that gloebits received are locked down until user authenticates as merchant.
            
            // ************ BUILD AND SEND TRANSACT U2U POST REQUEST ******** //
            
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
            
            // TODO - add params describing recipient, transaction type, fees
            // U2U specific transact params
            transact_params["seller-name-on-application"] = String.Format("{0} - {1}", recipientName, recipient.PrincipalID);
            transact_params["seller-id-on-application"] = recipient.PrincipalID;
            if (recipient.GloebitID != null) {
                transact_params["seller-id-from-gloebit"] = recipient.GloebitID;
            }
            if (recipientEmail != String.Empty) {
                transact_params["seller-email-address"] = recipientEmail;
            }
            
            HttpWebRequest request = BuildGloebitRequest("transact-u2u", "POST", sender, "application/json", transact_params);
            if (request == null) {
                // ERROR
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U failed to create HttpWebRequest");
                return;
                // TODO once we return, return error value
            }

            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U about to BeginGetResponse");
            // **** Asynchronously make web request **** //
            IAsyncResult r = request.BeginGetResponse(GloebitWebResponseCallback,
			                                          new GloebitRequestState(request, 
			                        delegate(OSDMap responseDataMap) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U async response");
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U response: {0}", responseDataMap);

                //************ PARSE AND HANDLE TRANSACT-U2U RESPONSE *********//

                bool success = (bool)responseDataMap["success"];
                // TODO: if success=false: id, balance, product-count are invalid.  Do not set balance.
                double balance = responseDataMap["balance"].AsReal();
                string reason = responseDataMap["reason"];
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U success: {0} balance: {1} reason: {2}", success, balance, reason);
                // TODO - update the user's balance
                // TODO - consider updating recipient's balance (do we need to check if logged in?)
            }));
            
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
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.GloebitReadCallback");
            
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
    }
}
