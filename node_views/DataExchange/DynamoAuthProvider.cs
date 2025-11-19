using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Autodesk.DataExchange.Core.Interface;
using Autodesk.DataExchange.Core.Models;
using Dynamo.Core;
using Dynamo.ViewModels;
using Greg.AuthProviders;
using Newtonsoft.Json.Linq;

namespace DataExchangeNodes.NodeViews.DataExchange
{
    /// <summary>
    /// Authentication provider for DataExchange SDK using Dynamo's AuthenticationManager
    /// </summary>
    public class DynamoAuthProvider : IAuth
    {
        private readonly DynamoViewModel _dynamoViewModel;
        private UserAccount _cachedUserAccount;

        public DynamoAuthProvider(DynamoViewModel dynamoViewModel)
        {
            _dynamoViewModel = dynamoViewModel ?? throw new ArgumentNullException(nameof(dynamoViewModel));
        }

        /// <summary>
        /// Gets the authentication token from Dynamo's AuthenticationManager
        /// </summary>
        public string GetToken()
        {
            try
            {
                var authProvider = _dynamoViewModel?.Model?.AuthenticationManager?.AuthProvider;
                if (authProvider == null)
                    return null;

                // Check if user is logged out
                if (authProvider.LoginState == LoginState.LoggedOut)
                    return null;

                // Get the type name to determine which auth provider we're using
                string typeName = authProvider.GetType().Name.ToLower();

                if (typeName.Contains("idsdkmanager"))
                {
                    // Use reflection to call internal IDSDK_GetToken method for IDSDKManager
                    try
                    {
                        var tokenObj = authProvider.GetType().InvokeMember(
                            "IDSDK_GetToken",
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.InvokeMethod,
                            null,
                            authProvider,
                            Array.Empty<object>());

                        if (tokenObj is string token && !string.IsNullOrEmpty(token))
                            return token;
                    }
                    catch
                    {
                        // Fall through to other methods
                    }
                }
                else if (typeName.Contains("revitoauth2provider"))
                {
                    // Handle Revit authentication
                    try
                    {
                        var revServices = authProvider.GetType()
                            .GetField("_revitAuthServices", BindingFlags.Instance | BindingFlags.NonPublic)?
                            .GetValue(authProvider);

                        if (revServices != null)
                        {
                            var accessTokenObj = revServices.GetType()
                                .GetProperty("AccessToken", BindingFlags.Instance | BindingFlags.NonPublic)?
                                .GetValue(revServices);

                            if (accessTokenObj is string accessToken && !string.IsNullOrEmpty(accessToken))
                                return accessToken;
                        }
                    }
                    catch
                    {
                        // Fall through
                    }
                }
            }
            catch (Exception ex)
            {
                _dynamoViewModel?.Model?.Logger?.Log($"DynamoAuthProvider: Failed to get token - {ex.Message}");
            }

            return null;
        }

        public Task<string> GetAuthTokenAsync()
        {
            return Task.FromResult(GetToken());
        }

        public string GetAuthToken(bool isForceRefresh = false)
        {
            return GetToken();
        }

        public async Task<UserAccount> GetUserAccountAsync()
        {
            if (_cachedUserAccount != null)
                return _cachedUserAccount;

            string token = GetToken();
            if (string.IsNullOrEmpty(token))
                return null;

            string userInfoUri = "https://developer.api.autodesk.com/userprofile/v1/users/@me";

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(userInfoUri);
            request.Method = "GET";
            request.Headers.Add("Authorization", "Bearer " + token);

            try
            {
                using (WebResponse response = await request.GetResponseAsync())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    string responseText = await reader.ReadToEndAsync();
                    JObject userInfo = JObject.Parse(responseText);

                    _cachedUserAccount = new UserAccount
                    {
                        FirstName = userInfo["firstName"]?.ToString(),
                        LastName = userInfo["lastName"]?.ToString(),
                        UserId = userInfo["userId"]?.ToString()
                    };
                }
            }
            catch (Exception ex)
            {
                _dynamoViewModel?.Model?.Logger?.Log($"DynamoAuthProvider: Failed to get user account - {ex.Message}");
                return null;
            }

            return _cachedUserAccount;
        }

        /// <summary>
        /// Check if the current user is authenticated
        /// </summary>
        public bool IsAuthenticated()
        {
            var authProvider = _dynamoViewModel?.Model?.AuthenticationManager?.AuthProvider;
            return authProvider != null && authProvider.LoginState != LoginState.LoggedOut;
        }
    }
}

