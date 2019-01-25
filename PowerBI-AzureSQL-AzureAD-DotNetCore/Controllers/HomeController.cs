using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.PowerBI.Api.V2;
using Microsoft.PowerBI.Api.V2.Models;
using Microsoft.Rest;
using Newtonsoft.Json.Linq;
using PowerBI_AzureSQL_AzureAD_DotNetCore.Models;

namespace PowerBI_AzureSQL_AzureAD_DotNetCore.Controllers
{
    [Authorize]
    public class HomeController : Controller
    {
        private static SemaphoreSlim _lock;
        private static string _pbiAccessToken;
        private static DateTime _pbiAccessTokenExpiry;
        private Models.ConfigurationSettings _configSettings;

        static HomeController()
        {
            _lock = new SemaphoreSlim(1, 1);
            _pbiAccessToken = null;
            _pbiAccessTokenExpiry = DateTime.Now;
        }
        public HomeController(Models.ConfigurationSettings configSettings)
        {
            _configSettings = configSettings;
        }
        public async Task<IActionResult> Index()
        {
            var result = new EmbedConfig();
            try
            {
                //get access token for Power BI Super User
                var pbiAccessToken = await GetPbiPowerUserAccessToken() 
                    ?? throw new ApplicationException("Could not acquire Super User Access Token");

                var tokenCredentials = new TokenCredentials(pbiAccessToken, "Bearer");

                // Create a Power BI Client object. It will be used to call Power BI APIs.
                using (var client = new PowerBIClient(new Uri(_configSettings.ApiUrl), tokenCredentials))
                {
                    // Safety code in case there are no reports in the workspace.
                    var reports = await client.Reports.GetReportsInGroupAsync(_configSettings.WorkspaceId);
                    if (reports.Value.Count() == 0)
                        throw new ApplicationException("No reports were found in the workspace");

                    //Safety code in case there is no report id provided or the report id is invalid.
                    Report report = string.IsNullOrWhiteSpace(_configSettings.ReportId) ?
                                reports.Value.FirstOrDefault() :
                                reports.Value.FirstOrDefault(r => r.Id == _configSettings.ReportId)
                                ?? throw new ApplicationException("No report with the given ID was found in the workspace. Make sure ReportId is valid.");                      

                    var datasets = new List<string> { report.DatasetId };

                    //Exchange the aad token for a token with access to sql.
                    var sqlAccessToken = await GetSqlAccessToken();
                    IdentityBlob token = new IdentityBlob(sqlAccessToken);

                    //pass the sql auth token in as the effective identity when retreiving the embed token.
                    var effectiveIdentity = new EffectiveIdentity(username: null, datasets: datasets, identityBlob: token);
                    GenerateTokenRequest generateTokenRequestParameters = new GenerateTokenRequest(accessLevel: "view", identity: effectiveIdentity);
                    var tokenResponse = await client.Reports.GenerateTokenInGroupAsync(_configSettings.WorkspaceId, report.Id, generateTokenRequestParameters);

                    // Generate Embed Configuration.
                    result.EmbedToken = tokenResponse ?? throw new ApplicationException("Failed to generate embed token.");
                    result.EmbedUrl = report.EmbedUrl;
                    result.Id = report.Id;
                }
            }
            catch(Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }
            return View(result);
        }

        [AllowAnonymous]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }

        internal async Task<string> GetPbiPowerUserAccessToken()
        {
            await _lock.WaitAsync();
            try
            {
                if (string.IsNullOrEmpty(_pbiAccessToken) || DateTime.Now > _pbiAccessTokenExpiry)
                {
                    using (HttpClient httpClient = new HttpClient())
                    {
                        var endpoint = $@"{_configSettings.AADInstance}{_configSettings.AADTenantId}/oauth2/token";

                        httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

                        var requestBody = $"resource={HttpUtility.UrlEncode(_configSettings.PbiResourceUrl)}" +
                                          $"&client_id={_configSettings.PbiApplicationId}" +
                                          $"&grant_type=password" +
                                          $"&username={_configSettings.PbiUsername}" +
                                          $"&password={_configSettings.PbiPassword}" +
                                          $"&scope=openid";

                        using (var response = await httpClient.PostAsync(
                            endpoint,
                            new StringContent(requestBody, Encoding.UTF8, "application/x-www-form-urlencoded")
                            ))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                var result = JObject.Parse(await response.Content.ReadAsStringAsync());
                                _pbiAccessToken = result.Value<string>("access_token");
                                //store token expiration to acquire a new one before the current token expires
                                _pbiAccessTokenExpiry = DateTime.Now.AddSeconds(result.Value<double>("expires_in") - 120);
                            }
                        }
                    }
                }
                return _pbiAccessToken;
            }
            finally
            {
                _lock.Release();
            }
            
        }
        internal async Task<string> GetSqlAccessToken()
        {
            string token = null;

            AuthenticationContext authContext = new AuthenticationContext($"{_configSettings.AADInstance}{_configSettings.AADTenantId}");
            ClientCredential clientCred = new ClientCredential(_configSettings.ClientId, _configSettings.ClientSecret);

            ClaimsPrincipal current = HttpContext.User; 
            var userAccessToken = current.Identities.First().BootstrapContext as string;

            string userName = current.FindFirst(ClaimTypes.Upn) != null
                ? current.FindFirst(ClaimTypes.Upn).Value
                : current.FindFirst(ClaimTypes.Email).Value;

            UserAssertion userAssertion = new UserAssertion(userAccessToken,
                "urn:ietf:params:oauth:grant-type:jwt-bearer",
                userName);

            AuthenticationResult result = null;
            try
            {
                //try to get the token from local cache first, else request a token from the endpoint
                result = await authContext.AcquireTokenSilentAsync(_configSettings.SqlResourceUrl, 
                    _configSettings.ClientId, 
                    new UserIdentifier(userName, UserIdentifierType.RequiredDisplayableId));
            }
            catch (AdalException adalException)
            {
                if (adalException.ErrorCode == AdalError.FailedToAcquireTokenSilently || adalException.ErrorCode == AdalError.UserInteractionRequired)
                {
                    result = await authContext.AcquireTokenAsync(_configSettings.SqlResourceUrl,
                        clientCred, 
                        userAssertion);
                }
            }

            if (result != null)
                token = result.AccessToken;

            return token;
        }
    }
}
