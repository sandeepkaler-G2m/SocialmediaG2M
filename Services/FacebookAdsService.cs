using System.Text.Json;

namespace SocialMediaPanel.Services
{
    /// <summary>
    /// Facebook Marketing API — ad accounts, campaigns, insights, and a
    /// simplified "boost an existing post" flow. Requires the ads_management
    /// permission (see MetaScopes.Full).
    ///
    /// No local audit table: results are read straight from the Graph API on
    /// every call rather than cached in the DB (this app's live MySQL schema
    /// has no EF migrations, so new tables are avoided — see project notes).
    /// </summary>
    public class FacebookAdsService
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public FacebookAdsService(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public async Task<List<AdAccountInfo>> GetAdAccountsAsync(string userToken)
        {
            var result = new List<AdAccountInfo>();
            var client = _httpClientFactory.CreateClient();

            var url = "https://graph.facebook.com/v19.0/me/adaccounts" +
                      "?fields=id,name,account_status,currency" +
                      $"&access_token={Uri.EscapeDataString(userToken)}";

            var body = await client.GetStringAsync(url);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var item in data.EnumerateArray())
                {
                    result.Add(new AdAccountInfo
                    {
                        Id = item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        AccountStatus = item.TryGetProperty("account_status", out var s) ? s.GetInt32() : 0,
                        Currency = item.TryGetProperty("currency", out var c) ? c.GetString() ?? "" : ""
                    });
                }
            }
            return result;
        }

        public async Task<List<CampaignInfo>> GetCampaignsAsync(string adAccountId, string userToken)
        {
            var result = new List<CampaignInfo>();
            var client = _httpClientFactory.CreateClient();

            var url = $"https://graph.facebook.com/v19.0/{adAccountId}/campaigns" +
                      "?fields=id,name,status,objective" +
                      $"&access_token={Uri.EscapeDataString(userToken)}";

            var body = await client.GetStringAsync(url);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var item in data.EnumerateArray())
                {
                    result.Add(new CampaignInfo
                    {
                        Id = item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        Status = item.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "",
                        Objective = item.TryGetProperty("objective", out var o) ? o.GetString() ?? "" : ""
                    });
                }
            }
            return result;
        }

        public async Task<CampaignInsight> GetCampaignInsightsAsync(string campaignId, string userToken)
        {
            var result = new CampaignInsight();
            var client = _httpClientFactory.CreateClient();

            var url = $"https://graph.facebook.com/v19.0/{campaignId}/insights" +
                      "?fields=impressions,reach,spend,clicks,ctr" +
                      $"&access_token={Uri.EscapeDataString(userToken)}";

            try
            {
                var body = await client.GetStringAsync(url);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
                {
                    var row = data[0];
                    result.Impressions = row.TryGetProperty("impressions", out var i) ? int.Parse(i.GetString() ?? "0") : 0;
                    result.Reach = row.TryGetProperty("reach", out var r) ? int.Parse(r.GetString() ?? "0") : 0;
                    result.Spend = row.TryGetProperty("spend", out var sp) ? decimal.Parse(sp.GetString() ?? "0") : 0;
                    result.Clicks = row.TryGetProperty("clicks", out var cl) ? int.Parse(cl.GetString() ?? "0") : 0;
                    result.Ctr = row.TryGetProperty("ctr", out var ctr) ? double.Parse(ctr.GetString() ?? "0") : 0;
                }
            }
            catch { /* no spend/traffic yet on a brand-new campaign — leave zeros */ }

            return result;
        }

        /// <summary>
        /// Boosts an existing Page post: creates a PAUSED Campaign → AdSet → Ad
        /// wrapping the post. Left PAUSED on purpose — nothing spends money until
        /// the user explicitly activates it (see AdsController.Activate).
        /// </summary>
        public async Task<BoostResult> BoostPostAsync(
            string adAccountId, string pageId, string postId, decimal dailyBudget, string userToken)
        {
            var client = _httpClientFactory.CreateClient();

            // 1. Campaign
            // is_adset_budget_sharing_enabled is now required whenever the
            // campaign itself has no budget (we set daily_budget on the AdSet
            // below instead) — confirmed live ("Must specify True or False...").
            // False = no CBO/budget sharing between ad sets, matching the
            // single-ad-set boost flow here.
            var campaignBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["name"] = $"Boost - {postId}",
                ["objective"] = "OUTCOME_ENGAGEMENT",
                ["status"] = "PAUSED",
                ["special_ad_categories"] = "[]",
                ["is_adset_budget_sharing_enabled"] = "false",
                ["access_token"] = userToken
            });
            var campaignResp = await client.PostAsync($"https://graph.facebook.com/v19.0/{adAccountId}/campaigns", campaignBody);
            var campaignJson = await campaignResp.Content.ReadAsStringAsync();
            if (!campaignResp.IsSuccessStatusCode)
                throw new Exception($"Campaign creation failed: {campaignJson}");
            var campaignId = JsonDocument.Parse(campaignJson).RootElement.GetProperty("id").GetString()!;

            // 2. Ad Set — broad targeting, daily budget in minor currency units (e.g. cents)
            var adSetBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["name"] = $"Boost AdSet - {postId}",
                ["campaign_id"] = campaignId,
                ["daily_budget"] = ((int)(dailyBudget * 100)).ToString(),
                ["billing_event"] = "IMPRESSIONS",
                ["optimization_goal"] = "POST_ENGAGEMENT",
                ["bid_strategy"] = "LOWEST_COST_WITHOUT_CAP",
                ["targeting"] = JsonSerializer.Serialize(new { geo_locations = new { countries = new[] { "IN" } } }),
                ["status"] = "PAUSED",
                ["access_token"] = userToken
            });
            var adSetResp = await client.PostAsync($"https://graph.facebook.com/v19.0/{adAccountId}/adsets", adSetBody);
            var adSetJson = await adSetResp.Content.ReadAsStringAsync();
            if (!adSetResp.IsSuccessStatusCode)
                throw new Exception($"Ad Set creation failed: {adSetJson}");
            var adSetId = JsonDocument.Parse(adSetJson).RootElement.GetProperty("id").GetString()!;

            // 3. Ad — creative wraps the existing organic post
            var creative = JsonSerializer.Serialize(new { object_story_id = $"{pageId}_{postId}" });
            var adBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["name"] = $"Boost Ad - {postId}",
                ["adset_id"] = adSetId,
                ["creative"] = creative,
                ["status"] = "PAUSED",
                ["access_token"] = userToken
            });
            var adResp = await client.PostAsync($"https://graph.facebook.com/v19.0/{adAccountId}/ads", adBody);
            var adJson = await adResp.Content.ReadAsStringAsync();
            if (!adResp.IsSuccessStatusCode)
            {
                // Meta requires a payment method on the ad account before it will
                // create an Ad (even a PAUSED one) — confirmed live via
                // error_subcode 1359188 / error_user_title "No payment method".
                // There's no Marketing API endpoint to add a payment method on a
                // user's behalf (billing must happen on Meta's own domain), so
                // surface a direct link to that ad account's billing page instead
                // of a generic failure.
                if (adJson.Contains("\"error_subcode\":1359188") || adJson.Contains("No payment method"))
                {
                    var numericId = adAccountId.StartsWith("act_") ? adAccountId[4..] : adAccountId;
                    throw new PaymentMethodRequiredException(
                        $"https://www.facebook.com/ads/manager/account_settings/account_billing/?act={numericId}");
                }
                throw new Exception($"Ad creation failed: {adJson}");
            }
            var adId = JsonDocument.Parse(adJson).RootElement.GetProperty("id").GetString()!;

            return new BoostResult { CampaignId = campaignId, AdSetId = adSetId, AdId = adId };
        }

        public async Task<bool> SetCampaignStatusAsync(string campaignId, string status, string userToken)
        {
            var client = _httpClientFactory.CreateClient();
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["status"] = status,
                ["access_token"] = userToken
            });
            var resp = await client.PostAsync($"https://graph.facebook.com/v19.0/{campaignId}", body);
            return resp.IsSuccessStatusCode;
        }
    }

    public class AdAccountInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int AccountStatus { get; set; }
        public string Currency { get; set; } = "";
    }

    public class CampaignInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public string Objective { get; set; } = "";
    }

    public class CampaignInsight
    {
        public int Impressions { get; set; }
        public int Reach { get; set; }
        public decimal Spend { get; set; }
        public int Clicks { get; set; }
        public double Ctr { get; set; }
    }

    public class BoostResult
    {
        public string CampaignId { get; set; } = "";
        public string AdSetId { get; set; } = "";
        public string AdId { get; set; } = "";
    }

    /// <summary>
    /// Thrown when Meta rejects Ad creation because the ad account has no
    /// payment method. BillingUrl points straight at that account's billing
    /// settings on Meta's own site — there is no API to add a payment method
    /// on a user's behalf, so this is the closest thing to a fix-it link.
    /// </summary>
    public class PaymentMethodRequiredException : Exception
    {
        public string BillingUrl { get; }

        public PaymentMethodRequiredException(string billingUrl)
            : base("Ad account has no payment method on file.")
        {
            BillingUrl = billingUrl;
        }
    }
}
