using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.PowerBI.Api.V2;
using Microsoft.PowerBI.Api.V2.Models;
using Microsoft.Rest;
using WebApp_OpenIDConnect_DotNet.Models;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Mvc;
using System.Security.Claims;

namespace WebApp_OpenIDConnect_DotNet.Controllers
{
   

    public class HomeController : Controller
    {
        private static readonly string Username = ConfigurationManager.AppSettings["pbiUsername"];
        private static readonly string Password = ConfigurationManager.AppSettings["pbiPassword"];
        private static readonly string AuthorityUrl = ConfigurationManager.AppSettings["authorityUrl"];
        private static readonly string pbiResourceUrl = ConfigurationManager.AppSettings["pbiResourceUrl"];
        private static readonly string pbiApplicationId = ConfigurationManager.AppSettings["pbiApplicationId"];
        private static readonly string ApiUrl = ConfigurationManager.AppSettings["apiUrl"];
        private static readonly string WorkspaceId = ConfigurationManager.AppSettings["workspaceId"];
        private static readonly string ReportId = ConfigurationManager.AppSettings["reportId"];

        private static readonly string sqlResourceUrl = ConfigurationManager.AppSettings["sqlResourceUrl"];
        private static readonly string sqlApplicationId = ConfigurationManager.AppSettings["sqlApplicationId"];
        private static readonly string clientSecret = ConfigurationManager.AppSettings["clientSecret"];

        public ActionResult Index()
        {
            return View();
        }

        public ActionResult About()
        {
            ViewBag.Message = "Your application description page.";

            return View();
        }

        public ActionResult Contact()
        {
            ViewBag.Message = "Your contact page.";

            return View();
        }

        public async Task<ActionResult> EmbedReport(string username, string roles)
         {
            var result = new EmbedConfig();
            try
            {
                result = new EmbedConfig { Username = username, Roles = roles };
                var error = GetWebConfigErrors();
                if (error != null)
                {
                    result.ErrorMessage = error;
                    return View(result);
                }

                // Create a user password cradentials.
                var pbiCredential = new UserPasswordCredential(Username, Password);

                // Authenticate using created credentials
                var authenticationContext = new AuthenticationContext(AuthorityUrl);
                var pbiAuthenticationResult = await authenticationContext.AcquireTokenAsync(pbiResourceUrl, pbiApplicationId, pbiCredential);

                
                if (pbiAuthenticationResult == null)
                {
                    result.ErrorMessage = "Authentication Failed.";
                    return View(result);
                }

                var tokenCredentials = new TokenCredentials(pbiAuthenticationResult.AccessToken, "Bearer");

                // Create a Power BI Client object. It will be used to call Power BI APIs.
                using (var client = new PowerBIClient(new Uri(ApiUrl), tokenCredentials))
                {
                    // Safety code in case there are no reports in the workspace.
                    var reports = await client.Reports.GetReportsInGroupAsync(WorkspaceId);
                    if (reports.Value.Count() == 0)
                    {
                        result.ErrorMessage = "No reports were found in the workspace";
                        return View(result);
                    }

                    //Safety code in case there is no report id provided or the report id is invalid.
                    Report report;
                    if (string.IsNullOrWhiteSpace(ReportId))
                    {
                        // Get the first report in the workspace.
                        report = reports.Value.FirstOrDefault();
                    }
                    else
                    {
                        report = reports.Value.FirstOrDefault(r => r.Id == ReportId);
                    }

                    if (report == null)
                    {
                        result.ErrorMessage = "No report with the given ID was found in the workspace. Make sure ReportId is valid.";
                        return View(result);
                    }

                    var datasets = new List<string> { report.DatasetId };

                    //Exchange the aad token for a token with access to sql.
                    IdentityBlob token = new IdentityBlob(GetSqlAccessToken().Result);

                    //pass the sql auth token in as the effective identity when retreiving the embed token.
                    var ee = new EffectiveIdentity(username:null, datasets:datasets, identityBlob:token);
                    GenerateTokenRequest generateTokenRequestParameters = new GenerateTokenRequest(accessLevel: "view", identity: ee);
                    var tokenResponse = await client.Reports.GenerateTokenInGroupAsync(WorkspaceId, report.Id, generateTokenRequestParameters);

                    if (tokenResponse == null)
                    {
                        result.ErrorMessage = "Failed to generate embed token.";
                        return View(result);
                    }

                    // Generate Embed Configuration.
                    result.EmbedToken = tokenResponse;
                    result.EmbedUrl = report.EmbedUrl;
                    result.Id = report.Id;

                    return View(result);
                }
            }
            catch (HttpOperationException exc)
            {
                result.ErrorMessage = string.Format("Status: {0} ({1})\r\nResponse: {2}\r\nRequestId: {3}", exc.Response.StatusCode, (int)exc.Response.StatusCode, exc.Response.Content, exc.Response.Headers["RequestId"].FirstOrDefault());
            }
            catch (Exception exc)
            {
                result.ErrorMessage = exc.ToString();
            }

            return View(result);
        }

        /// <summary>
        /// Check if web.config embed parameters have valid values.
        /// </summary>
        /// <returns>Null if web.config parameters are valid, otherwise returns specific error string.</returns>
        private string GetWebConfigErrors()
        {
            /*
            // Application Id must have a value.
            if (string.IsNullOrWhiteSpace(ApplicationId))
            {
                return "ApplicationId is empty. please register your application as Native app in https://dev.powerbi.com/apps and fill client Id in web.config.";
            }

            // Application Id must be a Guid object.
            Guid result;
            if (!Guid.TryParse(ApplicationId, out result))
            {
                return "ApplicationId must be a Guid object. please register your application as Native app in https://dev.powerbi.com/apps and fill application Id in web.config.";
            }
            
            // Workspace Id must have a value.
            if (string.IsNullOrWhiteSpace(WorkspaceId))
            {
                return "WorkspaceId is empty. Please select a group you own and fill its Id in web.config";
            }

            // Workspace Id must be a Guid object.
            if (!Guid.TryParse(WorkspaceId, out result))
            {
                return "WorkspaceId must be a Guid object. Please select a workspace you own and fill its Id in web.config";
            }

            // Username must have a value.
            if (string.IsNullOrWhiteSpace(Username))
            {
                return "Username is empty. Please fill Power BI username in web.config";
            }

            // Password must have a value.
            if (string.IsNullOrWhiteSpace(Password))
            {
                return "Password is empty. Please fill password of Power BI username in web.config";
            }
            */
            return null;
        }

        public async Task<string> GetSqlAccessToken()
        {
            string token = null;
            if (Request.IsAuthenticated)
            {
                //string ResourceId = "https://database.windows.net/";
                AuthenticationResult result = null;
                AuthenticationContext authContext = new AuthenticationContext(AuthorityUrl);
                ClientCredential clientCred = new ClientCredential(
                    ConfigurationManager.AppSettings["ida:ClientId"],
                    ConfigurationManager.AppSettings["ida:ClientSecret"]);
                ClaimsPrincipal current = ClaimsPrincipal.Current;
                var bootstrapContext = current.Identities.First().BootstrapContext as string;
                                                          //as System.IdentityModel.Tokens.BootstrapContext;
                string userName = current.FindFirst(ClaimTypes.Upn) != null
                    ? current.FindFirst(ClaimTypes.Upn).Value
                    : current.FindFirst(ClaimTypes.Email).Value;
                string userAccessToken = bootstrapContext;//bootstrapContext.Token;
                UserAssertion userAssertion = new UserAssertion(userAccessToken,
                                                             "urn:ietf:params:oauth:grant-type:jwt-bearer",
                                                                userName);

                try
                {
                    //make sure we're extracting the token from cache if we already have one.
                    result = await authContext.AcquireTokenSilentAsync(sqlResourceUrl, sqlApplicationId, new UserIdentifier(userName, UserIdentifierType.RequiredDisplayableId));
                }
                catch (AdalException adalException)
                {
                    if (adalException.ErrorCode == AdalError.FailedToAcquireTokenSilently || adalException.ErrorCode == AdalError.UserInteractionRequired)
                    {
                        result = authContext.AcquireTokenAsync(sqlResourceUrl,
                                                                     clientCred,
                                                                     userAssertion).Result;

                    }
                }
                //might want to do some kind of error handling if no token could be acquired.
                if (result != null)
                    token = result.AccessToken;
            }
            return token;
        }

    }

}