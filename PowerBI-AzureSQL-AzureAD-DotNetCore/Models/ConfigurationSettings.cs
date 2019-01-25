namespace PowerBI_AzureSQL_AzureAD_DotNetCore.Models
{
    public class ConfigurationSettings
    {
        public string AADInstance { get; set; } = "https://login.microsoftonline.com/";
        public string SqlResourceUrl { get; set; } = "https://database.windows.net/";
        public string PbiResourceUrl { get; set; } = "https://analysis.windows.net/powerbi/api";
        public string ApiUrl { get; set; } = "https://api.powerbi.com/";
        public string EmbedUrlBase { get; set; } = "https://app.powerbi.com/";
        public string ClientId { get; set; }
        public string ClientSecret { get; set; }
        public string AADTenantId { get; set; }
        public string PbiApplicationId { get; set; }
        public string WorkspaceId { get; set; }
        public string ReportId { get; set; }
        public string PbiUsername { get; set; }
        public string PbiPassword { get; set; }
    }
}
