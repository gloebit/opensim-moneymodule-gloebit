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
        // TODO - build this redirect_uri correctly with the correct public hostname and port
        private const string REDIRECT_URI = "http://localhost:9000/gloebit/auth_complete";

        private string m_key;
        private string m_keyAlias;
        private string m_secret;
        private Uri m_url;

        private Dictionary<string,string> m_tokenMap;

        public GloebitAPI(string key, string keyAlias, string secret, Uri url) {
            m_key = key;
            m_keyAlias = keyAlias;
            m_secret = secret;
            m_url = url;
            m_tokenMap = new Dictionary<string,string>();
        }

        public void Authorize(IClientAPI user) {
            Dictionary<string, string> auth_params = new Dictionary<string, string>();

            auth_params["client_id"] = m_key;
            auth_params["r"] = m_keyAlias;
            auth_params["scope"] = "user balance transact";
            auth_params["redirect_uri"] = String.Format("{0}?agentId={1}", REDIRECT_URI, user.AgentId);
            auth_params["response_type"] = "code";
            auth_params["user"] = user.AgentId.ToString();
            // TODO - make use of 'state' param for XSRF protection
            // auth_params["state"] = ???;

            ArrayList query_args = new ArrayList();
            foreach(KeyValuePair<string, string> p in auth_params) {
                query_args.Add(String.Format("{0}={1}", p.Key, HttpUtility.UrlEncode(p.Value)));
            }

            string query_string = String.Join("&", (string[])query_args.ToArray(typeof(string)));

            m_log.InfoFormat("GloebitAPI.Authorize query_string: {0}", query_string);

            Uri request_uri = new Uri(m_url, String.Format("oauth2/authorize?{0}", query_string));
            m_log.InfoFormat("GloebitAPI.Authorize request_uri: {0}", request_uri);
            //WebRequest request = WebRequest.Create(request_uri);
            //request.Method = "GET";

            //HttpWebResponse response = (HttpWebResponse) request.GetResponse();
            //string status = response.StatusDescription;
            //StreamReader response_stream = new StreamReader(response.GetResponseStream());
            //string response_str = response_stream.ReadToEnd();
            //m_log.InfoFormat("GloebitAPI.Authorize response: {0}", response_str);
            //response.Close();

            string message = String.Format("To use Gloebit currency, please autorize Gloebit to link to your avatar's account on this web page: {0}", request_uri);
            // GridInstantMessage im = new GridInstantMessage();
            // im.fromAgentID = Guid.Empty;
            // im.fromAgentName = "Gloebit";
            // im.toAgentID = user.AgentId.Guid;
            // im.dialog = (byte)19;  // Object message
            // im.fromGroup = false;
            // im.message = message;
            // im.imSessionID = UUID.Random().Guid;
            // im.offline = 0;
            // im.Position = Vector3.Zero;
            // im.binaryBucket = new byte[0];
            // im.ParentEstateID = 0;
            // im.RegionID = Guid.Empty;
            // im.timestamp = (uint)Util.UnixTimeSinceEpoch();
            // 
            // user.SendInstantMessage(im);
            user.SendBlueBoxMessage(UUID.Zero, "Gloebit", message);
        }

        public string ExchangeAccessToken(IClientAPI user, string auth_code) {

            Uri request_uri = new Uri(m_url, "oauth2/access-token");
            HttpWebRequest request = (HttpWebRequest) WebRequest.Create(request_uri);
            request.Method = "POST";
            //request.KeepAlive = false;

            //OSDMap auth_params = new OSDMap();
            Dictionary<string,string> auth_params = new Dictionary<string,string>();

            auth_params["client_id"] = m_key;
            auth_params["client_secret"] = m_secret;
            auth_params["code"] = auth_code;
            auth_params["grant_type"] = "authorization_code";
            auth_params["scope"] = "user balance transact";
            auth_params["redirect_uri"] = REDIRECT_URI;

            //string params_json = OSDParser.SerializeJsonString(auth_params);
            //byte[] post_data = System.Text.Encoding.UTF8.GetBytes(params_json);
            //request.ContentType = "application/json";

            StringBuilder params_str = new StringBuilder();
            foreach(KeyValuePair<string,string> p in auth_params) {
                if(params_str.Length != 0) {
                    params_str.Append('&');
                }
                params_str.AppendFormat("{0}={1}", p.Key, HttpUtility.UrlEncode(p.Value));
            }
            byte[] post_data = System.Text.Encoding.UTF8.GetBytes(params_str.ToString());
            request.ContentType = "application/x-www-form-urlencoded";

            m_log.InfoFormat("GloebitAPI.ExchangeAccessToken post_data: {0} Length:{1}", System.Text.Encoding.Default.GetString(post_data), post_data.Length);

            request.Proxy = null;
            request.ContentLength = post_data.Length;
            using (Stream s = request.GetRequestStream()) {
                s.Write(post_data, 0, post_data.Length);
            }

            HttpWebResponse response = (HttpWebResponse) request.GetResponse();
            string status = response.StatusDescription;
            using(StreamReader response_stream = new StreamReader(response.GetResponseStream())) {
                string response_str = response_stream.ReadToEnd();
                // TODO - do not actually log the token
                m_log.InfoFormat("GloebitAPI.ExchangeAccessToken response: {0}", response_str);
                OSDMap responseData = (OSDMap)OSDParser.DeserializeJson(response_str);

                string token = responseData["access_token"];
                // TODO - do something to handle the "refresh_token" field properly
                if(token != String.Empty) {
                    string agentId = user.AgentId.ToString();
                    // TODO - do not actually log the token
                    m_log.InfoFormat("GloebitAPI.ExchangeAccessToken saving token for agent: {0} as token: {1}", agentId, token);
                    lock(m_tokenMap) {
                        m_tokenMap[agentId] = token;
                    }
                    return token;
                } else {
                    m_log.ErrorFormat("GloebitAPI.ExchangeAccessToken error: {0}, reason: {1}", responseData["error"], responseData["reason"]);
                    return null;
                }
            }

        }

        public int GetBalance(UUID agentID) {
            string token;
            lock(m_tokenMap) {
                bool found = m_tokenMap.TryGetValue(agentID.ToString(), out token);
            }
            if(token == null) {
                return 0;
            }

            Uri request_uri = new Uri(m_url, "balance");
            HttpWebRequest request = (HttpWebRequest) WebRequest.Create(request_uri);
            request.Method = "GET";
            request.Headers.Add("Authorization", String.Format("Bearer {0}", token));

            HttpWebResponse response = (HttpWebResponse) request.GetResponse();
            string status = response.StatusDescription;
            using(StreamReader response_stream = new StreamReader(response.GetResponseStream())) {
                string response_str = response_stream.ReadToEnd();

                OSDMap responseData = (OSDMap)OSDParser.DeserializeJson(response_str);

                int balance = int.Parse(responseData["balance"]);
                m_log.InfoFormat("GloebitAPI.ExchangeAccessToken balance: {0}", balance);
                return balance;
            }

        }
    }

}
