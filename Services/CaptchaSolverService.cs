using System;
using System.Collections.Generic;
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
    /// CapSolver : https://capsolver.com (~$0.80 / 1000 captcha)
    ///   Key format: "CAP-xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
    /// </summary>
    public class CaptchaSolverService
    {
        private const string DSO_HCAPTCHA_SITEKEY = "4b68453d-2495-460d-8374-180a316104c9";
        private const string DSO_PAGE_URL = "https://www.drakensang.com/en/login";

        private readonly string _apiKey;
        private readonly HttpClient _http;
        private readonly bool _isCapSolver;

        // Debug log event
        public event Action<string> OnDebugLog;
        private void Log(string msg) => OnDebugLog?.Invoke($"[Captcha {DateTime.Now:HH:mm:ss}] {msg}");

        public CaptchaSolverService(string apiKey)
        {
            // Önündəki zibil simvolları sil (!, @, #, boşluq, etc.)
            // İstifadəçi API key-i kopyalayanda bəzən əlavə simvollar gəlir
            _apiKey = apiKey?.Trim().TrimStart('!', '@', '#', '$', '%', '^', '&', '*', ' ', '\t');
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };

            // CapSolver key'leri "CAP-" ilə başlar
            _isCapSolver = _apiKey?.StartsWith("CAP-", StringComparison.OrdinalIgnoreCase) == true;
        }

        public async Task<string> SolveHCaptchaAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                Log("API key boşdur!");
                return null;
            }

            Log($"Solver: {(_isCapSolver ? "CapSolver" : "2captcha")} | Key: {_apiKey.Substring(0, Math.Min(8, _apiKey.Length))}...");

            try
            {
                return _isCapSolver
                    ? await SolveWithCapSolver(cancellationToken)
                    : await SolveWith2Captcha(cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log($"Solver exception: {ex.Message}");
                return null;
            }
        }

        // ─── 2captcha ────────────────────────────────────────────────────────────
        // 2captcha hCaptcha üçün POST /in.php istəyir (form-data)
        private async Task<string> SolveWith2Captcha(CancellationToken ct)
        {
            // 1. Görevi POST ilə göndər
            var submitPayload = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("key",      _apiKey),
                new KeyValuePair<string, string>("method",   "hcaptcha"),
                new KeyValuePair<string, string>("sitekey",  DSO_HCAPTCHA_SITEKEY),
                new KeyValuePair<string, string>("pageurl",  DSO_PAGE_URL),
                new KeyValuePair<string, string>("json",     "1"),
            });

            Log("2captcha: Görev göndərilir (POST /in.php)...");
            HttpResponseMessage submitMsg;
            string submitResp;
            try
            {
                submitMsg  = await _http.PostAsync("https://2captcha.com/in.php", submitPayload, ct);
                submitResp = await submitMsg.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                Log($"2captcha submit error: {ex.Message}");
                return null;
            }

            Log($"2captcha submit response: {submitResp}");

            // {"status":1,"request":"CAPTCHA_ID"}
            var idMatch = Regex.Match(submitResp, "\"request\":\"(\\d+)\"");
            if (!idMatch.Success || submitResp.Contains("\"status\":0"))
            {
                Log($"2captcha submit failed: {submitResp}");
                return null;
            }

            string captchaId = idMatch.Groups[1].Value;
            Log($"2captcha task ID: {captchaId} — poll başlayır...");

            // 2. Çözümü gözlə (max 2 dəqiqə, 5s aralıqla)
            for (int i = 0; i < 24; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(5000, ct);

                string resultUrl = $"https://2captcha.com/res.php?key={_apiKey}&action=get&id={captchaId}&json=1";
                string resultResp;
                try { resultResp = await _http.GetStringAsync(resultUrl); }
                catch (Exception ex) { Log($"2captcha poll error: {ex.Message}"); continue; }

                Log($"2captcha poll [{i+1}/24]: {resultResp}");

                if (resultResp.Contains("NOT_READY") || resultResp.Contains("CAPCHA_NOT_READY"))
                    continue;

                if (resultResp.Contains("ERROR_CAPTCHA_UNSOLVABLE"))
                {
                    // Bu xəta transient ola bilər — yenidən submit et (max 2 dəfə)
                    Log($"2captcha UNSOLVABLE — yenidən submit edilir...");
                    var retryPayload = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("key",      _apiKey),
                        new KeyValuePair<string, string>("method",   "hcaptcha"),
                        new KeyValuePair<string, string>("sitekey",  DSO_HCAPTCHA_SITEKEY),
                        new KeyValuePair<string, string>("pageurl",  DSO_PAGE_URL),
                        new KeyValuePair<string, string>("json",     "1"),
                        new KeyValuePair<string, string>("soft_id",  "0"),
                    });
                    try
                    {
                        var retryMsg  = await _http.PostAsync("https://2captcha.com/in.php", retryPayload, ct);
                        var retryBody = await retryMsg.Content.ReadAsStringAsync();
                        Log($"2captcha retry submit: {retryBody}");
                        var retryId = Regex.Match(retryBody, "\"request\":\"(\\d+)\"");
                        if (retryId.Success && !retryBody.Contains("\"status\":0"))
                        {
                            captchaId = retryId.Groups[1].Value;
                            Log($"2captcha retry task ID: {captchaId}");
                            continue; // yeni ID ilə poll davam edir
                        }
                    }
                    catch { }
                    Log("2captcha retry submit da uğursuz — null qaytarılır.");
                    return null;
                }

                if (resultResp.Contains("\"status\":1"))
                {
                    var tokenMatch = Regex.Match(resultResp, "\"request\":\"([^\"]+)\"");
                    if (tokenMatch.Success)
                    {
                        Log($"2captcha token alındı! (uzunluq={tokenMatch.Groups[1].Value.Length})");
                        return tokenMatch.Groups[1].Value;
                    }
                }

                Log($"2captcha xəta cavabı: {resultResp}");
                return null;
            }

            Log("2captcha timeout — 2 dəqiqə ərzində token alınmadı.");
            return null;
        }

        // ─── CapSolver ───────────────────────────────────────────────────────────
        private async Task<string> SolveWithCapSolver(CancellationToken ct)
        {
            // 1. Görevi göndər
            string createJson = $@"{{
  ""clientKey"": ""{_apiKey}"",
  ""task"": {{
    ""type"": ""HCaptchaTaskProxyLess"",
    ""websiteURL"": ""{DSO_PAGE_URL}"",
    ""websiteKey"": ""{DSO_HCAPTCHA_SITEKEY}""
  }}
}}";
            Log("CapSolver: Görev göndərilir...");
            HttpResponseMessage createMsg;
            string createBody;
            try
            {
                createMsg  = await _http.PostAsync(
                    "https://api.capsolver.com/createTask",
                    new StringContent(createJson, Encoding.UTF8, "application/json"), ct);
                createBody = await createMsg.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                Log($"CapSolver createTask error: {ex.Message}");
                return null;
            }

            Log($"CapSolver createTask response: {createBody}");

            // {"errorId":0,"taskId":"..."}
            var taskIdMatch = Regex.Match(createBody, "\"taskId\":\"([^\"]+)\"");
            if (!taskIdMatch.Success || createBody.Contains("\"errorId\":1"))
            {
                Log($"CapSolver createTask failed: {createBody}");
                return null;
            }

            string taskId = taskIdMatch.Groups[1].Value;
            Log($"CapSolver task ID: {taskId}");

            // 2. Çözümü gözlə
            string getTaskJson = $@"{{""clientKey"":""{_apiKey}"",""taskId"":""{taskId}""}}";

            for (int i = 0; i < 24; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(5000, ct);

                HttpResponseMessage getMsg;
                string getBody;
                try
                {
                    getMsg  = await _http.PostAsync(
                        "https://api.capsolver.com/getTaskResult",
                        new StringContent(getTaskJson, Encoding.UTF8, "application/json"), ct);
                    getBody = await getMsg.Content.ReadAsStringAsync();
                }
                catch (Exception ex) { Log($"CapSolver poll error: {ex.Message}"); continue; }

                Log($"CapSolver poll [{i+1}/24]: {getBody}");

                if (getBody.Contains("\"status\":\"processing\"") ||
                    getBody.Contains("\"status\":\"idle\""))
                    continue;

                if (getBody.Contains("\"status\":\"ready\""))
                {
                    // CapSolver cavabı: {"solution":{"gRecaptchaResponse":"TOKEN..."}}
                    // gRecaptchaResponse field-i solution obyekti içindədir
                    var tokenMatch = Regex.Match(getBody, "\"gRecaptchaResponse\"\\s*:\\s*\"([^\"]+)\"");
                    if (!tokenMatch.Success)
                        tokenMatch = Regex.Match(getBody, "\"token\"\\s*:\\s*\"([^\"]+)\"");
                    if (!tokenMatch.Success)
                        tokenMatch = Regex.Match(getBody, "\"userAgent\"\\s*:\\s*\"([^\"]+)\""); // fallback axtarış

                    if (tokenMatch.Success)
                    {
                        // "userAgent" false-positive ola bilər, yoxla
                        string candidate = tokenMatch.Groups[1].Value;
                        if (candidate.Length > 20) // real token çox uzundur
                        {
                            Log($"CapSolver token alındı! (uzunluq={candidate.Length})");
                            return candidate;
                        }
                    }

                    Log($"CapSolver ready amma token tapılmadı: {getBody}");
                    return null;
                }

                Log($"CapSolver xəta cavabı: {getBody}");
                return null;
            }

            Log("CapSolver timeout — 2 dəqiqə ərzində token alınmadı.");
            return null;
        }

        public static bool IsConfigured(string apiKey) =>
            !string.IsNullOrWhiteSpace(apiKey);
    }
}
