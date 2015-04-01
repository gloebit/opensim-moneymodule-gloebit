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
using System.Web;
using log4net;
using OpenMetaverse;

using OpenSim.Framework;

namespace Gloebit.GloebitMoneyModule {

    public class GloebitAPI {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private string m_key;
        private string m_keyAlias;
        private string m_secret;
        private Uri m_url;

        public GloebitAPI(string key, string keyAlias, string secret, Uri url) {
            m_key = key;
            m_keyAlias = keyAlias;
            m_secret = secret;
            m_url = url;
        }

        public void Authorize(IClientAPI user) {
            Dictionary<string, string> auth_params = new Dictionary<string, string>();

            auth_params["client_id"] = m_key;
            auth_params["r"] = m_keyAlias;
            auth_params["scope"] = "user balance transact";
            // TODO - build this redirect_uri correctly with the correct public hostname and port
            auth_params["redirect_uri"] = "http://localhost:9000/gloebit/auth_complete";
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

            GridInstantMessage im = new GridInstantMessage();
            im.fromAgentID = Guid.Empty;
            im.fromAgentName = "Gloebit";
            im.toAgentID = user.AgentId.Guid;
            im.dialog = (byte)19;  // Object message
            im.fromGroup = false;
            im.message = String.Format("To use Gloebit currency, please autorize Gloebit to link to your avatar's account on this web page: {0}", request_uri);
            im.imSessionID = UUID.Random().Guid;
            im.offline = 0;
            im.Position = Vector3.Zero;
            im.binaryBucket = new byte[0];
            im.ParentEstateID = 0;
            im.RegionID = Guid.Empty;
            im.timestamp = (uint)Util.UnixTimeSinceEpoch();
            
            user.SendInstantMessage(im);
        }
    }

}
