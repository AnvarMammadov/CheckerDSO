using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CheckerDSO.Services
{
    /// <summary>
    /// 2captcha API ile hCaptcha çözümü
    /// API Key: https://2captcha.com/enterpage adresinden alınır
    /// Fiyat: ~$1 per 1000 captcha
    /// </summary>
    public class CaptchaSolverService
    {
        // DSO hCaptcha sitekey (sabit)
        private const string DSO_HCAPTCHA_SITEKEY = "99550723-f743-4b9c-9e8a-fbd48ca3e66e";
        private const string DSO_PAGE_URL = "https://www.drakensang.com/en/login";

        private readonly string _apiKey;
        private readonly HttpClient _httpClient;

        public CaptchaSolverService(string apiKey)
        {
            _apiKey = apiKey;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(120);
        }

        /// <summary>
        /// hCaptcha'yı 2captcha ile çözer ve token döndürür
        /// </summary>
        public async Task<string> SolveHCaptchaAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
                return null;

            try
            {
                // 1. Adım: 2captcha'ya çözüm isteği gönder
                var submitUrl = $"https://2captcha.com/in.php?key={_apiKey}&method=hcaptcha&sitekey={DSO_HCAPTCHA_SITEKEY}&pageurl={Uri.EscapeDataString(DSO_PAGE_URL)}&json=1";
                var submitResp = await _httpClient.GetStringAsync(submitUrl);

                // Yanıt format: {"status":1,"request":"CAPTCHA_ID"}
                var idMatch = System.Text.RegularExpressions.Regex.Match(submitResp, "\"request\":\"(\\d+)\"");
                if (!idMatch.Success || submitResp.Contains("\"status\":0"))
                    return null;

                string captchaId = idMatch.Groups[1].Value;

                // 2. Adım: Çözümü bekle (maksimum 2 dakika)
                for (int i = 0; i < 24; i++) // 24 * 5 saniye = 120 saniye
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(5000, cancellationToken); // 5 saniye bekle

                    var resultUrl = $"https://2captcha.com/res.php?key={_apiKey}&action=get&id={captchaId}&json=1";
                    var resultResp = await _httpClient.GetStringAsync(resultUrl);

                    if (resultResp.Contains("CAPCHA_NOT_READY") || resultResp.Contains("NOT_READY"))
                        continue; // Henüz hazır değil, bekle

                    var tokenMatch = System.Text.RegularExpressions.Regex.Match(resultResp, "\"request\":\"([^\"]+)\"");
                    if (tokenMatch.Success && resultResp.Contains("\"status\":1"))
                        return tokenMatch.Groups[1].Value; // Çözüm token'i

                    return null; // Hata
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            return null; // Timeout
        }

        public static bool IsConfigured(string apiKey) =>
            !string.IsNullOrWhiteSpace(apiKey);
    }
}
