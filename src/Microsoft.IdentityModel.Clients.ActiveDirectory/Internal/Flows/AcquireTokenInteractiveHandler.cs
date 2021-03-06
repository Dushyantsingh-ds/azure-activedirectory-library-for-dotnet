﻿//----------------------------------------------------------------------
//
// Copyright (c) Microsoft Corporation.
// All rights reserved.
//
// This code is licensed under the MIT License.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files(the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and / or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions :
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
//------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Identity.Core;
using Microsoft.Identity.Core.UI;
using Microsoft.Identity.Core.Cache;
using Microsoft.IdentityModel.Clients.ActiveDirectory.Internal.Helpers;
using Microsoft.IdentityModel.Clients.ActiveDirectory.Internal.OAuth2;
using Microsoft.IdentityModel.Clients.ActiveDirectory.Internal.Platform;
using Microsoft.Identity.Core.Http;
using Microsoft.Identity.Core.OAuth2;
using System.Threading;

namespace Microsoft.IdentityModel.Clients.ActiveDirectory.Internal.Flows
{
    internal class AcquireTokenInteractiveHandler : AcquireTokenHandlerBase
    {
        internal AuthorizationResult authorizationResult;
        private readonly Uri redirectUri;
        private readonly string redirectUriRequestParameter;
        private readonly IPlatformParameters authorizationParameters;
        private readonly string extraQueryParameters;
        private readonly IWebUI webUi;
        private readonly UserIdentifier userId;
        private readonly string claims;

        private string state;
        private string codeVerifier;

        public AcquireTokenInteractiveHandler(
            IServiceBundle serviceBundle,
            RequestData requestData,
            Uri redirectUri,
            IPlatformParameters platformParameters,
            SynchronizationContext synchronizationContext,
            UserIdentifier userId,
            string extraQueryParameters,
            string claims)
            : base(serviceBundle, requestData)
        {
            this.redirectUri = ComputeAndValidateRedirectUri(redirectUri, this.ClientKey?.ClientId);
            this.redirectUriRequestParameter = PlatformProxyFactory.GetPlatformProxy().GetBrokerOrRedirectUri(this.redirectUri);

            this.authorizationParameters = platformParameters;
            this.userId = userId ?? throw new ArgumentNullException(nameof(userId), AdalErrorMessage.SpecifyAnyUser);

            if (!string.IsNullOrEmpty(extraQueryParameters) && extraQueryParameters[0] == '&')
            {
                extraQueryParameters = extraQueryParameters.Substring(1);
            }

            this.extraQueryParameters = extraQueryParameters;
            this.webUi = CreateWebUIOrNull(platformParameters, synchronizationContext);
            this.UniqueId = userId.UniqueId;
            this.DisplayableId = userId.DisplayableId;
            this.UserIdentifierType = userId.Type;
            this.SupportADFS = true;

            if (!string.IsNullOrEmpty(claims))
            {
                LoadFromCache = false;
                RequestContext.Logger.Verbose("Claims present. Skip cache lookup.");
                this.claims = claims;
            }
            else
            {
                var platformInformation = new PlatformInformation();
                LoadFromCache = (requestData.TokenCache != null && platformParameters != null && platformInformation.GetCacheLoadPolicy(platformParameters));
            }

            BrokerParameters[BrokerParameter.Force] = "NO";
            if (userId != UserIdentifier.AnyUser)
            {
                BrokerParameters[BrokerParameter.Username] = userId.Id;
            }
            else
            {
                BrokerParameters[BrokerParameter.Username] = string.Empty;
            }
            BrokerParameters[BrokerParameter.UsernameType] = userId.Type.ToString();

            BrokerParameters[BrokerParameter.RedirectUri] = this.redirectUri.AbsoluteUri;
            BrokerParameters[BrokerParameter.ExtraQp] = extraQueryParameters;
            BrokerParameters[BrokerParameter.Claims] = claims;
            BrokerHelper.PlatformParameters = authorizationParameters;
        }

        private IWebUI CreateWebUIOrNull(IPlatformParameters parameters, SynchronizationContext synchronizationContext)
        {
            if (parameters == null)
            {
                return null;
            }

            if (!(parameters is PlatformParameters parametersObj))
            {
                throw new ArgumentException("Objects implementing IPlatformParameters should be of type PlatformParameters");
            }

            var coreUIParent = parametersObj.GetCoreUIParent();
            coreUIParent.SynchronizationContext = synchronizationContext;

            return WebUIFactoryProvider.WebUIFactory.CreateAuthenticationDialog(
                coreUIParent,
                RequestContext);
        }

        private static Uri ComputeAndValidateRedirectUri(Uri redirectUri, string clientId)
        {
            // ADAL mostly does not provide defaults for the redirect URI, currently only for UWP for broker support
            if (redirectUri == null)
            {
                string defaultUriAsString = PlatformProxyFactory.GetPlatformProxy().GetDefaultRedirectUri(clientId);

                if (!String.IsNullOrWhiteSpace(defaultUriAsString))
                {
                    return new Uri(defaultUriAsString);
                }
            }

            RedirectUriHelper.Validate(redirectUri);

            return redirectUri;
        }

        private static string ReplaceHost(string original, string newHost)
        {
            return new UriBuilder(original) { Host = newHost }.Uri.ToString();
        }

        protected internal /* internal for test only */ override async Task PreTokenRequestAsync()
        {
            await base.PreTokenRequestAsync().ConfigureAwait(false);

            // We do not have async interactive API in .NET, so we call this synchronous method instead.
            await this.AcquireAuthorizationAsync().ConfigureAwait(false);
            this.VerifyAuthorizationResult();

            if (!string.IsNullOrEmpty(authorizationResult.CloudInstanceHost))
            {
                var updatedAuthority = ReplaceHost(Authenticator.Authority, authorizationResult.CloudInstanceHost);

                await UpdateAuthorityAsync(updatedAuthority).ConfigureAwait(false);
            }
        }

        internal async Task AcquireAuthorizationAsync()
        {
            Uri authorizationUri = this.CreateAuthorizationUri(true);
            this.authorizationResult = await this.webUi.AcquireAuthorizationAsync(
                authorizationUri, 
                this.redirectUri, RequestContext).ConfigureAwait(false);
        }

        internal async Task<Uri> CreateAuthorizationUriAsync(Guid correlationId)
        {
            this.RequestContext.Logger.CorrelationId = correlationId;
            await this.Authenticator.UpdateFromTemplateAsync(RequestContext).ConfigureAwait(false);
            return this.CreateAuthorizationUri(false);
        }

        protected override void AddAdditionalRequestParameters(DictionaryRequestParameters requestParameters)
        {
            requestParameters[OAuthParameter.GrantType] = OAuthGrantType.AuthorizationCode;
            requestParameters[OAuthParameter.Code] = this.authorizationResult.Code;
            requestParameters[OAuthParameter.RedirectUri] = this.redirectUriRequestParameter;
            requestParameters[OAuthParameter.CodeVerifier] = this.codeVerifier;
        }

        protected override async Task PostTokenRequestAsync(AdalResultWrapper resultEx)
        {
            await base.PostTokenRequestAsync(resultEx).ConfigureAwait(false);
            if ((this.DisplayableId == null && this.UniqueId == null) || this.UserIdentifierType == UserIdentifierType.OptionalDisplayableId)
            {
                return;
            }

            string uniqueId = (resultEx.Result.UserInfo != null && resultEx.Result.UserInfo.UniqueId != null) ? resultEx.Result.UserInfo.UniqueId : "NULL";
            string displayableId = (resultEx.Result.UserInfo != null) ? resultEx.Result.UserInfo.DisplayableId : "NULL";

            if (this.UserIdentifierType == UserIdentifierType.UniqueId && string.Compare(uniqueId, this.UniqueId, StringComparison.Ordinal) != 0)
            {
                throw new AdalUserMismatchException(this.UniqueId, uniqueId);
            }

            if (this.UserIdentifierType == UserIdentifierType.RequiredDisplayableId && string.Compare(displayableId, this.DisplayableId, StringComparison.OrdinalIgnoreCase) != 0)
            {
                throw new AdalUserMismatchException(this.DisplayableId, displayableId);
            }
        }

        private Uri CreateAuthorizationUri(bool addPkce)
        {
            string loginHint = null;

            if (!userId.IsAnyUser
                && (userId.Type == UserIdentifierType.OptionalDisplayableId
                    || userId.Type == UserIdentifierType.RequiredDisplayableId))
            {
                loginHint = userId.Id;
            }

            IRequestParameters requestParameters = this.CreateAuthorizationRequest(loginHint, addPkce);

            return new Uri(new Uri(this.Authenticator.AuthorizationUri), "?" + requestParameters);
        }

        
        private DictionaryRequestParameters CreateAuthorizationRequest(string loginHint, bool addPkceAndState)
        {
            var authorizationRequestParameters = new DictionaryRequestParameters(this.Resource, this.ClientKey);
            authorizationRequestParameters[OAuthParameter.ResponseType] = OAuthResponseType.Code;
            authorizationRequestParameters[OAuthParameter.HasChrome] = "1";
            authorizationRequestParameters[OAuthParameter.RedirectUri] = this.redirectUriRequestParameter;

            // PKCE and State should be used for interactive auth, but not when creating an Authorization uri
            if (addPkceAndState)
            {
                AddPKCEAndState(authorizationRequestParameters);
            }

#if DESKTOP
            // Added form_post as a way to request to ensure we can handle large requests for dsts scenarios
            authorizationRequestParameters[OAuthParameter.ResponseMode] = OAuthResponseMode.FormPost;
#endif

            if (!string.IsNullOrWhiteSpace(loginHint))
            {
                authorizationRequestParameters[OAuthParameter.LoginHint] = loginHint;
            }

            if (!string.IsNullOrWhiteSpace(claims))
            {
                authorizationRequestParameters["claims"] = claims;
            }

            if (this.RequestContext != null && this.RequestContext.Logger.CorrelationId != Guid.Empty)
            {
                authorizationRequestParameters[OAuthParameter.CorrelationId] = this.RequestContext.Logger.CorrelationId.ToString();
            }

            if (this.authorizationParameters != null)
            {
                var platformInformation = new PlatformInformation();
                platformInformation.AddPromptBehaviorQueryParameter(this.authorizationParameters, authorizationRequestParameters);
            }

            IDictionary<string, string> adalIdParameters = AdalIdHelper.GetAdalIdParameters();
            foreach (KeyValuePair<string, string> kvp in adalIdParameters)
            {
                authorizationRequestParameters[kvp.Key] = kvp.Value;
            }


            if (!string.IsNullOrWhiteSpace(extraQueryParameters))
            {
                // Checks for extraQueryParameters duplicating standard parameters
                Dictionary<string, string> kvps = EncodingHelper.ParseKeyValueList(extraQueryParameters, '&', false, RequestContext);
                foreach (KeyValuePair<string, string> kvp in kvps)
                {
                    if (authorizationRequestParameters.ContainsKey(kvp.Key))
                    {
                        throw new AdalException(AdalError.DuplicateQueryParameter, string.Format(CultureInfo.CurrentCulture, AdalErrorMessage.DuplicateQueryParameterTemplate, kvp.Key));
                    }
                }

                authorizationRequestParameters.ExtraQueryParameter = extraQueryParameters;
            }

            return authorizationRequestParameters;
        }

        private void AddPKCEAndState(DictionaryRequestParameters authorizationRequestParameters)
        {
            codeVerifier = PlatformProxyFactory.GetPlatformProxy().CryptographyManager.GenerateCodeVerifier();
            string codeVerifierHash = PlatformProxyFactory.GetPlatformProxy().CryptographyManager.CreateBase64UrlEncodedSha256Hash(codeVerifier);
            authorizationRequestParameters[OAuthParameter.CodeChallenge] = codeVerifierHash;
            authorizationRequestParameters[OAuthParameter.CodeChallengeMethod] = OAuthValue.CodeChallengeMethodValue;

            state = Guid.NewGuid().ToString() + Guid.NewGuid().ToString();
            authorizationRequestParameters[OAuthParameter.State] = state;
        }

        private void VerifyAuthorizationResult()
        {
            if (this.authorizationResult.Status == AuthorizationStatus.Success &&
              !this.state.Equals(this.authorizationResult.State,
                  StringComparison.OrdinalIgnoreCase))
            {
                throw new AdalException(
                    AdalError.StateMismatchError,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Returned state({0}) from authorize endpoint is not the same as the one sent({1})",
                        authorizationResult.State,
                        state));
            }

            if (this.authorizationResult.Error == OAuthError.LoginRequired)
            {
                throw new AdalException(AdalError.UserInteractionRequired);
            }

            if (this.authorizationResult.Status != AuthorizationStatus.Success)
            {
                throw new AdalServiceException(this.authorizationResult.Error, this.authorizationResult.ErrorDescription);
            }
        }

        protected override void UpdateBrokerParameters(IDictionary<string, string> parameters)
        {
            Uri uri = new Uri(this.authorizationResult.Code);
            string query = EncodingHelper.UrlDecode(uri.Query);
            Dictionary<string, string> kvps = EncodingHelper.ParseKeyValueList(query, '&', false, RequestContext);
            parameters["username"] = kvps["username"];
        }

        protected override bool BrokerInvocationRequired()
        {
            if (this.authorizationResult != null
                && !string.IsNullOrEmpty(this.authorizationResult.Code)
                && this.authorizationResult.Code.StartsWith("msauth://", StringComparison.OrdinalIgnoreCase))
            {
                this.BrokerParameters[BrokerParameter.BrokerInstallUrl] = this.authorizationResult.Code;
                return true;
            }

            return false;
        }
    }
}