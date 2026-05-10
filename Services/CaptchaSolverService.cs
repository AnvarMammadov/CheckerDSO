using System;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CheckerDSO.Services
{
    /// <summary>
    /// hCaptcha çözümü — 2captcha veya CapSolver destekler
    /// 
    /// 2captcha  : https://2captcha.com  (~$1.00 / 1000 captcha)
    ///   Key format: sayısal string "1abc2def..."
    /// 
    /// CapSolver : https://capsolver.com (~$0.80 / 1000 captcha, ücretsiz deneme kredisi)
    ///   Key format: "CAP-xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
    /// </summary>
    public class CaptchaSolverService
    {
        private const string DSO_HCAPTCHA_SITEKEY = "4b68453d-2495-460d-8374-180a316104c9";
        private const string DSO_PAGE_URL = "https://www.drakensang.com/en/login";

        private readonly string _apiKey;
        private readonly HttpClient _http;
        private readonly bool _isCapSolver;

        public CaptchaSolverService(string apiKey)
        {
            _apiKey = apiKey;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };

            // CapSolver key'leri "CAP-" ile başlar
            _isCapSolver = apiKey?.StartsWith("CAP-", StringComparison.OrdinalIgnoreCase) == true;
        }

        public async Task<string> SolveHCaptchaAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey)) return null;

            try
            {
                return _isCapSolver
                    ? await SolveWithCapSolver(cancellationToken)
                    : await SolveWith2Captcha(cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        // ─── 2captcha ───────────────────────────────────────────────────────────
        private async Task<string> SolveWith2Captcha(CancellationToken ct)
        {
            // 1. Görevi gönder
            string submitUrl = $"https://2captcha.com/in.php" +
                $"?key={_apiKey}&method=hcaptcha" +
                $"&sitekey={DSO_HCAPTCHA_SITEKEY}" +
                $"&pageurl={Uri.EscapeDataString(DSO_PAGE_URL)}&json=1";

            string submitResp = await _http.GetStringAsync(submitUrl);

            // {"status":1,"request":"CAPTCHA_ID"}
            var idMatch = Regex.Match(submitResp, "\"request\":\"(\\d+)\"");
            if (!idMatch.Success || submitResp.Contains("\"status\":0"))
                return null;

            string captchaId = idMatch.Groups[1].Value;

            // 2. Çözümü bekle (max 2 dakika)
            for (int i = 0; i < 24; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(5000, ct);

                string resultUrl = $"https://2captcha.com/res.php?key={_apiKey}&action=get&id={captchaId}&json=1";
                string resultResp = await _http.GetStringAsync(resultUrl);

                if (resultResp.Contains("NOT_READY") || resultResp.Contains("CAPCHA_NOT_READY"))
                    continue;

                var tokenMatch = Regex.Match(resultResp, "\"request\":\"([^\"]+)\"");
                if (tokenMatch.Success && resultResp.Contains("\"status\":1"))
                    return tokenMatch.Groups[1].Value;

                return null; // hata
            }

            return null; // timeout
        }

        // ─── CapSolver ──────────────────────────────────────────────────────────
        private async Task<string> SolveWithCapSolver(CancellationToken ct)
        {
            // 1. Görevi gönder
            string createJson = $@"{{
  ""clientKey"": ""{_apiKey}"",
  ""task"": {{
    ""type"": ""HCaptchaTaskProxyLess"",
    ""websiteURL"": ""{DSO_PAGE_URL}"",
    ""websiteKey"": ""{DSO_HCAPTCHA_SITEKEY}""
  }}
}}";
            var createResp = await _http.PostAsync(
                "https://api.capsolver.com/createTask",
                new StringContent(createJson, Encoding.UTF8, "application/json"));
            string createBody = await createResp.Content.ReadAsStringAsync();

            // {"errorId":0,"taskId":"..."}
            var taskIdMatch = Regex.Match(createBody, "\"taskId\":\"([^\"]+)\"");
            if (!taskIdMatch.Success || createBody.Contains("\"errorId\":1"))
                return null;

            string taskId = taskIdMatch.Groups[1].Value;

            // 2. Çözümü bekle
            string getTaskJson = $@"{{""clientKey"":""{_apiKey}"",""taskId"":""{taskId}""}}";

            for (int i = 0; i < 24; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(5000, ct);

                var getResp = await _http.PostAsync(
                    "https://api.capsolver.com/getTaskResult",
                    new StringContent(getTaskJson, Encoding.UTF8, "application/json"));
                string getBody = await getResp.Content.ReadAsStringAsync();

                if (getBody.Contains("\"status\":\"processing\"") ||
                    getBody.Contains("\"status\":\"idle\""))
                    continue;

                if (getBody.Contains("\"status\":\"ready\""))
                {
                    var tokenMatch = Regex.Match(getBody, "\"gRecaptchaResponse\":\"([^\"]+)\"");
                    if (!tokenMatch.Success)
                        tokenMatch = Regex.Match(getBody, "\"token\":\"([^\"]+)\"");
                    if (tokenMatch.Success)
                        return tokenMatch.Groups[1].Value;
                }

                return null; // hata
            }

            return null; // timeout
        }

        public static bool IsConfigured(string apiKey) =>
            !string.IsNullOrWhiteSpace(apiKey);
    }
}
